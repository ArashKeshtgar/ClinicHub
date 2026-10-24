using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// A patient's allergy list. What is recorded here is checked when a drug is prescribed (see <see cref="DrugSafety"/>).
/// Anyone who may view patients reads it; whoever documents a visit records and ends entries. An entry is never deleted:
/// ending it keeps who did and why.
/// </summary>
public static class AllergyEndpoints
{
    public record AllergyDto(int Id, string? AllergenKey, string Substance, string? Reaction, string Severity, bool NoKnown, DateTime CreatedUtc, string RecordedBy);
    public record AllergyList(bool Recorded, bool NoKnownAllergies, AllergyDto[] Items);
    public record AllergenDto(string Key, string Label);
    public record AddAllergy(string? AllergenKey, string? Substance, string? Reaction, string? Severity);
    public record EndAllergy(string? Reason);

    public static void MapAllergyEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Allergies");

        api.MapGet("/allergens", () => DrugSafety.Allergens.Select(a => new AllergenDto(a.Key, a.Label)))
            .RequireAuthorization(Permissions.PatientsView);

        api.MapGet("/patients/{id:int}/allergies", async (int id, ClinicHubDbContext db) =>
            await db.Patients.AnyAsync(p => p.Id == id) ? Results.Ok(await List(db, id)) : Results.NotFound())
            .RequireAuthorization(Permissions.PatientsView);

        api.MapPost("/patients/{id:int}/allergies", async (int id, AddAllergy req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
            if (patient is null)
                return Results.NotFound();
            if (patient.MergedIntoPatientId is not null)
                return Results.Problem("This record was merged into another. Record it there.", statusCode: StatusCodes.Status409Conflict);
            var errors = new Dictionary<string, string[]>();
            var known = DrugSafety.Allergens.FirstOrDefault(a => a.Key == req.AllergenKey);
            var substance = known.Label ?? req.Substance?.Trim() ?? "";
            if (req.AllergenKey is not null && known.Key is null)
                errors["allergenKey"] = ["Unknown allergen."];
            if (substance.Length < 2 || substance.Length > 100)
                errors["substance"] = ["Say what the patient is allergic to (2 to 100 characters)."];
            if (req.Severity is not (AllergySeverities.Mild or AllergySeverities.Moderate or AllergySeverities.Severe))
                errors["severity"] = ["Mild, Moderate or Severe."];
            if (req.Reaction?.Trim().Length > 200)
                errors["reaction"] = ["At most 200 characters."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var now = clock.GetUtcNow().UtcDateTime;
            var active = await db.Allergies.Where(a => a.PatientId == id && a.EndedUtc == null).ToListAsync();
            if (active.Any(a => !a.NoKnown && string.Equals(a.Substance, substance, StringComparison.OrdinalIgnoreCase)))
                return Results.Problem($"{substance} is already on the list.", statusCode: StatusCodes.Status409Conflict);
            // An allergy replaces "no known allergies".
            foreach (var none in active.Where(a => a.NoKnown))
                (none.EndedUtc, none.EndedByUserId, none.EndReason) = (now, user.RequiredUserId(), "An allergy was recorded");
            db.Allergies.Add(new PatientAllergy
            {
                PatientId = id, AllergenKey = known.Key, Substance = substance, Severity = req.Severity!,
                Reaction = string.IsNullOrWhiteSpace(req.Reaction) ? null : req.Reaction.Trim(), RecordedByUserId = user.RequiredUserId(), CreatedUtc = now,
            });
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = id, Action = AccessActions.Edit,
                Context = $"Recorded allergy: {substance}", IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(await List(db, id));
        }).RequireAuthorization(Permissions.EncountersDocument);

        api.MapPost("/patients/{id:int}/allergies/none", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
            if (patient is null)
                return Results.NotFound();
            if (patient.MergedIntoPatientId is not null)
                return Results.Problem("This record was merged into another. Record it there.", statusCode: StatusCodes.Status409Conflict);
            var active = await db.Allergies.Where(a => a.PatientId == id && a.EndedUtc == null).ToListAsync();
            if (active.Any(a => !a.NoKnown))
                return Results.Problem("The patient has allergies on the list. End them first if they were entered in error.", statusCode: StatusCodes.Status409Conflict);
            if (active.Count == 0)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                db.Allergies.Add(new PatientAllergy
                {
                    PatientId = id, Substance = "No known allergies", NoKnown = true, RecordedByUserId = user.RequiredUserId(), CreatedUtc = now,
                });
                db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = id, Action = AccessActions.Edit,
                    Context = "Recorded: no known allergies", IpAddress = AuthEndpoints.Ip(http) });
                await db.SaveChangesAsync();
            }
            return Results.Ok(await List(db, id));
        }).RequireAuthorization(Permissions.EncountersDocument);

        api.MapPost("/allergies/{id:int}/end", async (int id, EndAllergy req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var allergy = await db.Allergies.SingleOrDefaultAsync(a => a.Id == id);
            if (allergy is null)
                return Results.NotFound();
            if (allergy.EndedUtc is not null)
                return Results.Problem("This entry was already ended.", statusCode: StatusCodes.Status409Conflict);
            var reason = req.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Say why (up to 200 characters), for the record."] });
            var now = clock.GetUtcNow().UtcDateTime;
            (allergy.EndedUtc, allergy.EndedByUserId, allergy.EndReason) = (now, user.RequiredUserId(), reason);
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = allergy.PatientId, Action = AccessActions.Edit,
                Context = $"Ended allergy entry: {allergy.Substance}", IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(await List(db, allergy.PatientId));
        }).RequireAuthorization(Permissions.EncountersDocument);
    }

    public static async Task<AllergyList> List(ClinicHubDbContext db, int patientId)
    {
        var rows = await db.Allergies.AsNoTracking().Where(a => a.PatientId == patientId && a.EndedUtc == null).OrderBy(a => a.NoKnown).ThenBy(a => a.Id).ToListAsync();
        var names = await db.Users.AsNoTracking().Where(u => rows.Select(r => r.RecordedByUserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        return new AllergyList(rows.Count > 0, rows.Any(r => r.NoKnown),
            rows.Where(r => !r.NoKnown).Select(r => new AllergyDto(r.Id, r.AllergenKey, r.Substance, r.Reaction, r.Severity, false, r.CreatedUtc,
                names.GetValueOrDefault(r.RecordedByUserId, ""))).ToArray());
    }
}
