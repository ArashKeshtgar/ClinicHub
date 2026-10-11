using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

/// <summary>The front desk's day: the schedule, check-in with health card validation, and patient flow.</summary>
public static class FrontDeskEndpoints
{
    public record PatientBrief(int Id, string Name, string Mrn, int Age, string Sex, string Coverage);
    public record TypeBrief(string Name, string Color, int Minutes);
    public record EligibilityBrief(string Code, bool Eligible, string Message, string PaymentProgram);

    public record AppointmentRow(
        int Id, DateTime StartUtc, DateTime EndUtc, string Status, string Source, int? QueueNumber, string? Reason,
        int ProviderId, int? RoomId, string? Room, TypeBrief Type, PatientBrief Patient,
        DateTime? ArrivedUtc, DateTime? RoomedUtc, DateTime? WithProviderUtc, DateTime? CompletedUtc,
        int? EncounterId, EligibilityBrief? Eligibility);

    public record ProviderBrief(int Id, string Name, string Kind, string? Specialty);
    public record RoomBrief(int Id, string Name, string Kind);
    public record DayView(DateOnly Date, int LocationId, string Location, ProviderBrief[] Providers, RoomBrief[] Rooms, AppointmentRow[] Appointments);

    public record CheckInRequest(string? PresentedVersion);
    public record StatusRequest(string Status, int? RoomId, string? Reason);
    public record WalkInRequest(int PatientId, int? ProviderId, string? Reason);

    public static void MapFrontDeskEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/frontdesk").WithTags("Front desk");

        api.MapGet("/day", async (DateOnly? date, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var day = date ?? Today(clock);
            return await LoadDay(db, day, clock);
        }).RequireAuthorization(Permissions.ScheduleView);

        api.MapPost("/appointments/{id:int}/check-in", async (int id, CheckInRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var appt = await db.Appointments.Include(a => a.Patient).Include(a => a.AppointmentType).SingleOrDefaultAsync(a => a.Id == id);
            if (appt is null)
                return Results.NotFound();
            if (appt.Status != AppointmentStatus.Booked)
                return Results.Problem($"Already {Words(appt.Status)}.", statusCode: StatusCodes.Status409Conflict);

            var now = clock.GetUtcNow().UtcDateTime;
            var userId = user.RequiredUserId();
            var reciprocal = await db.Provinces.ToDictionaryAsync(p => p.Code, p => p.ReciprocalBilling);
            var result = HcvSimulator.Check(appt.Patient, req.PresentedVersion, Today(clock), reciprocal);

            var check = new EligibilityCheck
            {
                PatientId = appt.PatientId, CheckedUtc = now, HealthCardNumber = appt.Patient.HealthCardNumber ?? "",
                HealthCardVersion = req.PresentedVersion ?? appt.Patient.HealthCardVersion, ResponseCode = result.Code,
                IsEligible = result.Eligible, Message = result.Message, CheckedByUserId = userId,
            };
            db.Encounters.Add(new Encounter
            {
                PatientId = appt.PatientId, ProviderId = appt.ProviderId, LocationId = appt.LocationId, Appointment = appt,
                EligibilityCheck = check, PaymentProgram = result.PaymentProgram, ReasonForVisit = appt.Reason, CheckInUtc = now,
                Kind = appt.Source == AppointmentSources.WalkIn ? EncounterKinds.WalkIn
                    : appt.AppointmentType.Name == "Virtual visit" ? EncounterKinds.Virtual : EncounterKinds.Office,
            });
            Move(appt, AppointmentStatus.Arrived, now, userId);
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = userId, PatientId = appt.PatientId, Action = AccessActions.Edit,
                Context = $"Check-in, appointment {appt.Id}", IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.Ok(new { eligibility = new EligibilityBrief(result.Code, result.Eligible, result.Message, result.PaymentProgram) });
        }).RequireAuthorization(Permissions.ScheduleManage);

        api.MapPost("/appointments/{id:int}/status", async (int id, StatusRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var appt = await db.Appointments.Include(a => a.Room).SingleOrDefaultAsync(a => a.Id == id);
            if (appt is null)
                return Results.NotFound();
            var now = clock.GetUtcNow().UtcDateTime;
            if (!Allowed(appt.Status, req.Status))
                return Results.Problem($"Can't go from {Words(appt.Status)} to {Words(req.Status)}.", statusCode: StatusCodes.Status409Conflict);

            if (req.Status == AppointmentStatus.Roomed)
            {
                var room = await db.Rooms.SingleOrDefaultAsync(r => r.Id == req.RoomId && r.LocationId == appt.LocationId && r.IsActive);
                if (room is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["roomId"] = ["Pick a room."] });
                var occupied = await db.Appointments.AnyAsync(a => a.RoomId == room.Id && a.Id != appt.Id
                    && (a.Status == AppointmentStatus.Roomed || a.Status == AppointmentStatus.WithProvider));
                if (occupied)
                    return Results.Problem($"{room.Name} is occupied.", statusCode: StatusCodes.Status409Conflict);
                appt.RoomId = room.Id;
            }
            if (req.Status == AppointmentStatus.NoShow && appt.StartUtc > now)
                return Results.Problem("A no-show can only be recorded after the appointment time.", statusCode: StatusCodes.Status409Conflict);
            if (req.Status == AppointmentStatus.Cancelled)
                appt.CancelReason = string.IsNullOrWhiteSpace(req.Reason) ? "Cancelled at front desk" : req.Reason.Trim();

            var encounter = await db.Encounters.SingleOrDefaultAsync(e => e.AppointmentId == appt.Id);
            if (encounter is not null)
            {
                if (req.Status == AppointmentStatus.WithProvider) encounter.StartedUtc ??= now;
                if (req.Status == AppointmentStatus.Completed) encounter.EndedUtc = now;
                if (req.Status == AppointmentStatus.Cancelled) encounter.Status = EncounterStatus.Voided;
            }
            if (req.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
                appt.RoomId = appt.Status == AppointmentStatus.Completed ? appt.RoomId : null;

            Move(appt, req.Status, now, user.RequiredUserId());
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem("Someone else just changed this appointment. Refresh and try again.", statusCode: StatusCodes.Status409Conflict);
            }
            return Results.NoContent();
        }).RequireAuthorization(Permissions.ScheduleManage);

        // A walk-in joins the queue with the provider who has the fewest people waiting.
        api.MapPost("/walk-ins", async (WalkInRequest req, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var (dayStart, dayEnd) = DayBounds(Today(clock));
            if (!await db.Patients.AnyAsync(p => p.Id == req.PatientId && p.MergedIntoPatientId == null))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["patientId"] = ["Pick a patient."] });
            var location = await db.Locations.OrderBy(l => l.Id).FirstAsync();
            var walkInProviders = await db.ScheduleTemplates
                .Where(t => t.AcceptsWalkIns && t.DayOfWeek == Today(clock).DayOfWeek).Select(t => t.ProviderId).Distinct().ToListAsync();
            if (walkInProviders.Count == 0)
                return Results.Problem("No provider takes walk-ins today.", statusCode: StatusCodes.Status409Conflict);
            var providerId = req.ProviderId is { } chosen && walkInProviders.Contains(chosen) ? chosen
                : (await db.Appointments.Where(a => a.StartUtc >= dayStart && a.StartUtc < dayEnd && walkInProviders.Contains(a.ProviderId)
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Arrived))
                    .GroupBy(a => a.ProviderId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
                  .Concat(walkInProviders.Select(p => new { Key = p, Count = 0 }))
                  .GroupBy(x => x.Key).Select(g => new { Provider = g.Key, Count = g.Sum(x => x.Count) })
                  .OrderBy(x => x.Count).First().Provider;
            var queue = (await db.Appointments.Where(a => a.StartUtc >= dayStart && a.StartUtc < dayEnd).MaxAsync(a => a.QueueNumber)) ?? 0;
            var type = await db.AppointmentTypes.SingleAsync(t => t.Name == "Walk-in visit");
            var appt = new Appointment
            {
                PatientId = req.PatientId, ProviderId = providerId, LocationId = location.Id, AppointmentTypeId = type.Id,
                StartUtc = now, EndUtc = now.AddMinutes(type.DurationMinutes), Source = AppointmentSources.WalkIn, QueueNumber = queue + 1,
                Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(), CreatedByUserId = user.RequiredUserId(), CreatedUtc = now,
            };
            db.Appointments.Add(appt);
            await db.SaveChangesAsync();
            return Results.Created($"/api/frontdesk/appointments/{appt.Id}", new { appt.Id, appt.QueueNumber });
        }).RequireAuthorization(Permissions.ScheduleManage);
    }

    private static async Task<DayView> LoadDay(ClinicHubDbContext db, DateOnly day, TimeProvider clock)
    {
        var (start, end) = DayBounds(day);
        var location = await db.Locations.AsNoTracking().OrderBy(l => l.Id).FirstAsync();
        var providers = await db.Providers.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Kind).ThenBy(p => p.LastName).ToListAsync();
        var rooms = await db.Rooms.AsNoTracking().Where(r => r.LocationId == location.Id && r.IsActive).OrderBy(r => r.Id).ToListAsync();
        var appts = await db.Appointments.AsNoTracking()
            .Include(a => a.Patient).Include(a => a.AppointmentType).Include(a => a.Room).Include(a => a.StatusChanges)
            .Where(a => a.LocationId == location.Id && a.StartUtc >= start && a.StartUtc < end)
            .OrderBy(a => a.StartUtc).ThenBy(a => a.ProviderId).ToListAsync();
        var ids = appts.Select(a => a.Id).ToList();
        var encounters = await db.Encounters.AsNoTracking().Include(e => e.EligibilityCheck)
            .Where(e => e.AppointmentId != null && ids.Contains(e.AppointmentId.Value)).ToDictionaryAsync(e => e.AppointmentId!.Value);
        var today = Today(clock);

        return new DayView(day, location.Id, location.Name,
            providers.Select(p => new ProviderBrief(p.Id, p.DisplayName, p.Kind, p.SpecialtyName)).ToArray(),
            rooms.Select(r => new RoomBrief(r.Id, r.Name, r.Kind)).ToArray(),
            appts.Select(a =>
            {
                DateTime? At(string s) => a.StatusChanges.Where(c => c.Status == s).Select(c => (DateTime?)c.AtUtc).LastOrDefault();
                encounters.TryGetValue(a.Id, out var enc);
                var el = enc?.EligibilityCheck;
                return new AppointmentRow(a.Id, a.StartUtc, a.EndUtc, a.Status, a.Source, a.QueueNumber, a.Reason, a.ProviderId,
                    a.RoomId, a.Room?.Name, new TypeBrief(a.AppointmentType.Name, a.AppointmentType.Color, a.AppointmentType.DurationMinutes),
                    Brief(a.Patient, today), At(AppointmentStatus.Arrived), At(AppointmentStatus.Roomed),
                    At(AppointmentStatus.WithProvider), At(AppointmentStatus.Completed), enc?.Id,
                    el is null ? null : new EligibilityBrief(el.ResponseCode, el.IsEligible, el.Message, enc!.PaymentProgram));
            }).ToArray());
    }

    internal static PatientBrief Brief(Patient p, DateOnly today)
    {
        var age = today.Year - p.DateOfBirth.Year - (today < p.DateOfBirth.AddYears(today.Year - p.DateOfBirth.Year) ? 1 : 0);
        var coverage = p.HealthCardNumber is not null ? "OHIP" : p.CoverageProvince ?? "None";
        return new PatientBrief(p.Id, p.DisplayName, p.Mrn, age, p.Sex, coverage);
    }

    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [AppointmentStatus.Booked] = [AppointmentStatus.NoShow, AppointmentStatus.Cancelled],
        [AppointmentStatus.Arrived] = [AppointmentStatus.Roomed, AppointmentStatus.Cancelled],
        [AppointmentStatus.Roomed] = [AppointmentStatus.WithProvider],
        [AppointmentStatus.WithProvider] = [AppointmentStatus.Completed],
    };

    /// <summary>Check-in (Booked → Arrived) has its own endpoint because it validates the health card.</summary>
    public static bool Allowed(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);

    internal static void Move(Appointment a, string status, DateTime now, int userId)
    {
        a.Status = status;
        a.StatusChanges.Add(new AppointmentStatusChange { Status = status, AtUtc = now, UserId = userId });
    }

    private static string Words(string status) => status switch
    {
        AppointmentStatus.WithProvider => "with the provider",
        AppointmentStatus.NoShow => "a no-show",
        _ => status.ToLowerInvariant(),
    };

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, DemoSeeder.Toronto));

    public static (DateTime Start, DateTime End) DayBounds(DateOnly day)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), DemoSeeder.Toronto);
        return (start, TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), DemoSeeder.Toronto));
    }
}
