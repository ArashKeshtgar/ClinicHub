using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// PHIPA consent directives: the privacy officer records what the patient withholds and from whom; clinicians see
/// "withheld at the patient's request" in its place; in an emergency a clinician may override, giving a reason, and
/// the privacy officer sees every override. The rules are in <see cref="ConsentView"/>.
/// </summary>
public static class ConsentEndpoints
{
    public const string OverrideItem = "consent.override.id";

    public record DirectiveDto(int Id, string Scope, int? EncounterId, DateTime? EncounterUtc, string? EncounterProvider, int? ExcludedProviderId,
        string? ExcludedProvider, string? Note, string RecordedBy, DateTime CreatedUtc, DateTime? RevokedUtc, int Overrides);
    public record VisitChoice(int Id, DateTime CheckInUtc, string Provider);
    public record ProviderChoice(int Id, string Name);
    public record ConsentInfo(DirectiveDto[] Directives, VisitChoice[] Visits, ProviderChoice[] Providers, DateTime? OverrideUntilUtc);
    public record NewDirective(string Scope, int? EncounterId, int? ExcludedProviderId, string? Note);
    public record OverrideRequest(string Reason);
    public record OverrideRow(int Id, DateTime AtUtc, DateTime ExpiresUtc, string User, int PatientId, string Patient, string Mrn, string Scope, string Reason);

    public static void MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Consent");

        // What the patient has asked; the visits and providers to choose from are only for whoever records directives.
        api.MapGet("/patients/{id:int}/consent", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            if (!await db.Patients.AnyAsync(p => p.Id == id))
                return Results.NotFound();
            var now = clock.GetUtcNow().UtcDateTime;
            var manage = user.Can(Permissions.ConsentManage);
            var userId = user.RequiredUserId();
            var until = await db.ConsentOverrides.Where(o => o.ConsentDirective.PatientId == id && o.UserId == userId && o.ExpiresUtc > now && o.ConsentDirective.RevokedUtc == null)
                .MaxAsync(o => (DateTime?)o.ExpiresUtc);
            return Results.Ok(new ConsentInfo(await Directives(db, id),
                manage ? await db.Encounters.AsNoTracking().Where(e => e.PatientId == id && e.Status != EncounterStatus.Voided).OrderByDescending(e => e.CheckInUtc).Take(50)
                    .Select(e => new VisitChoice(e.Id, e.CheckInUtc, e.Provider.LastName)).ToArrayAsync() : [],
                manage ? await db.Providers.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.LastName)
                    .Select(p => new ProviderChoice(p.Id, p.Title == null ? p.FirstName + " " + p.LastName : p.Title + " " + p.FirstName + " " + p.LastName)).ToArrayAsync() : [],
                until));
        }).RequireAuthorization(Permissions.PatientsView);

        api.MapPost("/patients/{id:int}/consent", async (int id, NewDirective req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == id);
            if (patient is null)
                return Results.NotFound();
            if (patient.MergedIntoPatientId is not null)
                return Results.Problem("This record was merged into another one; record the directive there.", statusCode: StatusCodes.Status409Conflict);
            var errors = new Dictionary<string, string[]>();
            var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            if (req.Scope is not (ConsentScopes.EntireRecord or ConsentScopes.Encounter or ConsentScopes.Provider))
                errors["scope"] = ["Choose what is withheld."];
            if (req.Scope == ConsentScopes.Encounter && !await db.Encounters.AnyAsync(e => e.Id == req.EncounterId && e.PatientId == id))
                errors["encounterId"] = ["Choose one of the patient's visits."];
            if (req.Scope == ConsentScopes.Provider && !await db.Providers.AnyAsync(p => p.Id == req.ExcludedProviderId))
                errors["excludedProviderId"] = ["Choose the provider."];
            if (note?.Length > 300)
                errors["note"] = ["At most 300 characters."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var now = clock.GetUtcNow().UtcDateTime;
            db.ConsentDirectives.Add(new ConsentDirective
            {
                PatientId = id, Scope = req.Scope, Note = note, RecordedByUserId = user.RequiredUserId(), CreatedUtc = now,
                EncounterId = req.Scope == ConsentScopes.Encounter ? req.EncounterId : null,
                ExcludedProviderId = req.Scope == ConsentScopes.Provider ? req.ExcludedProviderId : null,
            });
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = id, Action = AccessActions.Edit,
                Context = $"Consent directive recorded ({Words(req.Scope)})", IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(await Directives(db, id));
        }).RequireAuthorization(Permissions.ConsentManage);

        // The patient withdraws the directive. It stays, with the date, so the history of who could see what is kept.
        api.MapPost("/consent/{id:int}/revoke", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var d = await db.ConsentDirectives.SingleOrDefaultAsync(x => x.Id == id);
            if (d is null)
                return Results.NotFound();
            if (d.RevokedUtc is not null)
                return Results.Problem("Already withdrawn.", statusCode: StatusCodes.Status409Conflict);
            var now = clock.GetUtcNow().UtcDateTime;
            d.RevokedUtc = now;
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = d.PatientId, Action = AccessActions.Edit,
                Context = $"Consent directive {id} withdrawn", IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(await Directives(db, d.PatientId));
        }).RequireAuthorization(Permissions.ConsentManage);

        // Emergency: the whole record opens to this clinician for an hour. One override per active directive, so the
        // log says which wish was set aside.
        api.MapPost("/patients/{id:int}/consent/override", async (int id, OverrideRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var reason = req.Reason?.Trim() ?? "";
            if (reason.Length < 10 || reason.Length > 300)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Say why, in 10 to 300 characters."] });
            var directives = await db.ConsentDirectives.Where(d => d.PatientId == id && d.RevokedUtc == null).ToListAsync();
            if (directives.Count == 0)
                return Results.Problem("Nothing is withheld for this patient.", statusCode: StatusCodes.Status409Conflict);
            var now = clock.GetUtcNow().UtcDateTime;
            var userId = user.RequiredUserId();
            var overrides = directives.Select(d => new ConsentOverride
            {
                ConsentDirectiveId = d.Id, UserId = userId, Reason = reason, AtUtc = now, ExpiresUtc = now + ConsentView.OverrideLength,
            }).ToList();
            db.ConsentOverrides.AddRange(overrides);
            await db.SaveChangesAsync();
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = userId, PatientId = id, Action = AccessActions.View,
                Context = "Emergency override of consent directive", ConsentOverrideId = overrides[0].Id, IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(new { expiresUtc = now + ConsentView.OverrideLength });
        }).RequireAuthorization(Permissions.ConsentOverride);

        // For the privacy officer: every override, newest first.
        api.MapGet("/consent/overrides", async (ClinicHubDbContext db) =>
            await db.ConsentOverrides.AsNoTracking().OrderByDescending(o => o.AtUtc).Take(200)
                .Select(o => new OverrideRow(o.Id, o.AtUtc, o.ExpiresUtc,
                    db.Users.Where(u => u.Id == o.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? "",
                    o.ConsentDirective.PatientId, o.ConsentDirective.Patient.LastName + ", " + o.ConsentDirective.Patient.FirstName,
                    o.ConsentDirective.Patient.Mrn, o.ConsentDirective.Scope, o.Reason))
                .ToListAsync()).RequireAuthorization(Permissions.AuditView);
    }

    /// <summary>
    /// For every route with the visit's id: answers 403 when the visit is withheld from this person, and otherwise
    /// remembers the override that opened it (if any) for the access log.
    /// </summary>
    public static async ValueTask<object?> EncounterFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!int.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id))
            return await next(ctx);
        var db = http.RequestServices.GetRequiredService<ClinicHubDbContext>();
        var e = await db.Encounters.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.PatientId, x.ProviderId, x.Status, x.Patient.FirstName, x.Patient.LastName, x.Patient.Mrn }).SingleOrDefaultAsync();
        if (e is null)
            return await next(ctx);
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var decision = (await ConsentView.LoadAsync(db, e.PatientId, http.User, now)).Decide(id, e.ProviderId, e.Status);
        if (decision.Withheld)
            return Withheld(http.User, e.PatientId, $"{e.LastName}, {e.FirstName}", e.Mrn);
        http.Items[OverrideItem] = decision.OverrideId;
        return await next(ctx);
    }

    public static IResult Withheld(ClaimsPrincipal user, int patientId, string patient, string mrn) =>
        Results.Problem("Withheld at the patient's request (PHIPA consent directive).", statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?>
            {
                ["consentWithheld"] = true, ["canOverride"] = user.Can(Permissions.ConsentOverride),
                ["patientId"] = patientId, ["patient"] = patient, ["mrn"] = mrn,
            });

    public static int? OverrideId(HttpContext http) => http.Items.TryGetValue(OverrideItem, out var v) ? v as int? : null;

    private static string Words(string scope) => scope switch
    {
        ConsentScopes.Encounter => "one visit",
        ConsentScopes.Provider => "from one provider",
        _ => "entire record",
    };

    private static async Task<DirectiveDto[]> Directives(ClinicHubDbContext db, int patientId) =>
        await db.ConsentDirectives.AsNoTracking().Where(d => d.PatientId == patientId).OrderBy(d => d.RevokedUtc != null).ThenByDescending(d => d.CreatedUtc)
            .Select(d => new DirectiveDto(d.Id, d.Scope, d.EncounterId,
                db.Encounters.Where(e => e.Id == d.EncounterId).Select(e => (DateTime?)e.CheckInUtc).FirstOrDefault(),
                db.Encounters.Where(e => e.Id == d.EncounterId).Select(e => e.Provider.LastName).FirstOrDefault(),
                d.ExcludedProviderId,
                db.Providers.Where(p => p.Id == d.ExcludedProviderId).Select(p => p.Title == null ? p.FirstName + " " + p.LastName : p.Title + " " + p.FirstName + " " + p.LastName).FirstOrDefault(),
                d.Note, db.Users.Where(u => u.Id == d.RecordedByUserId).Select(u => u.DisplayName).FirstOrDefault() ?? "",
                d.CreatedUtc, d.RevokedUtc, d.Overrides.Count))
            .ToArrayAsync();
}
