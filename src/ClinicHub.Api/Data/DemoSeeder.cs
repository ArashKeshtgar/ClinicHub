using System.Security.Cryptography;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Data;

public class DemoOptions
{
    /// <summary>Seeds a fictional clinic with synthetic patients and one-click demo users. Never with real patients.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// A fictional Toronto walk-in and family practice clinic. Every person is synthetic; health card
/// numbers have valid check digits but are random. The day's arrivals follow the hourly curve of the
/// legacy system (peaks 08–11 and 15–18), scaled to one clinic.
/// </summary>
public static class DemoSeeder
{
    public static readonly string[] DemoUsers = ["frontdesk", "nurse", "dr.patel", "billing", "privacy", "admin"];
    public static TimeZoneInfo Toronto { get; } = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    public static async Task SeedAsync(ClinicHubDbContext db, IPasswordHasher<User> hasher, DateTime nowUtc)
    {
        if (!await db.Locations.AnyAsync())
            await SeedClinic(db, hasher, nowUtc);

        // Keep a rolling week of appointments: missing days are generated on start-up.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Toronto));
        for (var day = today; day <= today.AddDays(7); day = day.AddDays(1))
            await SeedDay(db, day, nowUtc);
        await SeedEncounters(db, today);
    }

    /// <summary>
    /// Today's patients who have already arrived get the encounter check-in would have opened, and those
    /// roomed get the nurse's vitals, so the clinician's worklist starts mid-day like the front desk does.
    /// </summary>
    private static async Task SeedEncounters(ClinicHubDbContext db, DateOnly today)
    {
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), Toronto);
        string[] underway = [AppointmentStatus.Arrived, AppointmentStatus.Roomed, AppointmentStatus.WithProvider, AppointmentStatus.Completed];
        var appts = await db.Appointments.Include(a => a.Patient).Include(a => a.AppointmentType).Include(a => a.StatusChanges)
            .Where(a => a.StartUtc >= dayStartUtc && a.StartUtc < dayStartUtc.AddDays(1) && underway.Contains(a.Status)
                && !db.Encounters.Any(e => e.AppointmentId == a.Id))
            .ToListAsync();
        if (appts.Count == 0)
            return;
        var reciprocal = await db.Provinces.ToDictionaryAsync(p => p.Code, p => p.ReciprocalBilling);
        var deskId = await db.Users.Where(u => u.UserName == "frontdesk").Select(u => u.Id).FirstAsync();
        var nurseId = await db.Users.Where(u => u.UserName == "nurse").Select(u => u.Id).FirstAsync();
        var rng = new Random(today.DayNumber + 7);

        foreach (var a in appts)
        {
            DateTime? At(string s) => a.StatusChanges.Where(c => c.Status == s).Select(c => (DateTime?)c.AtUtc).LastOrDefault();
            var arrived = At(AppointmentStatus.Arrived) ?? a.StartUtc;
            var hcv = HcvSimulator.Check(a.Patient, null, today, reciprocal);
            var encounter = new Encounter
            {
                PatientId = a.PatientId, ProviderId = a.ProviderId, LocationId = a.LocationId, AppointmentId = a.Id, ReasonForVisit = a.Reason,
                PaymentProgram = hcv.PaymentProgram, CheckInUtc = arrived, StartedUtc = At(AppointmentStatus.WithProvider), EndedUtc = At(AppointmentStatus.Completed),
                Kind = a.Source == AppointmentSources.WalkIn ? EncounterKinds.WalkIn : a.AppointmentType.Name == "Virtual visit" ? EncounterKinds.Virtual : EncounterKinds.Office,
                EligibilityCheck = new EligibilityCheck
                {
                    PatientId = a.PatientId, CheckedUtc = arrived, HealthCardNumber = a.Patient.HealthCardNumber ?? "", HealthCardVersion = a.Patient.HealthCardVersion,
                    ResponseCode = hcv.Code, IsEligible = hcv.Eligible, Message = hcv.Message, CheckedByUserId = deskId,
                },
            };
            if (At(AppointmentStatus.Roomed) is { } roomed && encounter.Kind != EncounterKinds.Virtual)
            {
                var systolic = (short)rng.Next(104, 152);
                encounter.Vitals.Add(new VitalSigns
                {
                    TakenUtc = roomed.AddMinutes(3), TakenByUserId = nurseId, SystolicMmHg = systolic, DiastolicMmHg = (short)(systolic - rng.Next(35, 55)),
                    HeartRate = (short)rng.Next(58, 98), RespiratoryRate = (short)rng.Next(12, 19), TemperatureC = 36.4m + rng.Next(0, 16) / 10m,
                    SpO2 = (byte)rng.Next(95, 100), WeightKg = rng.Next(52, 105), HeightCm = rng.Next(152, 192),
                });
            }
            db.Encounters.Add(encounter);
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedClinic(ClinicHubDbContext db, IPasswordHasher<User> hasher, DateTime nowUtc)
    {
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name);
        User NewUser(string userName, string name, params string[] roleNames)
        {
            var u = new User
            {
                UserName = userName, DisplayName = name, CreatedUtc = nowUtc,
                Roles = roleNames.Select(r => new UserRole { Role = roles[r] }).ToList(),
            };
            u.PasswordHash = hasher.HashPassword(u, RandomNumberGenerator.GetHexString(32)); // demo users use one-click sign-in
            return u;
        }

        var location = new Location
        {
            Name = "Harbourview Medical Centre (demo)", AddressLine1 = "100 Example Street, Suite 200", City = "Toronto",
            Province = "ON", PostalCode = "M5V 0A1", Phone = "(416) 555-0142", OhipGroupNumber = "AB12",
            Rooms = [.. Enumerable.Range(1, 6).Select(i => new Room { Name = $"Exam {i}" }),
                new Room { Name = "Procedure", Kind = RoomKinds.Procedure },
                new Room { Name = "Lab collection", Kind = RoomKinds.LabCollection },
                new Room { Name = "ECG", Kind = RoomKinds.Diagnostics }],
        };
        db.Locations.Add(location);

        var drPatel = NewUser("dr.patel", "Dr. Nadia Patel", SystemRoles.Physician);
        var users = new[]
        {
            NewUser("frontdesk", "Jordan Lee", SystemRoles.FrontDesk), NewUser("nurse", "Sam Okafor", SystemRoles.Nurse),
            drPatel, NewUser("billing", "Chris Moreau", SystemRoles.Billing), NewUser("privacy", "Alex Tremblay", SystemRoles.PrivacyOfficer),
            NewUser("admin", "Clinic Administrator", SystemRoles.Administrator),
        };
        db.Users.AddRange(users);

        Provider Doc(string first, string last, string billing, string specialtyCode, string specialty, decimal overhead, User? user = null) => new()
        {
            Title = "Dr.", FirstName = first, LastName = last, Kind = ProviderKinds.Physician, RegistrationNumber = $"9{billing[..4]}",
            OhipBillingNumber = billing, SpecialtyCode = specialtyCode, SpecialtyName = specialty, OverheadPercent = overhead, User = user,
        };
        var providers = new[]
        {
            Doc("Nadia", "Patel", "123456", "00", "Family practice", 30, drPatel),
            Doc("Marcus", "Chen", "234567", "00", "Family practice", 30),
            Doc("Elena", "Rossi", "345678", "00", "Family practice", 30),
            Doc("Omar", "Haddad", "456789", "13", "Internal medicine", 25),
            new Provider { FirstName = "Grace", LastName = "Mensah", Kind = ProviderKinds.NursePractitioner, RegistrationNumber = "NP7731",
                SpecialtyName = "Primary care", OverheadPercent = 0 },
        };
        db.Providers.AddRange(providers);
        await db.SaveChangesAsync();

        var from = new DateOnly(2026, 1, 1);
        foreach (var p in providers)
            foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                db.ScheduleTemplates.Add(new ScheduleTemplate { ProviderId = p.Id, LocationId = location.Id, DayOfWeek = day,
                    StartTime = new(8, 0), EndTime = new(12, 0), SlotMinutes = 15, AcceptsWalkIns = p.LastName != "Haddad", EffectiveFrom = from });
                db.ScheduleTemplates.Add(new ScheduleTemplate { ProviderId = p.Id, LocationId = location.Id, DayOfWeek = day,
                    StartTime = new(13, 0), EndTime = new(18, 0), SlotMinutes = 15, AcceptsWalkIns = p.LastName != "Haddad", EffectiveFrom = from });
            }
        // Weekend walk-in clinic: two providers, 09:00–15:00. Dr. Patel is one so the demo physician has patients every day.
        foreach (var p in providers.Where(p => p.LastName is "Patel" or "Mensah"))
            foreach (var day in new[] { DayOfWeek.Saturday, DayOfWeek.Sunday })
                db.ScheduleTemplates.Add(new ScheduleTemplate { ProviderId = p.Id, LocationId = location.Id, DayOfWeek = day,
                    StartTime = new(9, 0), EndTime = new(15, 0), SlotMinutes = 15, AcceptsWalkIns = true, EffectiveFrom = from });

        db.Patients.AddRange(SyntheticPatients(400, nowUtc));
        await db.SaveChangesAsync();
    }

    private static readonly string[] FemaleNames =
    [
        "Olivia", "Emma", "Ava", "Sophia", "Mia", "Chloe", "Zoe", "Priya", "Mei", "Fatima", "Isabella", "Amara", "Leila",
        "Anya", "Giulia", "Aisha", "Claire", "Nora", "Hana", "Rosa",
    ];

    private static readonly string[] MaleNames =
    [
        "Liam", "Noah", "Ethan", "Lucas", "Benjamin", "Daniel", "Arjun", "Wei", "Hassan", "Mateo", "Kwame", "Yusuf", "Dmitri",
        "Luca", "Sanjay", "Thomas", "Julien", "Owen", "Kenji", "Diego",
    ];

    private static readonly string[] NeutralNames = ["Alex", "Sam", "Jordan", "Robin", "Charlie", "Rowan"];

    private static readonly string[] LastNames =
    [
        "Smith", "Brown", "Tremblay", "Martin", "Roy", "Wilson", "MacDonald", "Taylor", "Campbell", "Anderson", "Singh", "Li",
        "Wong", "Nguyen", "Kim", "Ahmed", "Ali", "Khan", "Garcia", "Rodriguez", "Santos", "Silva", "Kowalski", "Novak", "Murphy",
        "O'Brien", "Gauthier", "Lavoie", "Fraser", "Grant", "Osei", "Adeyemi", "Haddad", "Karimi", "Rahimi", "Sato", "Russo", "Costa",
    ];

    private static readonly (string City, string Prefix)[] Places =
    [
        ("Toronto", "M5V"), ("Toronto", "M4C"), ("Toronto", "M6H"), ("North York", "M2N"), ("Scarborough", "M1P"),
        ("Etobicoke", "M9B"), ("Mississauga", "L5B"), ("Brampton", "L6T"), ("Markham", "L3R"), ("Vaughan", "L4K"),
    ];

    public static IEnumerable<Patient> SyntheticPatients(int count, DateTime nowUtc)
    {
        var rng = new Random(20261010);
        for (var i = 1; i <= count; i++)
        {
            var sex = rng.NextDouble() < 0.52 ? "F" : rng.NextDouble() < 0.98 ? "M" : "X";
            var names = sex switch { "F" => FemaleNames, "M" => MaleNames, _ => NeutralNames };
            var first = names[rng.Next(names.Length)];
            var last = LastNames[rng.Next(LastNames.Length)];
            var place = Places[rng.Next(Places.Length)];
            var roll = rng.NextDouble();
            // 93% OHIP, 4% other province (reciprocal billing), 3% no provincial coverage.
            var (card, version, coverage) = roll < 0.93 ? (NewHealthCard(rng), RandomVersion(rng), "ON")
                : roll < 0.97 ? ((string?)null, (string?)null, (string?)new[] { "BC", "AB", "NS", "MB" }[rng.Next(4)])
                : (null, null, null);
            var dob = new DateOnly(1940, 1, 1).AddDays(rng.Next(0, 365 * 84));
            yield return new Patient
            {
                Mrn = $"HV{i:000000}", FirstName = first, LastName = last, DateOfBirth = dob,
                Sex = sex,
                HealthCardNumber = card, HealthCardVersion = version, CoverageProvince = coverage,
                HealthCardExpiry = card is null ? null : DateOnly.FromDateTime(nowUtc).AddDays(rng.Next(-60, 5 * 365)),
                Phone = $"(416) 555-{rng.Next(1000, 9999)}", City = place.City, Province = "ON",
                PostalCode = $"{place.Prefix} {rng.Next(1, 9)}{(char)('A' + rng.Next(0, 20))}{rng.Next(0, 9)}",
                PreferredLanguage = rng.NextDouble() < 0.06 ? "fr" : "en", CreatedUtc = nowUtc, UpdatedUtc = nowUtc,
            };
        }
    }

    /// <summary>A random 10-digit number with a valid Luhn check digit (not a real person's card).</summary>
    public static string NewHealthCard(Random rng)
    {
        while (true)
        {
            var body = rng.Next(1, 10) + string.Concat(Enumerable.Range(0, 8).Select(_ => rng.Next(0, 10)));
            for (var check = 0; check <= 9; check++)
                if (HealthCard.IsValidNumber(body + check))
                    return body + check;
        }
    }

    private static string RandomVersion(Random rng) => $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}";

    /// <summary>Arrivals per hour from the legacy system (2016–2023, outpatient side), 08:00–17:00.</summary>
    private static readonly double[] HourlyWeight = [84, 85, 79, 60, 0, 30, 36, 50, 51, 50];

    private static async Task SeedDay(ClinicHubDbContext db, DateOnly day, DateTime nowUtc)
    {
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Toronto);
        if (await db.Appointments.AnyAsync(a => a.StartUtc >= dayStartUtc && a.StartUtc < dayStartUtc.AddDays(1)))
            return;

        var rng = new Random(day.DayNumber);
        var location = await db.Locations.OrderBy(l => l.Id).FirstAsync();
        var sessions = await db.ScheduleTemplates.Include(t => t.Provider)
            .Where(t => t.DayOfWeek == day.DayOfWeek && t.Provider.IsActive).OrderBy(t => t.ProviderId).ThenBy(t => t.StartTime).ToListAsync();
        var types = await db.AppointmentTypes.ToDictionaryAsync(t => t.Name);
        var patientIds = await db.Patients.Where(p => p.MergedIntoPatientId == null).Select(p => p.Id).ToListAsync();
        var creator = await db.Users.Where(u => u.UserName == "frontdesk").Select(u => u.Id).FirstAsync();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Toronto);
        var isToday = DateOnly.FromDateTime(nowLocal) == day;
        var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var queue = 0;

        foreach (var session in sessions)
        {
            var provider = session.Provider;
            for (var slotStart = session.StartTime; slotStart < session.EndTime; slotStart = slotStart.AddMinutes(session.SlotMinutes))
            {
                // Busier hours fill more slots, following the legacy arrival curve.
                var hourIndex = slotStart.Hour - 8;
                var weight = hourIndex >= 0 && hourIndex < HourlyWeight.Length && HourlyWeight[hourIndex] > 0 ? HourlyWeight[hourIndex] : 40;
                if (rng.NextDouble() > weight / 85.0 * 0.9)
                    continue;
                var walkIn = session.AcceptsWalkIns && rng.NextDouble() < (weekend ? 0.75 : 0.35);
                var type = walkIn ? types["Walk-in visit"]
                    : provider.SpecialtyCode == "13" ? types["Follow-up"]
                    : rng.NextDouble() switch { < 0.6 => types["Follow-up"], < 0.75 => types["Annual physical"], < 0.85 => types["Virtual visit"], < 0.93 => types["Prenatal visit"], _ => types["ECG"] };
                var start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(slotStart), Toronto);
                var appt = new Appointment
                {
                    PatientId = patientIds[rng.Next(patientIds.Count)], ProviderId = provider.Id, LocationId = location.Id,
                    AppointmentTypeId = type.Id, StartUtc = start, EndUtc = start.AddMinutes(type.DurationMinutes),
                    Source = walkIn ? AppointmentSources.WalkIn : rng.NextDouble() < 0.4 ? AppointmentSources.Online : AppointmentSources.Phone,
                    QueueNumber = walkIn ? 0 : null, // numbered by time below
                    Reason = walkIn ? WalkInReasons[rng.Next(WalkInReasons.Length)] : null,
                    Status = AppointmentStatus.Booked, CreatedByUserId = creator, CreatedUtc = nowUtc,
                };
                if (isToday)
                    ProgressTo(appt, nowUtc, rng, creator);
                db.Appointments.Add(appt);
            }
        }

        // Walk-in tickets go in order of arrival across all providers, as at a real front desk.
        foreach (var walkIn in db.Appointments.Local.Where(a => a.QueueNumber == 0).OrderBy(a => a.StartUtc))
            walkIn.QueueNumber = ++queue;
        await db.SaveChangesAsync();
    }

    private static readonly string[] WalkInReasons =
    [
        "Sore throat", "Cough and fever", "Ear pain", "Rash", "Back pain", "Prescription renewal", "Sick note", "Urinary symptoms",
        "Minor cut", "Headache", "Allergy flare", "Sprained ankle",
    ];

    /// <summary>Gives today's appointments a believable state for the current time, with the status history to match.</summary>
    private static void ProgressTo(Appointment a, DateTime nowUtc, Random rng, int userId)
    {
        void Step(string status, DateTime at)
        {
            a.Status = status;
            a.StatusChanges.Add(new AppointmentStatusChange { Status = status, AtUtc = at, UserId = userId });
        }

        var minutesUntil = (a.StartUtc - nowUtc).TotalMinutes;
        if (minutesUntil > 20)
            return; // still ahead
        if (minutesUntil < -25)
        {
            if (rng.NextDouble() < 0.06) { Step(AppointmentStatus.NoShow, a.StartUtc.AddMinutes(20)); return; }
            var arrived = a.StartUtc.AddMinutes(rng.Next(-15, 6));
            Step(AppointmentStatus.Arrived, arrived);
            Step(AppointmentStatus.Roomed, arrived.AddMinutes(rng.Next(5, 25)));
            Step(AppointmentStatus.WithProvider, arrived.AddMinutes(rng.Next(25, 40)));
            Step(AppointmentStatus.Completed, arrived.AddMinutes(rng.Next(40, 60)));
            return;
        }
        // Around now: spread across the waiting room, exam rooms and the provider.
        var arrivedAt = a.StartUtc.AddMinutes(-rng.Next(0, 15));
        if (arrivedAt > nowUtc) return;
        Step(AppointmentStatus.Arrived, arrivedAt);
        if (minutesUntil < 0 && rng.NextDouble() < 0.6)
            Step(AppointmentStatus.Roomed, Min(nowUtc, arrivedAt.AddMinutes(rng.Next(5, 20))));
        if (minutesUntil < -10 && a.Status == AppointmentStatus.Roomed && rng.NextDouble() < 0.5)
            Step(AppointmentStatus.WithProvider, Min(nowUtc, arrivedAt.AddMinutes(rng.Next(20, 30))));
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
