using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
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
    public record WritePrescriptions(PrescriptionLine[]? Items);
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

            var lines = req.Items ?? [];
            var errors = Validate(lines);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);
            var already = await db.Prescriptions.AsNoTracking().Where(p => p.EncounterId == id && p.Status != PrescriptionStatus.Cancelled && p.Status != PrescriptionStatus.Failed)
                .Select(p => p.Din).ToListAsync();
            if (lines.FirstOrDefault(l => already.Contains(l.Din)) is { } twice)
                return Results.Problem($"{PrescriptionCatalog.Find(twice.Din)!.Display} is already prescribed at this visit.", statusCode: StatusCodes.Status409Conflict);

            var now = clock.GetUtcNow().UtcDateTime;
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
                        Status = PrescriptionStatus.Draft, CreatedUtc = now, OutboxMessageId = message.Id,
                    };
                }).ToList();
                db.Prescriptions.AddRange(written);
                await db.SaveChangesAsync();
                // The drugs written together share a number, the way an order's tests share a requisition.
                foreach (var p in written)
                    p.GroupNumber = written.Min(x => x.Id);
                message.PayloadJson = FhirPrescriptionBundle.Build(clinic, encounter.Patient, prescriber, written, now).ToJsonString();
                db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = encounter.PatientId,
                    Action = AccessActions.Edit, Context = $"Prescribed {written.Count} from encounter {id}", IpAddress = AuthEndpoints.Ip(http) });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
            return Results.Ok(await List(db, id));
        }).WithTags("Prescriptions").RequireAuthorization(Permissions.PrescriptionsWrite).AddEndpointFilter(ConsentEndpoints.EncounterFilter);

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
                            message.PayloadJson = FhirPrescriptionBundle.Build(clinic, rx.Patient, rx.PrescriberProvider, rest, now).ToJsonString();
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
