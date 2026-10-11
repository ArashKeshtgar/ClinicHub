using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static ClinicHub.Api.Endpoints.FrontDeskEndpoints;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// The visit: the clinician's worklist, vitals, the SOAP note, diagnosis and fee codes, and signing.
/// Signing locks the note (later corrections are addenda) and turns the coded services into an OHIP
/// claim ready for the next batch, plus an invoice for anything OHIP does not pay for.
/// </summary>
public static class EncounterEndpoints
{
    public record WorklistRow(
        int Id, int? AppointmentId, PatientBrief Patient, int ProviderId, string Kind, string Status, string PaymentProgram,
        string? AppointmentStatus, string? Room, string? Reason, DateTime CheckInUtc, DateTime? RoomedUtc, DateTime? StartedUtc,
        bool HasVitals, bool HasNote, int Coded);
    public record Worklist(DateOnly Date, ProviderBrief[] Providers, WorklistRow[] Visits, WorklistRow[] OlderUnsigned);

    public record Banner(int Id, string Name, string Mrn, int Age, string Sex, DateOnly DateOfBirth, string Coverage, string? HealthCard);
    public record VitalsDto(int Id, DateTime TakenUtc, short? SystolicMmHg, short? DiastolicMmHg, short? HeartRate, short? RespiratoryRate,
        decimal? TemperatureC, byte? SpO2, decimal? WeightKg, decimal? HeightCm, decimal? Bmi, string TakenBy);
    public record NoteDto(string? Subjective, string? Objective, string? Assessment, string? Plan, string? Addendum, DateTime UpdatedUtc, string Author);
    public record DiagnosisDto(string Code, string Description, bool IsPrimary);
    public record ServiceDto(string? FeeCode, int? UninsuredServiceId, string Code, string Description, byte Units, decimal Fee, bool Insured);
    public record HistoryRow(int Id, DateTime CheckInUtc, string Provider, string Status, string? Diagnosis, string? Assessment);
    public record BillingDto(string? ClaimNumber, string? ClaimStatus, decimal? ClaimAmount, string? InvoiceNumber, decimal? InvoiceTotal);

    public record EncounterDetail(
        int Id, string Status, string Kind, string PaymentProgram, string? ReasonForVisit, DateTime CheckInUtc, DateTime? StartedUtc,
        DateTime? EndedUtc, DateTime? SignedUtc, string? SignedBy, Banner Patient, ProviderBrief Provider, int? AppointmentId,
        string? AppointmentStatus, string? Room, EligibilityBrief? Eligibility, VitalsDto[] Vitals, NoteDto? Note,
        DiagnosisDto[] Diagnoses, ServiceDto[] Services, decimal InsuredTotal, decimal UninsuredTotal, string[] ReadyToSign,
        BillingDto? Billing, HistoryRow[] History, int WithheldVisits);

    public record VitalsRequest(short? SystolicMmHg, short? DiastolicMmHg, short? HeartRate, short? RespiratoryRate,
        decimal? TemperatureC, byte? SpO2, decimal? WeightKg, decimal? HeightCm);
    public record NoteRequest(string? Subjective, string? Objective, string? Assessment, string? Plan);
    public record CodedDiagnosis(string Code, bool IsPrimary);
    public record CodedService(string? FeeCode, int? UninsuredServiceId, byte Units);
    public record CodingRequest(CodedDiagnosis[] Diagnoses, CodedService[] Services);
    public record AddendumRequest(string Text);

    public static void MapEncounterEndpoints(this IEndpointRouteBuilder app)
    {
        // A visit withheld by a consent directive answers 403 on every route that names it.
        var api = app.MapGroup("/api/encounters").WithTags("Encounters").AddEndpointFilter(ConsentEndpoints.EncounterFilter);

        // The day's visits for one provider (default: the signed-in one) or the whole clinic, plus their unsigned backlog.
        api.MapGet("/", async (DateOnly? date, int? providerId, bool? all, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var day = date ?? Today(clock);
            var provider = all == true ? null : providerId ?? user.ProviderId();
            var (start, end) = DayBounds(day);
            var providers = await db.Providers.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Kind).ThenBy(p => p.LastName)
                .Select(p => new ProviderBrief(p.Id, p.Title == null ? p.FirstName + " " + p.LastName : p.Title + " " + p.FirstName + " " + p.LastName, p.Kind, p.SpecialtyName))
                .ToArrayAsync();
            var mine = db.Encounters.AsNoTracking().Where(e => e.Status != EncounterStatus.Voided && (provider == null || e.ProviderId == provider));
            var today = Today(clock);
            var visits = await Rows(mine.Where(e => e.CheckInUtc >= start && e.CheckInUtc < end), today);
            var older = await Rows(mine.Where(e => e.Status == EncounterStatus.Open && e.CheckInUtc < start), today);
            return new Worklist(day, providers, visits, older);
        }).RequireAuthorization(Permissions.EncountersView);

        api.MapGet("/{id:int}", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var e = await db.Encounters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (e is null)
                return Results.NotFound();
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = clock.GetUtcNow().UtcDateTime, UserId = user.RequiredUserId(), PatientId = e.PatientId,
                Action = AccessActions.View, Context = $"Encounter {id}", ConsentOverrideId = ConsentEndpoints.OverrideId(http), IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, id, clock, user));
        }).RequireAuthorization(Permissions.EncountersView);

        // The clinician walks in (Roomed → with the provider) and out (→ completed). Signing is separate: often at the end of the day.
        api.MapPost("/{id:int}/start", (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
            MoveAppointment(id, AppointmentStatus.WithProvider, user, db, clock)).RequireAuthorization(Permissions.EncountersDocument);
        api.MapPost("/{id:int}/finish", (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
            MoveAppointment(id, AppointmentStatus.Completed, user, db, clock)).RequireAuthorization(Permissions.EncountersDocument);

        api.MapPost("/{id:int}/vitals", async (int id, VitalsRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var e = await db.Encounters.SingleOrDefaultAsync(x => x.Id == id);
            if (Locked(e) is { } locked)
                return locked;
            var errors = ValidateVitals(req);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);
            db.VitalSigns.Add(new VitalSigns
            {
                EncounterId = id, TakenUtc = clock.GetUtcNow().UtcDateTime, TakenByUserId = user.RequiredUserId(),
                SystolicMmHg = req.SystolicMmHg, DiastolicMmHg = req.DiastolicMmHg, HeartRate = req.HeartRate, RespiratoryRate = req.RespiratoryRate,
                TemperatureC = req.TemperatureC, SpO2 = req.SpO2, WeightKg = req.WeightKg, HeightCm = req.HeightCm,
            });
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, id, clock, user));
        }).RequireAuthorization(Permissions.EncountersDocument);

        // Saved as the clinician types (the page sends it after a pause), so it returns only the time it was saved.
        api.MapPut("/{id:int}/note", async (int id, NoteRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var e = await db.Encounters.Include(x => x.Note).SingleOrDefaultAsync(x => x.Id == id);
            if (Locked(e) is { } locked)
                return locked;
            var now = clock.GetUtcNow().UtcDateTime;
            void Apply(ClinicalNote n)
            {
                n.Subjective = Clean(req.Subjective);
                n.Objective = Clean(req.Objective);
                n.Assessment = Clean(req.Assessment);
                n.Plan = Clean(req.Plan);
                n.AuthorUserId = user.RequiredUserId();
                n.UpdatedUtc = now;
            }
            var creating = e!.Note is null;
            e.Note ??= new ClinicalNote();
            Apply(e.Note);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) when (creating)
            {
                // A save from another tab or request created the note first: update that one.
                db.ChangeTracker.Clear();
                var existing = await db.ClinicalNotes.SingleAsync(n => n.EncounterId == id);
                Apply(existing);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { updatedUtc = now });
        }).RequireAuthorization(Permissions.EncountersDocument);

        // Diagnosis and fee codes are the billing decision, so only those who can sign may set them.
        api.MapPut("/{id:int}/coding", async (int id, CodingRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var e = await db.Encounters.Include(x => x.Diagnoses).Include(x => x.Services).SingleOrDefaultAsync(x => x.Id == id);
            if (Locked(e) is { } locked)
                return locked;

            var errors = new Dictionary<string, string[]>();
            var dxCodes = req.Diagnoses.Select(d => d.Code.Trim()).ToArray();
            var known = await db.DiagnosticCodes.Where(d => dxCodes.Contains(d.Code) && d.IsActive).Select(d => d.Code).ToListAsync();
            if (dxCodes.Except(known).FirstOrDefault() is { } unknownDx)
                errors["diagnoses"] = [$"Unknown diagnostic code {unknownDx}."];
            else if (dxCodes.Distinct().Count() != dxCodes.Length)
                errors["diagnoses"] = ["Each diagnosis once."];
            else if (req.Diagnoses.Count(d => d.IsPrimary) > 1)
                errors["diagnoses"] = ["Only one primary diagnosis."];

            var feeCodes = await db.FeeCodes.Where(f => f.IsActive).ToDictionaryAsync(f => f.ServiceCode);
            var uninsured = await db.UninsuredServices.Where(u => u.IsActive).ToDictionaryAsync(u => u.Id);
            var serviceDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e!.CheckInUtc, DemoSeeder.Toronto));
            var services = new List<EncounterService>();
            foreach (var s in req.Services)
            {
                if (s.Units is < 1 or > 99)
                    errors["services"] = ["Units must be 1 to 99."];
                else if ((s.FeeCode is null) == (s.UninsuredServiceId is null))
                    errors["services"] = ["Each service is either an OHIP fee code or an uninsured service."];
                else if (s.FeeCode is not null && !feeCodes.ContainsKey(s.FeeCode))
                    errors["services"] = [$"Unknown fee code {s.FeeCode}."];
                else if (s.UninsuredServiceId is { } u && !uninsured.ContainsKey(u))
                    errors["services"] = ["Unknown uninsured service."];
                else if (s.FeeCode is not null && e.PaymentProgram == PaymentPrograms.Uninsured)
                    errors["services"] = ["This visit is uninsured: OHIP fee codes can't be billed. Use the clinic's private price list."];
                else
                    services.Add(new EncounterService
                    {
                        // Fee holds the line total (fee × units), as it goes on the claim.
                        FeeCodeServiceCode = s.FeeCode, UninsuredServiceId = s.UninsuredServiceId, Units = s.Units, ServiceDate = serviceDate,
                        Fee = s.FeeCode is not null ? feeCodes[s.FeeCode].Fee * s.Units : uninsured[s.UninsuredServiceId!.Value].Price * s.Units,
                    });
            }
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            db.EncounterDiagnoses.RemoveRange(e.Diagnoses);
            db.EncounterServices.RemoveRange(e.Services);
            // The filtered unique index on the primary diagnosis needs the old rows gone before the new ones arrive.
            await db.SaveChangesAsync();
            e.Diagnoses.AddRange(req.Diagnoses.Select(d => new EncounterDiagnosis { DiagnosticCodeCode = d.Code.Trim(), IsPrimary = d.IsPrimary }));
            e.Services.AddRange(services);
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, id, clock, user));
        }).RequireAuthorization(Permissions.EncountersSign);

        api.MapPost("/{id:int}/sign", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var e = await db.Encounters.Include(x => x.Patient).Include(x => x.Provider).Include(x => x.Note).Include(x => x.Vitals)
                .Include(x => x.Diagnoses).Include(x => x.Services).ThenInclude(s => s.UninsuredService)
                .SingleOrDefaultAsync(x => x.Id == id);
            if (Locked(e) is { } locked)
                return locked;
            if (user.ProviderId() != e!.ProviderId)
                return Results.Problem("Only the treating provider can sign this visit.", statusCode: StatusCodes.Status403Forbidden);
            var missing = ReadyToSign(e);
            if (missing.Length > 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sign"] = missing });

            var now = clock.GetUtcNow().UtcDateTime;
            EncounterSigning.Sign(db, e, user.RequiredUserId(), now);

            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = e.SignedByUserId!.Value, PatientId = e.PatientId,
                Action = AccessActions.Edit, Context = $"Signed encounter {id}", IpAddress = AuthEndpoints.Ip(http) });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem("The visit changed while you were signing. Review it and sign again.", statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Ok(await Detail(db, id, clock, user));
        }).RequireAuthorization(Permissions.EncountersSign);

        // A signed note never changes; a correction is appended with its author and time.
        api.MapPost("/{id:int}/addendum", async (int id, AddendumRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var e = await db.Encounters.Include(x => x.Note).SingleOrDefaultAsync(x => x.Id == id);
            if (e is null)
                return Results.NotFound();
            if (e.Status != EncounterStatus.Signed || e.Note is null)
                return Results.Problem("Addenda are for signed visits; edit the note instead.", statusCode: StatusCodes.Status409Conflict);
            var text = Clean(req.Text);
            if (text is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Write the addendum."] });
            var now = clock.GetUtcNow().UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, DemoSeeder.Toronto);
            var entry = $"[{local:yyyy-MM-dd HH:mm}, {user.Identity!.Name}] {text}";
            e.Note.Addendum = e.Note.Addendum is null ? entry : e.Note.Addendum + "\n\n" + entry;
            await db.SaveChangesAsync();
            return Results.Ok(await Detail(db, id, clock, user));
        }).RequireAuthorization(Permissions.EncountersDocument);

        app.MapGet("/api/codes", async (ClinicHubDbContext db) => new
        {
            Diagnostic = await db.DiagnosticCodes.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Code).Select(d => new { d.Code, d.Description }).ToListAsync(),
            Fee = await db.FeeCodes.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.ServiceCode).Select(f => new { Code = f.ServiceCode, f.Description, f.Fee }).ToListAsync(),
            Uninsured = await db.UninsuredServices.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Name).Select(u => new { u.Id, u.Code, u.Name, u.Price }).ToListAsync(),
        }).WithTags("Reference").RequireAuthorization(Permissions.EncountersView);
    }

    /// <summary>What still stands between this visit and a signature. Empty when it can be signed.</summary>
    public static string[] ReadyToSign(Encounter e)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(e.Note?.Assessment))
            missing.Add("Write the assessment.");
        if (string.IsNullOrWhiteSpace(e.Note?.Plan))
            missing.Add("Write the plan.");
        if (!e.Diagnoses.Any(d => d.IsPrimary))
            missing.Add("Pick a primary diagnosis.");
        if (e.Services.Count == 0)
            missing.Add("Add at least one service (fee code or uninsured service).");
        return [.. missing];
    }

    private static IResult? Locked(Encounter? e) => e switch
    {
        null => Results.NotFound(),
        { Status: EncounterStatus.Signed } => Results.Problem("This visit is signed and locked. Add an addendum instead.", statusCode: StatusCodes.Status409Conflict),
        { Status: EncounterStatus.Voided } => Results.Problem("This visit was voided.", statusCode: StatusCodes.Status409Conflict),
        _ => null,
    };

    private static async Task<IResult> MoveAppointment(int id, string status, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock)
    {
        var e = await db.Encounters.Include(x => x.Appointment).SingleOrDefaultAsync(x => x.Id == id);
        if (Locked(e) is { } locked)
            return locked;
        var now = clock.GetUtcNow().UtcDateTime;
        if (e!.Appointment is { } appt)
        {
            if (!Allowed(appt.Status, status))
                return Results.Problem(status == AppointmentStatus.WithProvider ? "The patient isn't in a room yet." : "The visit hasn't started.",
                    statusCode: StatusCodes.Status409Conflict);
            Move(appt, status, now, user.RequiredUserId());
        }
        if (status == AppointmentStatus.WithProvider) e.StartedUtc ??= now;
        if (status == AppointmentStatus.Completed) e.EndedUtc = now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem("Someone else just moved this patient. Refresh and try again.", statusCode: StatusCodes.Status409Conflict);
        }
        return Results.Ok(await Detail(db, id, clock, user));
    }

    public static Dictionary<string, string[]> ValidateVitals(VitalsRequest v)
    {
        var errors = new Dictionary<string, string[]>();
        void Range<T>(string field, T? value, T min, T max, string label) where T : struct, IComparable<T>
        {
            if (value is { } x && (x.CompareTo(min) < 0 || x.CompareTo(max) > 0))
                errors[field] = [$"{label} must be between {min} and {max}."];
        }
        Range("systolicMmHg", v.SystolicMmHg, (short)50, (short)300, "Systolic");
        Range("diastolicMmHg", v.DiastolicMmHg, (short)20, (short)200, "Diastolic");
        Range("heartRate", v.HeartRate, (short)10, (short)300, "Heart rate");
        Range("respiratoryRate", v.RespiratoryRate, (short)4, (short)80, "Respiratory rate");
        Range("temperatureC", v.TemperatureC, 25m, 45m, "Temperature");
        Range("spO2", v.SpO2, (byte)50, (byte)100, "SpO₂");
        Range("weightKg", v.WeightKg, 0.3m, 500m, "Weight");
        Range("heightCm", v.HeightCm, 20m, 250m, "Height");
        if ((v.SystolicMmHg is null) != (v.DiastolicMmHg is null))
            errors["diastolicMmHg"] = ["Record both blood pressure numbers."];
        else if (v.SystolicMmHg <= v.DiastolicMmHg)
            errors["diastolicMmHg"] = ["Diastolic must be lower than systolic."];
        if (v is { SystolicMmHg: null, HeartRate: null, RespiratoryRate: null, TemperatureC: null, SpO2: null, WeightKg: null, HeightCm: null })
            errors["vitals"] = ["Enter at least one measurement."];
        return errors;
    }

    private static async Task<WorklistRow[]> Rows(IQueryable<Encounter> query, DateOnly today)
    {
        var rows = await query.Include(e => e.Patient).Include(e => e.Appointment!).ThenInclude(a => a.Room)
            .Include(e => e.Appointment!).ThenInclude(a => a.StatusChanges)
            .OrderBy(e => e.CheckInUtc)
            .Select(e => new { e, HasVitals = e.Vitals.Any(), HasNote = e.Note != null, Coded = e.Diagnoses.Count + e.Services.Count })
            .AsSplitQuery().ToListAsync();
        return rows.Select(r => new WorklistRow(r.e.Id, r.e.AppointmentId, Brief(r.e.Patient, today), r.e.ProviderId, r.e.Kind, r.e.Status,
            r.e.PaymentProgram, r.e.Appointment?.Status, r.e.Appointment?.Room?.Name, r.e.ReasonForVisit ?? r.e.Appointment?.Reason, r.e.CheckInUtc,
            r.e.Appointment?.StatusChanges.Where(c => c.Status == AppointmentStatus.Roomed).Select(c => (DateTime?)c.AtUtc).LastOrDefault(),
            r.e.StartedUtc, r.HasVitals, r.HasNote, r.Coded)).ToArray();
    }

    private static async Task<EncounterDetail> Detail(ClinicHubDbContext db, int id, TimeProvider clock, ClaimsPrincipal user)
    {
        db.ChangeTracker.Clear();
        var e = await db.Encounters.AsNoTracking().AsSplitQuery()
            .Include(x => x.Patient).Include(x => x.Provider).Include(x => x.Appointment!).ThenInclude(a => a.Room)
            .Include(x => x.EligibilityCheck).Include(x => x.Vitals).Include(x => x.Note)
            .Include(x => x.Diagnoses).ThenInclude(d => d.DiagnosticCode)
            .Include(x => x.Services).ThenInclude(s => s.FeeCode).Include(x => x.Services).ThenInclude(s => s.UninsuredService)
            .SingleAsync(x => x.Id == id);
        var userIds = e.Vitals.Select(v => v.TakenByUserId).Append(e.Note?.AuthorUserId ?? 0).Append(e.SignedByUserId ?? 0).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        string Name(int? userId) => userId is { } u && names.TryGetValue(u, out var n) ? n : "";

        var today = Today(clock);
        var p = e.Patient;
        var brief = Brief(p, today);
        var card = p.HealthCardNumber is { } hcn ? $"•••• ••• {hcn[7..]}{(p.HealthCardVersion is { } v ? " " + v : "")}" : null;

        var history = await db.Encounters.AsNoTracking().Where(x => x.PatientId == e.PatientId && x.Id != id && x.Status != EncounterStatus.Voided)
            .OrderByDescending(x => x.CheckInUtc).Take(10)
            .Select(x => new
            {
                x.Id, x.CheckInUtc, x.Status, x.ProviderId, Provider = x.Provider.LastName, x.Provider.Title,
                Dx = x.Diagnoses.Where(d => d.IsPrimary).Select(d => d.DiagnosticCode.Description).FirstOrDefault(),
                Assessment = x.Note != null ? x.Note.Assessment : null,
            }).ToListAsync();
        // Earlier visits the patient withheld from this person are left out; the page says how many.
        var consent = await ConsentView.LoadAsync(db, e.PatientId, user, clock.GetUtcNow().UtcDateTime);
        var shown = history.Where(h => !consent.Decide(h.Id, h.ProviderId, h.Status).Withheld).ToList();

        BillingDto? billing = null;
        if (e.Status == EncounterStatus.Signed)
        {
            var claim = await db.Claims.AsNoTracking().Where(c => c.EncounterId == id).Select(c => new { c.AccountingNumber, c.Status, c.AmountSubmitted }).FirstOrDefaultAsync();
            var invoice = await db.Invoices.AsNoTracking().Where(i => i.EncounterId == id).Select(i => new { i.Number, i.Total }).FirstOrDefaultAsync();
            billing = new BillingDto(claim?.AccountingNumber, claim?.Status, claim?.AmountSubmitted, invoice?.Number, invoice?.Total);
        }

        var services = e.Services.OrderBy(s => s.Id).Select(s => s.FeeCode is { } f
            ? new ServiceDto(f.ServiceCode, null, f.ServiceCode, f.Description, s.Units, s.Fee, true)
            : new ServiceDto(null, s.UninsuredServiceId, s.UninsuredService!.Code, s.UninsuredService.Name, s.Units, s.Fee, false)).ToArray();

        return new EncounterDetail(e.Id, e.Status, e.Kind, e.PaymentProgram, e.ReasonForVisit ?? e.Appointment?.Reason, e.CheckInUtc, e.StartedUtc,
            e.EndedUtc, e.SignedUtc, e.SignedByUserId is null ? null : Name(e.SignedByUserId),
            new Banner(p.Id, p.DisplayName, p.Mrn, brief.Age, p.Sex, p.DateOfBirth, brief.Coverage, card),
            new ProviderBrief(e.Provider.Id, e.Provider.DisplayName, e.Provider.Kind, e.Provider.SpecialtyName),
            e.AppointmentId, e.Appointment?.Status, e.Appointment?.Room?.Name,
            e.EligibilityCheck is { } el ? new EligibilityBrief(el.ResponseCode, el.IsEligible, el.Message, e.PaymentProgram) : null,
            e.Vitals.OrderByDescending(v => v.TakenUtc).Select(v => new VitalsDto(v.Id, v.TakenUtc, v.SystolicMmHg, v.DiastolicMmHg, v.HeartRate,
                v.RespiratoryRate, v.TemperatureC, v.SpO2, v.WeightKg, v.HeightCm, Bmi(v.WeightKg, v.HeightCm), Name(v.TakenByUserId))).ToArray(),
            e.Note is { } n ? new NoteDto(n.Subjective, n.Objective, n.Assessment, n.Plan, n.Addendum, n.UpdatedUtc, Name(n.AuthorUserId)) : null,
            e.Diagnoses.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Id).Select(d => new DiagnosisDto(d.DiagnosticCodeCode, d.DiagnosticCode.Description, d.IsPrimary)).ToArray(),
            services, services.Where(s => s.Insured).Sum(s => s.Fee), services.Where(s => !s.Insured).Sum(s => s.Fee),
            e.Status == EncounterStatus.Open ? ReadyToSign(e) : [], billing,
            shown.Select(h => new HistoryRow(h.Id, h.CheckInUtc, $"{h.Title} {h.Provider}".Trim(), h.Status, h.Dx, h.Assessment)).ToArray(),
            history.Count - shown.Count);
    }

    public static decimal? Bmi(decimal? weightKg, decimal? heightCm) =>
        weightKg is { } w && heightCm is { } h && h > 0 ? Math.Round(w / (h / 100m * (h / 100m)), 1) : null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int? ProviderId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.ProviderId), out var id) ? id : null;
}
