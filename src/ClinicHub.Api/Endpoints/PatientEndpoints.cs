using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using static ClinicHub.Api.Endpoints.FrontDeskEndpoints;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// The master patient index: finding, registering and correcting patients, the queue of records that may be the
/// same person, and merging them. A merged record stays, pointing at the one it was merged into, so its MRN and
/// its access log still lead somewhere.
/// </summary>
public static partial class PatientEndpoints
{
    public record PatientFields(
        string FirstName, string? MiddleName, string LastName, string? PreferredName, DateOnly? DateOfBirth, string? Sex,
        string? HealthCardNumber, string? HealthCardVersion, DateOnly? HealthCardExpiry, string? CoverageProvince, string? OutOfProvinceNumber,
        string? Phone, string? Email, string? AddressLine1, string? City, string? Province, string? PostalCode, string? PreferredLanguage,
        string? FamilyProviderName, string? FamilyProviderOhipNumber, bool IsDeceased, DateOnly? DeceasedDate);

    public record PatientDetail(
        int Id, string Mrn, string Name, int Age, string Coverage, PatientFields Fields, PatientBrief? MergedInto, PatientBrief[] MergedFrom,
        int Visits, DateTime? LastVisitUtc, int OpenDuplicates, DateTime CreatedUtc, DateTime UpdatedUtc, string RowVersion);

    public record RegisterPatient(PatientFields Patient, bool ConfirmNew);
    public record UpdatePatient(PatientFields Patient, string RowVersion);
    public record PossibleDuplicate(PatientBrief Patient, DateOnly DateOfBirth, string? Phone, int Score, string[] Reasons);

    public record DuplicateRow(int Id, int Score, string[] Reasons, string Status, DateTime FoundUtc, PatientBrief A, PatientBrief B,
        string? ReviewedBy, DateTime? ReviewedUtc);
    public record DuplicatePair(DuplicateRow Row, PatientDetail A, PatientDetail B);
    public record MergeRequest(int SurvivorId, int MergedId);

    public static void MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/patients").WithTags("Patients");

        api.MapGet("", async (string? q, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var term = (q ?? "").Trim();
            if (term.Length < 2)
                return Results.Ok(Array.Empty<PatientBrief>());
            var digits = new string(term.Where(char.IsDigit).ToArray());
            var dob = DateOnly.TryParseExact(term, "yyyy-MM-dd", out var d) ? d : (DateOnly?)null;
            var today = Today(clock);
            var rows = await db.Patients.AsNoTracking().Where(p => p.MergedIntoPatientId == null && (
                    p.Mrn == term.ToUpper() || (digits.Length == 10 && p.HealthCardNumber == digits) || p.DateOfBirth == dob
                    || p.LastName.StartsWith(term) || (p.LastName + ", " + p.FirstName).StartsWith(term)))
                .OrderBy(p => p.LastName).ThenBy(p => p.FirstName).Take(20).ToListAsync();
            // An old MRN still finds the person: the record it was merged into is shown.
            if (rows.Count == 0 && await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Mrn == term.ToUpper()) is { MergedIntoPatientId: not null } old)
                rows.Add(await Survivor(db, old));
            return Results.Ok(rows.Select(p => Brief(p, today)));
        }).RequireAuthorization(Permissions.PatientsView);

        api.MapGet("/{id:int}", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (p is null)
                return Results.NotFound();
            Log(db, user, http, clock, p.Id, AccessActions.View, "Demographics");
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, p, Today(clock)));
        }).RequireAuthorization(Permissions.PatientsView);

        // Registering: a health card already on file is refused; a likely match stops the clerk until they confirm
        // it is someone else, and is then kept in the duplicates queue in case they were wrong.
        api.MapPost("", async (RegisterPatient req, ClaimsPrincipal user, ClinicHubDbContext db, CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            var today = Today(clock);
            var errors = await Validate(db, req.Patient, today);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);
            var now = clock.GetUtcNow().UtcDateTime;
            var patient = new Patient { CreatedUtc = now };
            Apply(patient, req.Patient, now);
            if (await CardHolder(db, patient) is { } holder)
                return CardTaken(holder, today);

            var matches = PatientMatching.FindMatches(patient, await Neighbours(db, patient));
            if (!req.ConfirmNew && matches.Any(m => m.Match.Score >= PatientMatching.Likely))
                return Results.Problem("This person may already be registered. Open the existing record, or confirm this is someone else.",
                    statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?>
                    {
                        ["possibleDuplicates"] = matches.Select(m => new PossibleDuplicate(Brief(m.Other, today), m.Other.DateOfBirth, m.Other.Phone,
                            m.Match.Score, m.Match.Reasons)).ToArray(),
                    });

            patient.Mrn = await NextMrn(db, catalog, user);
            db.Patients.Add(patient);
            await db.SaveChangesAsync();
            await QueueMatches(db, patient, matches, now);
            Log(db, user, http, clock, patient.Id, AccessActions.Edit, matches.Count > 0 ? $"Registered ({matches.Count} possible duplicate)" : "Registered");
            await db.SaveChangesAsync();
            return Results.Created($"/api/patients/{patient.Id}", await Detail(db, patient, today));
        }).RequireAuthorization(Permissions.PatientsEdit);

        api.MapPut("/{id:int}", async (int id, UpdatePatient req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == id);
            if (patient is null)
                return Results.NotFound();
            if (patient.MergedIntoPatientId is not null)
                return Results.Problem("This record was merged into another one; correct that one instead.", statusCode: StatusCodes.Status409Conflict);
            var today = Today(clock);
            var errors = await Validate(db, req.Patient, today);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);
            db.Entry(patient).Property(p => p.RowVersion).OriginalValue = Convert.FromBase64String(req.RowVersion);
            var now = clock.GetUtcNow().UtcDateTime;
            Apply(patient, req.Patient, now);
            if (await CardHolder(db, patient) is { } holder)
                return CardTaken(holder, today);
            Log(db, user, http, clock, patient.Id, AccessActions.Edit, "Demographics corrected");
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem("Someone else just changed this patient. Reload and try again.", statusCode: StatusCodes.Status409Conflict);
            }
            // A corrected name or birth date can reveal a duplicate that wasn't visible before.
            await QueueMatches(db, patient, PatientMatching.FindMatches(patient, await Neighbours(db, patient)), now);
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, patient, today));
        }).RequireAuthorization(Permissions.PatientsEdit);

        // ---- duplicates ----

        api.MapGet("/duplicates", async (string? status, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var today = Today(clock);
            var s = status is DuplicateStatus.Merged or DuplicateStatus.NotDuplicate ? status : DuplicateStatus.Open;
            var rows = await db.DuplicateCandidates.AsNoTracking().Include(c => c.PatientA).Include(c => c.PatientB)
                .Where(c => c.Status == s).OrderByDescending(c => s == DuplicateStatus.Open ? c.Score : 0).ThenByDescending(c => c.ReviewedUtc ?? c.FoundUtc)
                .Take(200).ToListAsync();
            var reviewers = await Reviewers(db, rows);
            return rows.Select(c => Row(c, today, reviewers));
        }).RequireAuthorization(Permissions.PatientsMerge);

        api.MapGet("/duplicates/{id:int}", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var c = await db.DuplicateCandidates.AsNoTracking().Include(x => x.PatientA).Include(x => x.PatientB).SingleOrDefaultAsync(x => x.Id == id);
            if (c is null)
                return Results.NotFound();
            var today = Today(clock);
            Log(db, user, http, clock, c.PatientAId, AccessActions.View, $"Duplicate review {c.Id}");
            Log(db, user, http, clock, c.PatientBId, AccessActions.View, $"Duplicate review {c.Id}");
            await db.SaveChangesAsync();
            return Results.Ok(new DuplicatePair(Row(c, today, await Reviewers(db, [c])), await Detail(db, c.PatientA, today), await Detail(db, c.PatientB, today)));
        }).RequireAuthorization(Permissions.PatientsMerge);

        // Compares every live record with the ones it could be (same birth date, phone, email, or birth year and
        // sound-alike last name). Pairs already decided are not asked again.
        api.MapPost("/duplicates/scan", async (ClinicHubDbContext db, TimeProvider clock) =>
        {
            var found = await Scan(db, clock.GetUtcNow().UtcDateTime);
            return new { found, open = await db.DuplicateCandidates.CountAsync(c => c.Status == DuplicateStatus.Open) };
        }).RequireAuthorization(Permissions.PatientsMerge);

        api.MapPost("/duplicates/{id:int}/not-duplicate", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var c = await db.DuplicateCandidates.SingleOrDefaultAsync(x => x.Id == id);
            if (c is null)
                return Results.NotFound();
            if (c.Status != DuplicateStatus.Open)
                return Results.Problem("This pair was already decided.", statusCode: StatusCodes.Status409Conflict);
            c.Status = DuplicateStatus.NotDuplicate;
            c.ReviewedByUserId = user.RequiredUserId();
            c.ReviewedUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Permissions.PatientsMerge);

        api.MapPost("/merge", async (MergeRequest req, ClaimsPrincipal user, ClinicHubDbContext db, CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            if (req.SurvivorId == req.MergedId)
                return Results.Problem("Choose two different records.", statusCode: StatusCodes.Status400BadRequest);
            var survivor = await db.Patients.SingleOrDefaultAsync(p => p.Id == req.SurvivorId);
            var merged = await db.Patients.SingleOrDefaultAsync(p => p.Id == req.MergedId);
            if (survivor is null || merged is null)
                return Results.NotFound();
            if (survivor.MergedIntoPatientId is not null || merged.MergedIntoPatientId is not null)
                return Results.Problem("One of these records was already merged.", statusCode: StatusCodes.Status409Conflict);
            if (survivor.HealthCardNumber is not null && merged.HealthCardNumber is not null && survivor.HealthCardNumber != merged.HealthCardNumber)
                return Results.Problem("The two records have different health cards. If one card is mistyped, correct it first; otherwise these are two people.",
                    statusCode: StatusCodes.Status409Conflict);

            var clinic = await catalog.Clinics.AsNoTracking().SingleAsync(c => c.Id == user.RequiredClinicId());
            await Merge(db, clinic, survivor, merged, user.RequiredUserId(), clock.GetUtcNow().UtcDateTime, AuthEndpoints.Ip(http));
            return Results.Ok(await Detail(db, survivor, Today(clock)));
        }).RequireAuthorization(Permissions.PatientsMerge);

        api.MapPost("/{id:int}/unmerge", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            var merged = await db.Patients.SingleOrDefaultAsync(p => p.Id == id);
            if (merged is null)
                return Results.NotFound();
            if (merged.MergedIntoPatientId is null)
                return Results.Problem("This record is not merged into another.", statusCode: StatusCodes.Status409Conflict);
            var clinic = await catalog.Clinics.AsNoTracking().SingleAsync(c => c.Id == user.RequiredClinicId());
            var refusal = await Unmerge(db, clinic, merged, user.RequiredUserId(), clock.GetUtcNow().UtcDateTime, AuthEndpoints.Ip(http));
            return refusal is null ? Results.Ok(await Detail(db, merged, Today(clock))) : Results.Problem(refusal, statusCode: StatusCodes.Status409Conflict);
        }).RequireAuthorization(Permissions.PatientsMerge);
    }

    /// <summary>
    /// One person, one record. Everything the merged record had (appointments, visits, orders, claims, invoices,
    /// consent directives) moves to the survivor; blanks in the survivor are filled from it; the merged record stays,
    /// pointing at the survivor. The access log is not rewritten: it says which record was opened, and the survivor's
    /// log includes the merged record's.
    /// </summary>
    public static async Task Merge(ClinicHubDbContext db, Clinic clinic, Patient survivor, Patient merged, int userId, DateTime now, string? ip)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var (to, from) = (survivor.Id, merged.Id);
            // The applications that already have something for the merged record are told (IntegrationHub's flow 6):
            // those its orders went to, and the pharmacy for its prescriptions. The external lab is not: it is sent orders
            // over HL7 and has no merge message.
            var told = await db.ServiceRequests.Where(x => x.PatientId == from && x.SentUtc != null && x.TargetSystem != TargetSystems.ExternalLab)
                .Select(x => x.TargetSystem).Distinct().ToListAsync();
            if (await db.Prescriptions.AnyAsync(x => x.PatientId == from && x.SentUtc != null))
                told.Add(TargetSystems.PharmaFlow);
            // What moves is written down, so that the merge can be undone (see Unmerge).
            var moved = new Dictionary<string, List<int>>
            {
                [nameof(db.Appointments)] = await db.Appointments.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.Encounters)] = await db.Encounters.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.EligibilityChecks)] = await db.EligibilityChecks.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.Referrals)] = await db.Referrals.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.ServiceRequests)] = await db.ServiceRequests.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.Prescriptions)] = await db.Prescriptions.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.Claims)] = await db.Claims.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.Invoices)] = await db.Invoices.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.ConsentDirectives)] = await db.ConsentDirectives.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
                [nameof(db.Allergies)] = await db.Allergies.Where(x => x.PatientId == from).Select(x => x.Id).ToListAsync(),
            };
            await db.Appointments.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.Encounters.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.EligibilityChecks.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.Referrals.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.ServiceRequests.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.Prescriptions.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.Claims.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.Invoices.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.ConsentDirectives.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            await db.Allergies.Where(x => x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));

            // The merged record leaves the live set first, so its health card can move to the survivor
            // (one live record per card is a unique index).
            merged.MergedIntoPatientId = to;
            merged.UpdatedUtc = now;
            await db.SaveChangesAsync();
            var before = Snapshot(survivor);
            FillBlanks(survivor, merged);
            survivor.UpdatedUtc = now;
            var after = Snapshot(survivor);
            var filled = after.Keys.Where(k => before[k] != after[k]).ToList();

            // Other open pairs with the merged record become pairs with the survivor, unless that pair is already known.
            var pairs = await db.DuplicateCandidates.Where(c => c.PatientAId == from || c.PatientBId == from).ToListAsync();
            var known = await db.DuplicateCandidates.Where(c => c.PatientAId == to || c.PatientBId == to)
                .Select(c => c.PatientAId == to ? c.PatientBId : c.PatientAId).ToListAsync();
            var suggested = pairs.Any(c => c.PatientAId == to || c.PatientBId == to);
            foreach (var c in pairs)
            {
                var other = c.PatientAId == from ? c.PatientBId : c.PatientAId;
                if (other == to)
                {
                    c.Status = DuplicateStatus.Merged;
                    c.ReviewedByUserId = userId;
                    c.ReviewedUtc = now;
                }
                else if (c.Status == DuplicateStatus.Open && !known.Contains(other))
                {
                    (c.PatientAId, c.PatientBId) = (Math.Min(to, other), Math.Max(to, other));
                    known.Add(other);
                }
                else if (c.Status == DuplicateStatus.Open)
                    db.DuplicateCandidates.Remove(c);
            }
            if (!suggested)
                db.DuplicateCandidates.Add(new PatientDuplicateCandidate
                {
                    PatientAId = Math.Min(to, from), PatientBId = Math.Max(to, from), Score = PatientMatching.Score(survivor, merged).Score,
                    Reasons = "Merged without a suggestion", Status = DuplicateStatus.Merged, FoundUtc = now, ReviewedByUserId = userId, ReviewedUtc = now,
                });

            // Orders not delivered yet go out under the surviving record. Delivered ones keep the MRN the application knows.
            var waiting = await db.Outbox.Where(m => m.SentUtc == null && m.Kind == OutboxKinds.Order
                && db.ServiceRequests.Any(s => s.OutboxMessageId == m.Id && s.PatientId == to)).ToListAsync();
            foreach (var m in waiting)
            {
                var orders = await db.ServiceRequests.Include(s => s.RequesterProvider).Where(s => s.OutboxMessageId == m.Id).OrderBy(s => s.Id).ToListAsync();
                var label = await ConfidentialityLabels.ForAsync(db, to, orders[0].EncounterId);
                m.PayloadJson = FhirOrderBundle.Build(clinic, survivor, orders[0].RequesterProvider, orders, m.CreatedUtc, label).ToJsonString();
            }

            var messages = told.Distinct().Select(destination => new OutboxMessage
            {
                Destination = destination, Kind = OutboxKinds.PatientMerge, CreatedUtc = now,
                PayloadJson = FhirOrderBundle.PatientMerge(clinic, merged.Mrn, survivor.Mrn, now).ToJsonString(),
            }).ToList();
            db.Outbox.AddRange(messages);

            var note = $"Merged {merged.Mrn} into {survivor.Mrn}";
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = userId, PatientId = to, Action = AccessActions.Edit, Context = note, IpAddress = ip });
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = userId, PatientId = from, Action = AccessActions.Edit, Context = note, IpAddress = ip });
            await db.SaveChangesAsync();
            db.MergeLogs.Add(new PatientMergeLog
            {
                SurvivorId = to, MergedId = from, AtUtc = now, ByUserId = userId,
                MovedJson = JsonSerializer.Serialize(moved),
                FieldsBeforeJson = JsonSerializer.Serialize(filled.ToDictionary(k => k, k => before[k])),
                FieldsAfterJson = JsonSerializer.Serialize(filled.ToDictionary(k => k, k => after[k])),
                OutboxIdsJson = JsonSerializer.Serialize(messages.Select(m => m.Id)),
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }

    /// <summary>The fields a merge may fill in, by name.</summary>
    private static readonly string[] FilledFields =
    [
        nameof(Patient.MiddleName), nameof(Patient.PreferredName), nameof(Patient.HealthCardNumber), nameof(Patient.HealthCardVersion),
        nameof(Patient.HealthCardExpiry), nameof(Patient.CoverageProvince), nameof(Patient.OutOfProvinceNumber), nameof(Patient.Phone),
        nameof(Patient.Email), nameof(Patient.AddressLine1), nameof(Patient.City), nameof(Patient.Province), nameof(Patient.PostalCode),
        nameof(Patient.FamilyProviderName), nameof(Patient.FamilyProviderOhipNumber), nameof(Patient.IsDeceased), nameof(Patient.DeceasedDate),
    ];

    /// <summary>Those fields as text (null stays null), to compare and to write down.</summary>
    private static Dictionary<string, string?> Snapshot(Patient p) =>
        FilledFields.ToDictionary(f => f, f => typeof(Patient).GetProperty(f)!.GetValue(p) switch
        {
            null => null,
            DateOnly d => d.ToString("O"),
            var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        });

    private static void Restore(Patient p, string field, string? value)
    {
        var prop = typeof(Patient).GetProperty(field)!;
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        prop.SetValue(p, value is null ? null
            : type == typeof(DateOnly) ? DateOnly.Parse(value)
            : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Takes a merge back: what moved goes back to the record it came from, the fields it filled in are emptied again
    /// (unless someone has changed them since), and the merged record is live again. Refused when the survivor was
    /// merged into another record since, when the applications were already told about the merge (they cannot be told
    /// to forget it), or when the two records share a health card. Returns why it was refused, or null.
    /// </summary>
    public static async Task<string?> Unmerge(ClinicHubDbContext db, Clinic clinic, Patient merged, int userId, DateTime now, string? ip)
    {
        var log = await db.MergeLogs.OrderByDescending(l => l.Id).FirstOrDefaultAsync(l => l.MergedId == merged.Id && l.UndoneUtc == null);
        if (log is null || merged.MergedIntoPatientId != log.SurvivorId)
            return "This merge was made before merges were written down, so it cannot be undone from here.";
        var survivor = await db.Patients.SingleAsync(p => p.Id == log.SurvivorId);
        if (survivor.MergedIntoPatientId is not null)
            return $"{survivor.Mrn} was merged into another record since. Undo that merge first.";
        var outboxIds = JsonSerializer.Deserialize<List<long>>(log.OutboxIdsJson) ?? [];
        if (await db.Outbox.AnyAsync(m => outboxIds.Contains(m.Id) && m.SentUtc != null))
            return "The applications have already been told about this merge and cannot be told to take it back. Correct the records there by hand.";

        string? refusal = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var (to, from) = (merged.Id, survivor.Id);
            var moved = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(log.MovedJson) ?? [];
            List<int> Ids(string table) => moved.GetValueOrDefault(table) ?? [];

            // The fields the merge filled in are emptied again, unless the value has been changed since.
            var beforeFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(log.FieldsBeforeJson) ?? [];
            var afterFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(log.FieldsAfterJson) ?? [];
            var current = Snapshot(survivor);
            foreach (var (field, was) in beforeFields)
                if (current[field] == afterFields[field])
                    Restore(survivor, field, was);
            if (merged.HealthCardNumber is not null && survivor.HealthCardNumber == merged.HealthCardNumber)
            {
                refusal = "The two records share a health card. Correct one of them first.";
                return;
            }
            survivor.UpdatedUtc = now;
            await db.SaveChangesAsync();
            merged.MergedIntoPatientId = null;
            merged.UpdatedUtc = now;
            await db.SaveChangesAsync();

            var a = Ids(nameof(db.Appointments)); await db.Appointments.Where(x => a.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var e = Ids(nameof(db.Encounters)); await db.Encounters.Where(x => e.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var c = Ids(nameof(db.EligibilityChecks)); await db.EligibilityChecks.Where(x => c.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var r = Ids(nameof(db.Referrals)); await db.Referrals.Where(x => r.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var o = Ids(nameof(db.ServiceRequests)); await db.ServiceRequests.Where(x => o.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var rx = Ids(nameof(db.Prescriptions)); await db.Prescriptions.Where(x => rx.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var cl = Ids(nameof(db.Claims)); await db.Claims.Where(x => cl.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var iv = Ids(nameof(db.Invoices)); await db.Invoices.Where(x => iv.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var cd = Ids(nameof(db.ConsentDirectives)); await db.ConsentDirectives.Where(x => cd.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));
            var al = Ids(nameof(db.Allergies)); await db.Allergies.Where(x => al.Contains(x.Id) && x.PatientId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.PatientId, to));

            // The merge's messages were never sent: they are dropped. Orders not delivered yet go out under the record they came from again.
            db.Outbox.RemoveRange(await db.Outbox.Where(m => outboxIds.Contains(m.Id)).ToListAsync());
            var waiting = await db.Outbox.Where(m => m.SentUtc == null && m.Kind == OutboxKinds.Order
                && db.ServiceRequests.Any(s => s.OutboxMessageId == m.Id && s.PatientId == to)).ToListAsync();
            foreach (var m in waiting)
            {
                var orders = await db.ServiceRequests.Include(s => s.RequesterProvider).Where(s => s.OutboxMessageId == m.Id).OrderBy(s => s.Id).ToListAsync();
                var label = await ConfidentialityLabels.ForAsync(db, to, orders[0].EncounterId);
                m.PayloadJson = FhirOrderBundle.Build(clinic, merged, orders[0].RequesterProvider, orders, m.CreatedUtc, label).ToJsonString();
            }

            // The pair is open again for a person to decide.
            var pair = await db.DuplicateCandidates.SingleOrDefaultAsync(x => x.PatientAId == Math.Min(to, from) && x.PatientBId == Math.Max(to, from));
            if (pair is not null)
                (pair.Status, pair.ReviewedByUserId, pair.ReviewedUtc) = (DuplicateStatus.Open, null, null);

            log.UndoneUtc = now;
            log.UndoneByUserId = userId;
            var note = $"Undid the merge of {merged.Mrn} into {survivor.Mrn}";
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = userId, PatientId = to, Action = AccessActions.Edit, Context = note, IpAddress = ip });
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = userId, PatientId = from, Action = AccessActions.Edit, Context = note, IpAddress = ip });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        return refusal;
    }

    /// <summary>The live record a merged one ended up in (merges can chain: A into B, later B into C).</summary>
    public static async Task<Patient> Survivor(ClinicHubDbContext db, Patient p)
    {
        for (var hops = 0; p.MergedIntoPatientId is { } next && hops < 20; hops++)
            p = await db.Patients.AsNoTracking().SingleAsync(x => x.Id == next);
        return p;
    }

    private static void FillBlanks(Patient s, Patient m)
    {
        s.MiddleName ??= m.MiddleName;
        s.PreferredName ??= m.PreferredName;
        if (s.HealthCardNumber is null && m.HealthCardNumber is not null)
            (s.HealthCardNumber, s.HealthCardVersion, s.HealthCardExpiry, s.CoverageProvince) = (m.HealthCardNumber, m.HealthCardVersion, m.HealthCardExpiry, "ON");
        if (s.CoverageProvince is null)
            (s.CoverageProvince, s.OutOfProvinceNumber) = (m.CoverageProvince, m.OutOfProvinceNumber);
        s.Phone ??= m.Phone;
        s.Email ??= m.Email;
        if (s.AddressLine1 is null)
            (s.AddressLine1, s.City, s.Province, s.PostalCode) = (m.AddressLine1, m.City ?? s.City, m.Province ?? s.Province, m.PostalCode ?? s.PostalCode);
        s.FamilyProviderName ??= m.FamilyProviderName;
        s.FamilyProviderOhipNumber ??= m.FamilyProviderOhipNumber;
        if (m.IsDeceased && !s.IsDeceased)
            (s.IsDeceased, s.DeceasedDate) = (true, m.DeceasedDate);
    }

    /// <summary>Adds the open pairs found among every live record; returns how many were new.</summary>
    public static async Task<int> Scan(ClinicHubDbContext db, DateTime now)
    {
        var live = await db.Patients.AsNoTracking().Where(p => p.MergedIntoPatientId == null).ToListAsync();
        var known = (await db.DuplicateCandidates.Select(c => new { c.PatientAId, c.PatientBId }).ToListAsync())
            .Select(c => (c.PatientAId, c.PatientBId)).ToHashSet();
        var fresh = PatientMatching.FindPairs(live).Where(p => !known.Contains((p.A.Id, p.B.Id))).ToList();
        db.DuplicateCandidates.AddRange(fresh.Select(p => new PatientDuplicateCandidate
        {
            PatientAId = p.A.Id, PatientBId = p.B.Id, Score = p.Match.Score, Reasons = Reasons(p.Match), FoundUtc = now,
        }));
        await db.SaveChangesAsync();
        return fresh.Count;
    }

    private static async Task QueueMatches(ClinicHubDbContext db, Patient patient, List<(Patient Other, PatientMatching.Match Match)> matches, DateTime now)
    {
        if (matches.Count == 0)
            return;
        var known = await db.DuplicateCandidates.Where(c => c.PatientAId == patient.Id || c.PatientBId == patient.Id)
            .Select(c => c.PatientAId == patient.Id ? c.PatientBId : c.PatientAId).ToListAsync();
        foreach (var (other, match) in matches.Where(m => !known.Contains(m.Other.Id)))
            db.DuplicateCandidates.Add(new PatientDuplicateCandidate
            {
                PatientAId = Math.Min(patient.Id, other.Id), PatientBId = Math.Max(patient.Id, other.Id), Score = match.Score,
                Reasons = Reasons(match), FoundUtc = now,
            });
    }

    private static string Reasons(PatientMatching.Match m)
    {
        var text = string.Join("; ", m.Reasons);
        return text.Length <= 300 ? text : text[..300];
    }

    /// <summary>The live records worth comparing with <paramref name="p"/>: same birth year, phone or email; the blocking keys narrow it further.</summary>
    private static async Task<List<Patient>> Neighbours(ClinicHubDbContext db, Patient p)
    {
        var year = p.DateOfBirth.Year;
        return await db.Patients.AsNoTracking().Where(o => o.MergedIntoPatientId == null && o.Id != p.Id
            && (o.DateOfBirth.Year == year || (p.Phone != null && o.Phone == p.Phone) || (p.Email != null && o.Email == p.Email))).ToListAsync();
    }

    private static Task<Patient?> CardHolder(ClinicHubDbContext db, Patient p) => p.HealthCardNumber is null
        ? Task.FromResult<Patient?>(null)
        : db.Patients.AsNoTracking().FirstOrDefaultAsync(o => o.HealthCardNumber == p.HealthCardNumber && o.MergedIntoPatientId == null && o.Id != p.Id);

    private static IResult CardTaken(Patient holder, DateOnly today) =>
        Results.Problem($"This health card is already on file for {holder.DisplayName} ({holder.Mrn}).", statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["existingPatient"] = Brief(holder, today) });

    /// <summary>The clinic's MRN prefix (the letters of its existing MRNs, or its slug's first two) and the next number.</summary>
    private static async Task<string> NextMrn(ClinicHubDbContext db, CatalogDbContext catalog, ClaimsPrincipal user)
    {
        var last = await db.Patients.OrderByDescending(p => p.Id).Select(p => p.Mrn).FirstOrDefaultAsync();
        var prefix = last is null ? null : new string(last.TakeWhile(char.IsAsciiLetter).ToArray());
        if (string.IsNullOrEmpty(prefix))
        {
            var slug = await catalog.Clinics.Where(c => c.Id == user.RequiredClinicId()).Select(c => c.Slug).SingleAsync();
            prefix = new string(slug.Where(char.IsAsciiLetter).Take(2).ToArray()).ToUpperInvariant().PadRight(2, 'P');
        }
        var width = prefix.Length + 6;
        var max = await db.Patients.Where(p => p.Mrn.StartsWith(prefix) && p.Mrn.Length == width).MaxAsync(p => (string?)p.Mrn);
        var next = max is not null && int.TryParse(max[prefix.Length..], out var n) ? n + 1 : 1;
        return $"{prefix}{next:000000}";
    }

    private static async Task<Dictionary<string, string[]>> Validate(ClinicHubDbContext db, PatientFields f, DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool bad, string field, string message) { if (bad && !errors.ContainsKey(field)) errors[field] = [message]; }
        static bool TooLong(string? s, int max) => s?.Trim().Length > max;

        Check(string.IsNullOrWhiteSpace(f.FirstName), "firstName", "Enter the first name.");
        Check(string.IsNullOrWhiteSpace(f.LastName), "lastName", "Enter the last name.");
        foreach (var (value, field) in new[] { (f.FirstName, "firstName"), (f.MiddleName, "middleName"), (f.LastName, "lastName"), (f.PreferredName, "preferredName") })
            Check(TooLong(value, 60), field, "At most 60 characters.");
        Check(f.DateOfBirth is null, "dateOfBirth", "Enter the date of birth.");
        Check(f.DateOfBirth > today, "dateOfBirth", "The date of birth is in the future.");
        Check(f.DateOfBirth < new DateOnly(1900, 1, 1), "dateOfBirth", "Check the year of birth.");
        Check(f.Sex is not (null or "M" or "F" or "X" or "U"), "sex", "Choose M, F, X or unknown.");

        var card = Clean(f.HealthCardNumber) is { } c ? HealthCard.Normalize(c) : null;
        Check(card is not null && !HealthCard.IsValidNumber(card), "healthCardNumber", "Not a valid Ontario health card number (10 digits, check digit).");
        Check(!HealthCard.IsValidVersion(Clean(f.HealthCardVersion)?.ToUpperInvariant()), "healthCardVersion", "One or two letters.");
        Check(card is null && Clean(f.HealthCardVersion) is not null, "healthCardVersion", "A version code needs a card number.");
        var coverage = Clean(f.CoverageProvince)?.ToUpperInvariant();
        Check(coverage is not null && !await db.Provinces.AnyAsync(p => p.Code == coverage), "coverageProvince", "Unknown province.");
        Check(card is not null && coverage is not null && coverage != "ON", "coverageProvince", "An Ontario health card means OHIP coverage.");
        Check(Clean(f.OutOfProvinceNumber) is not null && (coverage is null or "ON"), "outOfProvinceNumber", "Only for another province's plan.");
        Check(TooLong(f.OutOfProvinceNumber, 12), "outOfProvinceNumber", "At most 12 characters.");

        Check(TooLong(f.Phone, 20), "phone", "At most 20 characters.");
        Check(Clean(f.Email) is { } email && (email.Length > 120 || !email.Contains('@')), "email", "Not an email address.");
        Check(TooLong(f.AddressLine1, 200), "addressLine1", "At most 200 characters.");
        Check(TooLong(f.City, 80), "city", "At most 80 characters.");
        Check(Clean(f.Province) is { } prov && !await db.Provinces.AnyAsync(p => p.Code == prov.ToUpper()), "province", "Unknown province.");
        Check(Clean(f.PostalCode) is { } pc && !PostalCode().IsMatch(pc.ToUpperInvariant()), "postalCode", "A Canadian postal code, like M5V 0A1.");
        Check(f.PreferredLanguage is not (null or "en" or "fr"), "preferredLanguage", "English or French.");
        Check(TooLong(f.FamilyProviderName, 120), "familyProviderName", "At most 120 characters.");
        Check(Clean(f.FamilyProviderOhipNumber) is { } fp && (fp.Length != 6 || !fp.All(char.IsAsciiDigit)), "familyProviderOhipNumber", "Six digits.");
        Check(f.DeceasedDate is not null && !f.IsDeceased, "deceasedDate", "Only for a deceased patient.");
        Check(f.DeceasedDate > today || f.DeceasedDate < f.DateOfBirth, "deceasedDate", "Check the date of death.");
        return errors;
    }

    private static void Apply(Patient p, PatientFields f, DateTime now)
    {
        var card = Clean(f.HealthCardNumber) is { } c ? HealthCard.Normalize(c) : null;
        p.FirstName = f.FirstName.Trim();
        p.MiddleName = Clean(f.MiddleName);
        p.LastName = f.LastName.Trim();
        p.PreferredName = Clean(f.PreferredName);
        p.DateOfBirth = f.DateOfBirth!.Value;
        p.Sex = f.Sex ?? "U";
        p.HealthCardNumber = card;
        p.HealthCardVersion = card is null ? null : Clean(f.HealthCardVersion)?.ToUpperInvariant();
        p.HealthCardExpiry = card is null ? null : f.HealthCardExpiry;
        p.CoverageProvince = card is not null ? "ON" : Clean(f.CoverageProvince)?.ToUpperInvariant();
        p.OutOfProvinceNumber = p.CoverageProvince is null or "ON" ? null : Clean(f.OutOfProvinceNumber);
        p.Phone = Clean(f.Phone);
        p.Email = Clean(f.Email);
        p.AddressLine1 = Clean(f.AddressLine1);
        p.City = Clean(f.City);
        p.Province = Clean(f.Province)?.ToUpperInvariant();
        p.PostalCode = Clean(f.PostalCode) is { } pc ? FormatPostal(pc) : null;
        p.PreferredLanguage = f.PreferredLanguage ?? "en";
        p.FamilyProviderName = Clean(f.FamilyProviderName);
        p.FamilyProviderOhipNumber = Clean(f.FamilyProviderOhipNumber);
        p.IsDeceased = f.IsDeceased;
        p.DeceasedDate = f.IsDeceased ? f.DeceasedDate : null;
        p.UpdatedUtc = now;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string FormatPostal(string s)
    {
        var compact = new string(s.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return $"{compact[..3]} {compact[3..]}";
    }

    [GeneratedRegex(@"^[A-CEGHJ-NPR-TVXY]\d[A-CEGHJ-NPR-TV-Z] ?\d[A-CEGHJ-NPR-TV-Z]\d$")]
    private static partial Regex PostalCode();

    private static PatientFields Fields(Patient p) => new(p.FirstName, p.MiddleName, p.LastName, p.PreferredName, p.DateOfBirth, p.Sex,
        p.HealthCardNumber, p.HealthCardVersion, p.HealthCardExpiry, p.CoverageProvince, p.OutOfProvinceNumber, p.Phone, p.Email,
        p.AddressLine1, p.City, p.Province, p.PostalCode, p.PreferredLanguage, p.FamilyProviderName, p.FamilyProviderOhipNumber,
        p.IsDeceased, p.DeceasedDate);

    private static async Task<PatientDetail> Detail(ClinicHubDbContext db, Patient p, DateOnly today)
    {
        var mergedInto = p.MergedIntoPatientId is null ? null : await Survivor(db, p);
        var mergedFrom = await db.Patients.AsNoTracking().Where(x => x.MergedIntoPatientId == p.Id).OrderBy(x => x.Id).ToListAsync();
        var visits = db.Encounters.Where(e => e.PatientId == p.Id && e.Status != EncounterStatus.Voided);
        var open = await db.DuplicateCandidates.CountAsync(c => c.Status == DuplicateStatus.Open && (c.PatientAId == p.Id || c.PatientBId == p.Id));
        var brief = Brief(p, today);
        return new PatientDetail(p.Id, p.Mrn, brief.Name, brief.Age, brief.Coverage, Fields(p),
            mergedInto is null ? null : Brief(mergedInto, today), [.. mergedFrom.Select(x => Brief(x, today))],
            await visits.CountAsync(), await visits.MaxAsync(e => (DateTime?)e.CheckInUtc), open, p.CreatedUtc, p.UpdatedUtc,
            Convert.ToBase64String(p.RowVersion));
    }

    private static async Task<Dictionary<int, string>> Reviewers(ClinicHubDbContext db, IEnumerable<PatientDuplicateCandidate> rows)
    {
        var ids = rows.Where(c => c.ReviewedByUserId != null).Select(c => c.ReviewedByUserId!.Value).Distinct().ToList();
        return await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
    }

    private static DuplicateRow Row(PatientDuplicateCandidate c, DateOnly today, Dictionary<int, string> reviewers) =>
        new(c.Id, c.Score, c.Reasons.Split("; ", StringSplitOptions.RemoveEmptyEntries), c.Status, c.FoundUtc, Brief(c.PatientA, today), Brief(c.PatientB, today),
            c.ReviewedByUserId is { } r ? reviewers.GetValueOrDefault(r) : null, c.ReviewedUtc);

    private static void Log(ClinicHubDbContext db, ClaimsPrincipal user, HttpContext http, TimeProvider clock, int patientId, string action, string context) =>
        db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = clock.GetUtcNow().UtcDateTime, UserId = user.RequiredUserId(), PatientId = patientId,
            Action = action, Context = context, IpAddress = AuthEndpoints.Ip(http) });
}
