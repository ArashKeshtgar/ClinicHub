using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// Electronic prescriptions from a visit, to the pharmacy (PharmaFlow) as FHIR MedicationRequests. Writing one saves it
/// with its outgoing message in one transaction; the outbox worker delivers the message. The prescriber is the signed-in
/// clinician: a physician or nurse practitioner with a provider record and a college registration number.
/// </summary>
public static class PrescriptionEndpoints
{
    public const int MaxLines = 10;

    public record DrugDto(string Din, string Display, string Unit, string Group, bool Connected);
    public record PrescriptionLine(string Din, string? Directions, int Quantity, int? DaysSupply, int Repeats, bool NoSubstitution, string? Note);
    public record AlertOverride(string Key, string? Reason);
    public record WritePrescriptions(PrescriptionLine[]? Items, AlertOverride[]? Overrides);
    public record RenewPrescription(int EncounterId, int? Quantity, int? DaysSupply, int? Repeats, string? Directions, AlertOverride[]? Overrides);
    public record MedicationDto(int Id, string Din, string Display, string Directions, int Quantity, string Unit, int? DaysSupply, int Repeats, string Status,
        DateTime CreatedUtc, string Prescriber, DateTime RunsOutUtc, bool Current, bool CanRenew, int? RenewedBy);
    public record MedicationList(MedicationDto[] Items, bool SomeWithheld);
    public record CancelPrescription(string? Reason);
    public record PrescriptionDto(int Id, string Din, string Display, string Directions, int Quantity, string Unit, int? DaysSupply, int Repeats,
        bool NoSubstitution, string? Note, string Status, DateTime CreatedUtc, DateTime? SentUtc, string? ExternalId, string Prescriber,
        int Attempts, string? LastError, string? CancelReason, string? StatusNote);

    public static void MapPrescriptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/prescriptions/catalog", (IOptionsMonitor<IntegrationOptions> integrations) =>
            PrescriptionCatalog.All.Select(d => new DrugDto(d.Din, d.Display, d.Unit, d.Group,
                !string.IsNullOrWhiteSpace(integrations.CurrentValue.PharmaFlow.FhirBaseUrl))))
            .WithTags("Prescriptions").RequireAuthorization(Permissions.EncountersView);

        app.MapGet("/api/encounters/{id:int}/prescriptions", async (int id, ClinicHubDbContext db) => await List(db, id))
            .WithTags("Prescriptions").RequireAuthorization(Permissions.EncountersView).AddEndpointFilter(ConsentEndpoints.EncounterFilter);

        app.MapPost("/api/encounters/{id:int}/prescriptions", async (int id, WritePrescriptions req, ClaimsPrincipal user, ClinicHubDbContext db,
            CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
            await WriteAsync(id, req.Items ?? [], req.Overrides, null, user, db, catalog, clock, http))
            .WithTags("Prescriptions").RequireAuthorization(Permissions.PrescriptionsWrite).AddEndpointFilter(ConsentEndpoints.EncounterFilter);

        // What would be said before this goes out: allergies, interactions, duplicates. The page asks while the prescriber types.
        app.MapPost("/api/encounters/{id:int}/prescriptions/check", async (int id, WritePrescriptions req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var encounter = await db.Encounters.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
            if (encounter is null)
                return Results.NotFound();
            var dins = (req.Items ?? []).Select(l => l.Din).Where(d => d is not null && PrescriptionCatalog.Find(d) is not null).Select(d => d!).Distinct().ToList();
            var (alerts, partial, recorded) = await Alerts(db, encounter.PatientId, dins, user, clock.GetUtcNow().UtcDateTime);
            return Results.Ok(new CheckResult(alerts, partial, recorded));
        }).WithTags("Prescriptions").RequireAuthorization(Permissions.PrescriptionsWrite).AddEndpointFilter(ConsentEndpoints.EncounterFilter);

        // The patient's medicines as ClinicHub knows them (its own prescriptions), what is running out and what can be renewed.
        app.MapGet("/api/patients/{id:int}/medications", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            if (!await db.Patients.AnyAsync(p => p.Id == id))
                return Results.NotFound();
            var now = clock.GetUtcNow().UtcDateTime;
            var rows = await db.Prescriptions.AsNoTracking().Include(p => p.PrescriberProvider).Include(p => p.Encounter)
                .Where(p => p.PatientId == id && p.Status != PrescriptionStatus.Cancelled && p.Status != PrescriptionStatus.Failed).OrderByDescending(p => p.Id).ToListAsync();
            var consent = await ConsentView.LoadAsync(db, id, user, now);
            var shown = rows.Where(r => !consent.Decide(r.EncounterId, r.Encounter.ProviderId, r.Encounter.Status).Withheld).ToList();
            var renewedBy = (await db.Prescriptions.AsNoTracking().Where(p => p.PatientId == id && p.RenewsPrescriptionId != null
                && p.Status != PrescriptionStatus.Cancelled && p.Status != PrescriptionStatus.Failed).Select(p => new { p.Id, Of = p.RenewsPrescriptionId!.Value }).ToListAsync())
                .GroupBy(x => x.Of).ToDictionary(g => g.Key, g => g.Max(x => x.Id));
            return Results.Ok(new MedicationList(shown.Select(p => new MedicationDto(p.Id, p.Din, p.Display, p.Directions, p.Quantity, p.QuantityUnit, p.DaysSupply, p.Repeats,
                p.Status, p.CreatedUtc, p.PrescriberProvider.LastName, RunsOut(p), DrugSafety.IsCurrent(p, now),
                p.Status == PrescriptionStatus.Accepted && !renewedBy.ContainsKey(p.Id) && PrescriptionCatalog.Find(p.Din) is not null,
                renewedBy.TryGetValue(p.Id, out var by) ? by : null)).ToArray(), shown.Count < rows.Count));
        }).WithTags("Prescriptions").RequireAuthorization(Permissions.EncountersView);

        // A renewal: the same drug written again at a visit, with the same checks, linked to the one it renews.
        app.MapPost("/api/prescriptions/{id:int}/renew", async (int id, RenewPrescription req, ClaimsPrincipal user, ClinicHubDbContext db,
            CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            var old = await db.Prescriptions.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
            if (old is null)
                return Results.NotFound();
            var encounter = await db.Encounters.AsNoTracking().SingleOrDefaultAsync(e => e.Id == req.EncounterId);
            if (encounter is null)
                return Results.NotFound();
            // The same checks as writing: this visit's consent decision applies to it, and so does the renewed prescription's.
            var now = clock.GetUtcNow().UtcDateTime;
            var consent = await ConsentView.LoadAsync(db, encounter.PatientId, user, now);
            if (consent.Decide(encounter.Id, encounter.ProviderId, encounter.Status).Withheld)
                return ConsentEndpoints.Withheld(user, encounter.PatientId, "", "");
            if (old.PatientId != encounter.PatientId)
                return Results.Problem("That prescription is another patient's.", statusCode: StatusCodes.Status409Conflict);
            var visit = await db.Encounters.AsNoTracking().SingleAsync(e => e.Id == old.EncounterId);
            if (consent.Decide(visit.Id, visit.ProviderId, visit.Status).Withheld)
                return Results.Problem("The visit it was written at is withheld at the patient's request.", statusCode: StatusCodes.Status403Forbidden);
            if (old.Status != PrescriptionStatus.Accepted)
                return Results.Problem(old.Status == PrescriptionStatus.Draft ? "It has not reached the pharmacy yet." : $"A {old.Status.ToLowerInvariant()} prescription is not renewed.",
                    statusCode: StatusCodes.Status409Conflict);
            if (await db.Prescriptions.AnyAsync(p => p.RenewsPrescriptionId == id && p.Status != PrescriptionStatus.Cancelled && p.Status != PrescriptionStatus.Failed))
                return Results.Problem("It was already renewed.", statusCode: StatusCodes.Status409Conflict);
            if (PrescriptionCatalog.Find(old.Din) is null)
                return Results.Problem("This drug is no longer in the catalog.", statusCode: StatusCodes.Status409Conflict);

            var line = new PrescriptionLine(old.Din, string.IsNullOrWhiteSpace(req.Directions) ? old.Directions : req.Directions, req.Quantity ?? old.Quantity,
                req.DaysSupply ?? old.DaysSupply, req.Repeats ?? old.Repeats, old.NoSubstitution,
                $"Renewal of prescription {old.Id}" + (old.Note is { } n ? $". {n}" : ""));
            return await WriteAsync(req.EncounterId, [line], req.Overrides, id, user, db, catalog, clock, http);
        }).WithTags("Prescriptions").RequireAuthorization(Permissions.PrescriptionsWrite);

        // Withdrawing a prescription. Not delivered yet: it is taken out of its message here. Delivered: the pharmacy is
        // asked, and it decides (once dispensing has started it says no).
        app.MapPost("/api/prescriptions/{id:int}/cancel", async (int id, CancelPrescription req, ClaimsPrincipal user, ClinicHubDbContext db,
            CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            var rx = await db.Prescriptions.Include(p => p.Patient).Include(p => p.PrescriberProvider).SingleOrDefaultAsync(p => p.Id == id);
            if (rx is null)
                return Results.NotFound();
            var refusal = rx.Status switch
            {
                PrescriptionStatus.Cancelled => "This prescription is already cancelled.",
                PrescriptionStatus.Cancelling => "This prescription is already being cancelled.",
                _ => null,
            };
            if (refusal is not null)
                return Results.Problem(refusal, statusCode: StatusCodes.Status409Conflict);
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
            if (reason?.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["At most 200 characters."] });

            var now = clock.GetUtcNow().UtcDateTime;
            var clinic = await catalog.Clinics.AsNoTracking().SingleAsync(c => c.Id == user.RequiredClinicId());
            var message = rx.OutboxMessageId is { } m ? await db.Outbox.SingleOrDefaultAsync(x => x.Id == m) : null;
            rx.CancelReason = reason;
            rx.StatusNote = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                if (message is null || message.SentUtc is null)
                {
                    // Never reached the pharmacy: nothing to ask. The message goes on with the others, or not at all.
                    rx.Status = PrescriptionStatus.Cancelled;
                    rx.CancelledUtc = now;
                    rx.OutboxMessageId = null;
                    if (message is not null)
                    {
                        var rest = await db.Prescriptions.Where(p => p.OutboxMessageId == message.Id && p.Id != rx.Id).OrderBy(p => p.Id).ToListAsync();
                        if (rest.Count == 0)
                        {
                            await db.SaveChangesAsync();
                            db.Outbox.Remove(message);
                        }
                        else
                        {
                            var label = await ConfidentialityLabels.ForAsync(db, rx.PatientId, rx.EncounterId);
                            message.PayloadJson = FhirPrescriptionBundle.Build(clinic, rx.Patient, rx.PrescriberProvider, rest, now, label).ToJsonString();
                        }
                    }
                }
                else
                {
                    rx.Status = PrescriptionStatus.Cancelling;
                    db.Outbox.Add(new OutboxMessage
                    {
                        Destination = TargetSystems.PharmaFlow, Kind = OutboxKinds.PrescriptionCancel, PrescriptionId = rx.Id, CreatedUtc = now,
                        PayloadJson = FhirPrescriptionBundle.Cancel(clinic, rx.Patient, rx, now).ToJsonString(),
                    });
                }
                db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = rx.PatientId,
                    Action = AccessActions.Edit, Context = $"Cancelled prescription {rx.Id}", IpAddress = AuthEndpoints.Ip(http) });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
            return Results.Ok(await List(db, rx.EncounterId));
        }).WithTags("Prescriptions").RequireAuthorization(Permissions.PrescriptionsWrite);

        // After a failure the prescription can be tried again, e.g. once the pharmacy is back.
        app.MapPost("/api/prescriptions/{id:int}/resend", async (int id, ClinicHubDbContext db) =>
        {
            var rx = await db.Prescriptions.SingleOrDefaultAsync(p => p.Id == id);
            var message = rx?.OutboxMessageId is { } m ? await db.Outbox.SingleOrDefaultAsync(x => x.Id == m) : null;
            if (rx is null || message is null)
                return Results.NotFound();
            if (message.SentUtc is not null)
                return Results.Problem("This prescription was already delivered.", statusCode: StatusCodes.Status409Conflict);
            message.Attempts = 0;
            message.NextAttemptUtc = null;
            foreach (var p in await db.Prescriptions.Where(p => p.OutboxMessageId == message.Id).ToListAsync())
                p.Status = PrescriptionStatus.Draft;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).WithTags("Prescriptions").RequireAuthorization(Permissions.PrescriptionsWrite);
    }

    private static async Task<IResult> WriteAsync(int id, PrescriptionLine[] lines, AlertOverride[]? overrides, int? renews, ClaimsPrincipal user,
        ClinicHubDbContext db, CatalogDbContext catalog, TimeProvider clock, HttpContext http)
    {
        var encounter = await db.Encounters.Include(e => e.Patient).SingleOrDefaultAsync(e => e.Id == id);
        if (encounter is null)
            return Results.NotFound();
        if (encounter.Status == EncounterStatus.Voided)
            return Results.Problem("This visit was voided.", statusCode: StatusCodes.Status409Conflict);
        // A prescription is signed by writing it: the prescriber must be a clinician of this clinic who may prescribe.
        if (!int.TryParse(user.FindFirstValue(AppClaims.ProviderId), out var providerId))
            return Results.Problem("Only a clinician with a provider record at this clinic can prescribe.", statusCode: StatusCodes.Status403Forbidden);
        var prescriber = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId);
        if (prescriber.Kind is not (ProviderKinds.Physician or ProviderKinds.NursePractitioner))
            return Results.Problem("Only physicians and nurse practitioners prescribe.", statusCode: StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(prescriber.RegistrationNumber))
            return Results.Problem("Your provider record has no college registration number, which a prescription needs. Ask an administrator to add it.",
                statusCode: StatusCodes.Status409Conflict);

        var errors = Validate(lines);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);
        var already = await db.Prescriptions.AsNoTracking().Where(p => p.EncounterId == id && p.Status != PrescriptionStatus.Cancelled && p.Status != PrescriptionStatus.Failed)
            .Select(p => p.Din).ToListAsync();
        if (lines.FirstOrDefault(l => already.Contains(l.Din)) is { } twice)
            return Results.Problem($"{PrescriptionCatalog.Find(twice.Din)!.Display} is already prescribed at this visit.", statusCode: StatusCodes.Status409Conflict);

        var now = clock.GetUtcNow().UtcDateTime;
        // Allergies, interactions and duplicates: each is either stopped here or gone ahead of with a reason, which is kept.
        var (alerts, _, _) = await Alerts(db, encounter.PatientId, lines.Select(l => l.Din!).ToList(), user, now);
        var unanswered = alerts.Where(a => !Answered(a, overrides)).ToList();
        if (unanswered.Count > 0)
            return Results.Problem("Allergy, interaction or duplicate alerts need an answer before this is sent.", statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["alerts"] = alerts, ["unanswered"] = unanswered.Select(a => a.Key).ToArray() });
        // What was gone ahead of is kept with the prescription it concerns.
        string? AlertNoteFor(string din)
        {
            var mine = alerts.Where(a => a.Dins.Contains(din)).ToList();
            if (mine.Count == 0)
                return null;
            var text = string.Join("\n", mine.Select(a =>
                $"{a.Severity} {a.Kind.ToLowerInvariant()}: {a.Message} Reason: {overrides!.First(o => o.Key == a.Key).Reason?.Trim() ?? "acknowledged"}"));
            return text.Length > 1000 ? text[..1000] : text;
        }

        var clinic = await catalog.Clinics.AsNoTracking().SingleAsync(c => c.Id == user.RequiredClinicId());
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();
            var message = new OutboxMessage { Destination = TargetSystems.PharmaFlow, Kind = OutboxKinds.Prescription, CreatedUtc = now };
            db.Outbox.Add(message);
            await db.SaveChangesAsync();
            var written = lines.Select(l =>
            {
                var drug = PrescriptionCatalog.Find(l.Din)!;
                return new Prescription
                {
                    EncounterId = id, PatientId = encounter.PatientId, PrescriberProviderId = providerId, Din = drug.Din, Display = drug.Display,
                    Directions = l.Directions!.Trim(), Quantity = l.Quantity, QuantityUnit = drug.Unit, DaysSupply = l.DaysSupply, Repeats = l.Repeats,
                    NoSubstitution = l.NoSubstitution, Note = string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim(),
                    Status = PrescriptionStatus.Draft, CreatedUtc = now, OutboxMessageId = message.Id, AlertNote = AlertNoteFor(drug.Din), RenewsPrescriptionId = renews,
                };
            }).ToList();
            db.Prescriptions.AddRange(written);
            await db.SaveChangesAsync();
            // The drugs written together share a number, the way an order's tests share a requisition.
            foreach (var p in written)
                p.GroupNumber = written.Min(x => x.Id);
            var label = await ConfidentialityLabels.ForAsync(db, encounter.PatientId, encounter.Id);
            message.PayloadJson = FhirPrescriptionBundle.Build(clinic, encounter.Patient, prescriber, written, now, label).ToJsonString();
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = encounter.PatientId,
                Action = AccessActions.Edit, Context = (renews is null ? $"Prescribed {written.Count} from encounter {id}" : $"Renewed prescription {renews} at encounter {id}"), IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        return Results.Ok(await List(db, id));
    }

    public record CheckResult(List<DrugSafety.Alert> Alerts, bool SomeWithheld, bool AllergiesRecorded);

    /// <summary>
    /// The alerts for these drugs for this patient: against the recorded allergies and the medicines being taken. Medicines
    /// from visits the user may not see are left out (the result says so), so that an alert cannot give them away.
    /// </summary>
    private static async Task<(List<DrugSafety.Alert> Alerts, bool SomeWithheld, bool AllergiesRecorded)> Alerts(
        ClinicHubDbContext db, int patientId, IReadOnlyList<string> dins, ClaimsPrincipal user, DateTime now)
    {
        var allergies = await db.Allergies.AsNoTracking().Where(a => a.PatientId == patientId && a.EndedUtc == null).ToListAsync();
        var earlier = await db.Prescriptions.AsNoTracking().Include(p => p.Encounter).Where(p => p.PatientId == patientId).ToListAsync();
        var consent = await ConsentView.LoadAsync(db, patientId, user, now);
        var visible = earlier.Where(p => !consent.Decide(p.EncounterId, p.Encounter.ProviderId, p.Encounter.Status).Withheld).ToList();
        var current = visible.Where(p => DrugSafety.IsCurrent(p, now)).Select(p => new DrugSafety.Current(p.Din, p.Display, p.CreatedUtc)).ToList();
        var alerts = DrugSafety.Check(dins, allergies.Where(a => !a.NoKnown).ToList(), current);
        return (alerts, visible.Count < earlier.Count, allergies.Count > 0);
    }

    /// <summary>An alert is answered when the request says so: with a reason of 10 characters or more for severe and major ones.</summary>
    private static bool Answered(DrugSafety.Alert alert, AlertOverride[]? overrides) =>
        overrides?.FirstOrDefault(o => o.Key == alert.Key) is { } o
        && (!DrugSafety.Severities.NeedsReason(alert.Severity) || (o.Reason?.Trim().Length ?? 0) >= 10);

    /// <summary>When the supply of a prescription ends: its days of supply times its fills, 30 days a fill when it does not say.</summary>
    private static DateTime RunsOut(Prescription p) => p.CreatedUtc.AddDays((p.DaysSupply ?? 30) * (p.Repeats + 1));

    /// <summary>What a prescription line must be before anything is saved. Keys are the line and the field, e.g. "items[0].quantity".</summary>
    public static Dictionary<string, string[]> Validate(PrescriptionLine[] lines)
    {
        var errors = new Dictionary<string, string[]>();
        if (lines.Length == 0)
            errors["items"] = ["Choose at least one drug."];
        else if (lines.Length > MaxLines)
            errors["items"] = [$"At most {MaxLines} drugs at a time."];
        for (var i = 0; i < Math.Min(lines.Length, MaxLines); i++)
        {
            var l = lines[i];
            if (PrescriptionCatalog.Find(l.Din ?? "") is null)
                errors[$"items[{i}].din"] = ["Unknown drug."];
            var directions = l.Directions?.Trim() ?? "";
            if (directions.Length < 3)
                errors[$"items[{i}].directions"] = ["Write the directions (how much, how often, for how long)."];
            else if (directions.Length > 200)
                errors[$"items[{i}].directions"] = ["At most 200 characters."];
            if (l.Quantity is < 1 or > 9999)
                errors[$"items[{i}].quantity"] = ["Between 1 and 9999."];
            if (l.DaysSupply is < 1 or > 365)
                errors[$"items[{i}].daysSupply"] = ["Between 1 and 365 days, or leave it empty."];
            if (l.Repeats is < 0 or > 11)
                errors[$"items[{i}].repeats"] = ["Between 0 and 11 refills."];
            if (l.Note?.Trim().Length > 500)
                errors[$"items[{i}].note"] = ["At most 500 characters."];
        }
        var twice = lines.Where(l => l.Din is not null).GroupBy(l => l.Din).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            errors["items"] = [$"{PrescriptionCatalog.Find(twice.Key)?.Display ?? twice.Key} is on the list twice."];
        return errors;
    }

    private static async Task<PrescriptionDto[]> List(ClinicHubDbContext db, int encounterId) =>
        await db.Prescriptions.AsNoTracking().Where(p => p.EncounterId == encounterId).OrderByDescending(p => p.Id)
            .Select(p => new PrescriptionDto(p.Id, p.Din, p.Display, p.Directions, p.Quantity, p.QuantityUnit, p.DaysSupply, p.Repeats, p.NoSubstitution, p.Note,
                p.Status, p.CreatedUtc, p.SentUtc, p.ExternalId, p.PrescriberProvider.LastName,
                // The message in flight: the cancellation while one is being sent, otherwise the prescription itself.
                db.Outbox.Where(m => p.Status == PrescriptionStatus.Cancelling ? m.Kind == OutboxKinds.PrescriptionCancel && m.PrescriptionId == p.Id : m.Id == p.OutboxMessageId)
                    .OrderByDescending(m => m.Id).Select(m => m.Attempts).FirstOrDefault(),
                db.Outbox.Where(m => p.Status == PrescriptionStatus.Cancelling ? m.Kind == OutboxKinds.PrescriptionCancel && m.PrescriptionId == p.Id : m.Id == p.OutboxMessageId)
                    .OrderByDescending(m => m.Id).Select(m => m.LastError).FirstOrDefault(),
                p.CancelReason, p.StatusNote))
            .ToArrayAsync();
}
