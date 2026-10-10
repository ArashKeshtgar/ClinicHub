using System.Security.Cryptography;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Data;

public class DemoOptions
{
    /// <summary>Seeds fictional clinics with synthetic patients and one-click demo users. Never with real patients.</summary>
    public bool Enabled { get; set; }

    /// <summary>Public demo: accounts, roles, passwords and clinics stay as seeded, since every visitor can be an administrator.</summary>
    public bool LockAccounts { get; set; }
}

/// <summary>
/// Two fictional Toronto-area clinics, each in its own database: Harbourview (walk-in and family practice)
/// and Lakeshore (family health). Dr. Patel works at both, as Ontario physicians often split their week,
/// with one OHIP billing number and each clinic's group number. Every person is synthetic; health card
/// numbers have valid check digits but are random. Arrivals follow the hourly curve of the legacy system
/// (peaks 08–11 and 15–18), scaled to one clinic.
/// </summary>
public static class DemoSeeder
{
    public static readonly string[] DemoUsers = ["frontdesk", "nurse", "dr.patel", "billing", "privacy", "admin"];
    public static TimeZoneInfo Toronto { get; } = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    private static readonly DayOfWeek[] Weekend = [DayOfWeek.Saturday, DayOfWeek.Sunday];

    public record DemoAccount(string UserName, string DisplayName, string? Credentials = null, bool PlatformAdmin = false);
    public record DemoProvider(string? Title, string First, string Last, string Kind, string? Billing, string? SpecialtyCode, string Specialty,
        decimal Overhead, string UserName, DayOfWeek[] Days, bool WalkIns = true);
    public record DemoClinic(string Slug, string Name, string Address, string City, string PostalCode, string Phone, string Group,
        int ExamRooms, int Patients, string MrnPrefix, int Seed, string DeskUser, string NurseUser,
        (string UserName, string Role)[] Members, DemoProvider[] Providers);

    public static readonly DemoAccount[] Accounts =
    [
        new("frontdesk", "Jordan Lee"), new("nurse", "Sam Okafor", "RN"), new("dr.patel", "Dr. Nadia Patel", "MD, CCFP"),
        new("billing", "Chris Moreau"), new("privacy", "Alex Tremblay"), new("admin", "Clinic Administrator", PlatformAdmin: true),
        new("dr.chen", "Dr. Marcus Chen", "MD, CCFP"), new("dr.rossi", "Dr. Elena Rossi", "MD, CCFP"), new("dr.haddad", "Dr. Omar Haddad", "MD, FRCPC"),
        new("np.mensah", "Grace Mensah", "NP-PHC"), new("ls.frontdesk", "Riley Morgan"), new("ls.nurse", "Priya Nair", "RPN"),
        new("dr.okoye", "Dr. Hannah Okoye", "MD, CCFP"), new("np.fraser", "Liam Fraser", "NP-PHC"),
    ];

    public static readonly DemoClinic[] Clinics =
    [
        new("harbourview", "Harbourview Medical Centre (demo)", "100 Example Street, Suite 200", "Toronto", "M5V 0A1", "(416) 555-0142", "AB12",
            6, 400, "HV", 20261010, "frontdesk", "nurse",
            [("frontdesk", SystemRoles.FrontDesk), ("nurse", SystemRoles.Nurse), ("dr.patel", SystemRoles.Physician), ("billing", SystemRoles.Billing),
             ("privacy", SystemRoles.PrivacyOfficer), ("admin", SystemRoles.Administrator), ("dr.chen", SystemRoles.Physician),
             ("dr.rossi", SystemRoles.Physician), ("dr.haddad", SystemRoles.Physician), ("np.mensah", SystemRoles.Physician)],
            [
                // Dr. Patel: Monday, Tuesday, Thursday and the weekend walk-in clinic here; Wednesday and Friday at Lakeshore.
                new("Dr.", "Nadia", "Patel", ProviderKinds.Physician, "123456", "00", "Family practice", 30, "dr.patel",
                    [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Thursday, .. Weekend]),
                new("Dr.", "Marcus", "Chen", ProviderKinds.Physician, "234567", "00", "Family practice", 30, "dr.chen", Weekdays),
                new("Dr.", "Elena", "Rossi", ProviderKinds.Physician, "345678", "00", "Family practice", 30, "dr.rossi", Weekdays),
                new("Dr.", "Omar", "Haddad", ProviderKinds.Physician, "456789", "13", "Internal medicine", 25, "dr.haddad", Weekdays, WalkIns: false),
                new(null, "Grace", "Mensah", ProviderKinds.NursePractitioner, null, null, "Primary care", 0, "np.mensah", [.. Weekdays, .. Weekend]),
            ]),
        new("lakeshore", "Lakeshore Family Health (demo)", "2500 Example Boulevard West", "Etobicoke", "M8V 1A1", "(416) 555-0187", "CD34",
            4, 250, "LS", 20261012, "ls.frontdesk", "ls.nurse",
            [("ls.frontdesk", SystemRoles.FrontDesk), ("ls.nurse", SystemRoles.Nurse), ("dr.patel", SystemRoles.Physician),
             ("admin", SystemRoles.Administrator), ("dr.okoye", SystemRoles.Physician), ("np.fraser", SystemRoles.Physician)],
            [
                new("Dr.", "Nadia", "Patel", ProviderKinds.Physician, "123456", "00", "Family practice", 30, "dr.patel", [DayOfWeek.Wednesday, DayOfWeek.Friday]),
                new("Dr.", "Hannah", "Okoye", ProviderKinds.Physician, "567890", "00", "Family practice", 28, "dr.okoye", [.. Weekdays, DayOfWeek.Saturday]),
                new(null, "Liam", "Fraser", ProviderKinds.NursePractitioner, null, null, "Primary care", 0, "np.fraser", [.. Weekdays, .. Weekend]),
            ]),
    ];

    /// <summary>Creates what is missing: demo accounts, the demo clinics and their databases, memberships, then each clinic's data.</summary>
    public static async Task SeedAsync(CatalogDbContext catalog, ClinicProvisioning provisioning, ClinicDatabases databases,
        IPasswordHasher<Account> hasher, DateTime nowUtc)
    {
        var existing = await catalog.Accounts.ToDictionaryAsync(a => a.UserName);
        foreach (var d in Accounts.Where(d => !existing.ContainsKey(d.UserName)))
        {
            var a = new Account { UserName = d.UserName, DisplayName = d.DisplayName, Credentials = d.Credentials, IsPlatformAdmin = d.PlatformAdmin, CreatedUtc = nowUtc };
            a.PasswordHash = hasher.HashPassword(a, RandomNumberGenerator.GetHexString(32)); // demo accounts use one-click sign-in
            catalog.Accounts.Add(existing[d.UserName] = a);
        }
        await catalog.SaveChangesAsync();

        foreach (var profile in Clinics)
        {
            var clinic = await catalog.Clinics.SingleOrDefaultAsync(c => c.Slug == profile.Slug)
                ?? await provisioning.CreateAsync(profile.Slug, profile.Name, creatorAccountId: null);
            var roles = await catalog.Roles.Where(r => r.ClinicId == clinic.Id).ToDictionaryAsync(r => r.Name);
            var held = await catalog.AccountRoles.Where(ar => ar.Role.ClinicId == clinic.Id).Select(ar => new { ar.AccountId, ar.RoleId }).ToListAsync();
            foreach (var (userName, role) in profile.Members)
                if (!held.Any(h => h.AccountId == existing[userName].Id && h.RoleId == roles[role].Id))
                    catalog.AccountRoles.Add(new AccountRole { AccountId = existing[userName].Id, RoleId = roles[role].Id });
            await catalog.SaveChangesAsync();

            await using var db = databases.Open(clinic);
            foreach (var (userName, _) in profile.Members.DistinctBy(m => m.UserName))
                await StaffSync.EnsureAsync(db, existing[userName], member: true, nowUtc);
            await SeedClinicData(db, profile, nowUtc);
        }
    }

    private static async Task SeedClinicData(ClinicHubDbContext db, DemoClinic profile, DateTime nowUtc)
    {
        if (!await db.Providers.AnyAsync())
            await SeedClinic(db, profile, nowUtc);

        // Keep a rolling week of appointments: missing days are generated on start-up.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Toronto));
        for (var day = today; day <= today.AddDays(7); day = day.AddDays(1))
            await SeedDay(db, profile, day, nowUtc);
        await SeedEncounters(db, profile, today);
        await SeedBillingDemo(db, today, nowUtc);
    }

    /// <summary>
    /// Gives reciprocal-billing patients their home province's number, and signs about two thirds of today's
    /// finished visits with a short note, so the billing clerk starts with claims to send.
    /// </summary>
    private static async Task SeedBillingDemo(ClinicHubDbContext db, DateOnly today, DateTime nowUtc)
    {
        var rng = new Random(20261011);
        foreach (var p in await db.Patients.Where(p => p.HealthCardNumber == null && p.CoverageProvince != null && p.CoverageProvince != "ON" && p.OutOfProvinceNumber == null).ToListAsync())
            p.OutOfProvinceNumber = string.Concat(Enumerable.Range(0, 9).Select(_ => rng.Next(0, 10)));
        await db.SaveChangesAsync();

        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), Toronto);
        var finished = await db.Encounters.Include(e => e.Patient).Include(e => e.Provider).Include(e => e.Appointment!).ThenInclude(a => a.AppointmentType)
            .Include(e => e.Diagnoses).Include(e => e.Services).ThenInclude(s => s.UninsuredService)
            .Where(e => e.CheckInUtc >= dayStartUtc && e.CheckInUtc < dayStartUtc.AddDays(1) && e.Status == EncounterStatus.Open
                && e.Note == null && e.Appointment!.Status == AppointmentStatus.Completed)
            .ToListAsync();
        var fees = await db.FeeCodes.ToDictionaryAsync(f => f.ServiceCode, f => f.Fee);
        var uninsured = await db.UninsuredServices.ToDictionaryAsync(u => u.Code);
        foreach (var e in finished)
        {
            // About a third stay unsigned: the provider's end-of-day backlog.
            // Walk-ins by their reason; booked follow-ups are blood pressure checks. Other visit types are left for the provider.
            var key = e.ReasonForVisit ?? (e.Appointment!.AppointmentType.Name == "Follow-up" ? "" : null);
            if (new Random(e.Id).NextDouble() < 0.35 || key is null || !VisitTemplates.TryGetValue(key, out var t))
                continue;
            var serviceDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(e.CheckInUtc, Toronto));
            e.Note = new ClinicalNote { Subjective = t.Subjective, Objective = t.Objective, Assessment = t.Assessment, Plan = t.Plan,
                AuthorUserId = e.Provider.UserId!.Value, UpdatedUtc = e.EndedUtc ?? nowUtc };
            e.Diagnoses.Add(new EncounterDiagnosis { DiagnosticCodeCode = t.Dx, IsPrimary = true });
            if (e.PaymentProgram == PaymentPrograms.Uninsured)
                e.Services.Add(new EncounterService { UninsuredService = uninsured["VISIT"], UninsuredServiceId = uninsured["VISIT"].Id, Fee = uninsured["VISIT"].Price, ServiceDate = serviceDate });
            else
                e.Services.Add(new EncounterService { FeeCodeServiceCode = t.Fee, Fee = fees[t.Fee], ServiceDate = serviceDate });
            if (t.AlsoNote)
                e.Services.Add(new EncounterService { UninsuredService = uninsured["NOTE"], UninsuredServiceId = uninsured["NOTE"].Id, Fee = uninsured["NOTE"].Price, ServiceDate = serviceDate });
            EncounterSigning.Sign(db, e, e.Provider.UserId!.Value, (e.EndedUtc ?? nowUtc).AddMinutes(5));
        }
        await db.SaveChangesAsync();
    }

    private record VisitTemplate(string Dx, string Fee, string Subjective, string Objective, string Assessment, string Plan, bool AlsoNote = false);

    private static readonly Dictionary<string, VisitTemplate> VisitTemplates = new()
    {
        ["Sore throat"] = new("034", "A007A", "Sore throat 3 days, fever, no cough.", "T 38.2. Tonsillar exudate, tender anterior nodes.",
            "Streptococcal pharyngitis (Centor 4).", "Rapid strep positive. Penicillin V 600 mg BID x 10 days. Return if unable to swallow."),
        ["Cough and fever"] = new("466", "A007A", "Productive cough 6 days, low-grade fever.", "Scattered wheeze, no focal crackles. SpO2 97%.",
            "Acute bronchitis.", "Supportive care, fluids. Return if short of breath or fever past 3 more days."),
        ["Ear pain"] = new("382", "A007A", "Left ear pain 2 days after a cold.", "Left TM bulging and red.", "Acute otitis media, left.",
            "Amoxicillin 500 mg TID x 7 days. Analgesia."),
        ["Rash"] = new("692", "A001A", "Itchy rash on forearms 1 week.", "Erythematous scaly patches, no vesicles.", "Contact dermatitis.",
            "Avoid trigger. Hydrocortisone 1% cream BID x 2 weeks."),
        ["Back pain"] = new("724", "A007A", "Low back pain after lifting, 4 days. No numbness, no bladder symptoms.",
            "Paraspinal tenderness, SLR negative, neuro intact.", "Mechanical low back pain, no red flags.", "Stay active, NSAID x 1 week, physiotherapy if not improving."),
        ["Prescription renewal"] = new("401", "A001A", "Here for antihypertensive renewal. No side effects.", "BP 132/84.",
            "Hypertension, controlled.", "Renew ramipril 5 mg daily x 3 months. Bloodwork before next visit."),
        ["Sick note"] = new("460", "A001A", "Cold symptoms 2 days, missed work.", "Mild pharyngeal erythema, chest clear.", "Viral URI.",
            "Supportive care. Note for 2 days off work given.", AlsoNote: true),
        ["Urinary symptoms"] = new("599", "A007A", "Dysuria and frequency 2 days. No fever or flank pain.", "Afebrile, no CVA tenderness. Dipstick nitrite positive.",
            "Uncomplicated cystitis.", "Nitrofurantoin 100 mg BID x 5 days. Urine culture sent."),
        ["Headache"] = new("784", "A007A", "Recurrent frontal headaches, worse with screen time.", "Neuro exam normal, BP normal.",
            "Tension-type headache.", "Sleep and screen hygiene, acetaminophen as needed, headache diary. Follow up 4 weeks."),
        [""] = new("401", "A007A", "Follow-up of blood pressure. Home readings 130s/80s.", "BP 134/82, HR 72.",
            "Hypertension, at target.", "Continue current medication. Repeat lipids and A1C. Follow up 3 months."),
    };

    /// <summary>
    /// Today's patients who have already arrived get the encounter check-in would have opened, and those
    /// roomed get the nurse's vitals, so the clinician's worklist starts mid-day like the front desk does.
    /// </summary>
    private static async Task SeedEncounters(ClinicHubDbContext db, DemoClinic profile, DateOnly today)
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
        var deskId = await db.Users.Where(u => u.UserName == profile.DeskUser).Select(u => u.Id).FirstAsync();
        var nurseId = await db.Users.Where(u => u.UserName == profile.NurseUser).Select(u => u.Id).FirstAsync();
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

    private static async Task SeedClinic(ClinicHubDbContext db, DemoClinic profile, DateTime nowUtc)
    {
        // The location opened with the clinic gets the demo address and rooms.
        var location = await db.Locations.Include(l => l.Rooms).OrderBy(l => l.Id).FirstAsync();
        location.Name = profile.Name;
        location.AddressLine1 = profile.Address;
        location.City = profile.City;
        location.PostalCode = profile.PostalCode;
        location.Phone = profile.Phone;
        location.OhipGroupNumber = profile.Group;
        location.Rooms.AddRange([.. Enumerable.Range(1, profile.ExamRooms).Select(i => new Room { Name = $"Exam {i}" }),
            new Room { Name = "Procedure", Kind = RoomKinds.Procedure },
            new Room { Name = "Lab collection", Kind = RoomKinds.LabCollection },
            new Room { Name = "ECG", Kind = RoomKinds.Diagnostics }]);

        var staff = await db.Users.ToDictionaryAsync(u => u.UserName, u => u.Id);
        var providers = profile.Providers.Select(p => (Profile: p, Provider: new Provider
        {
            Title = p.Title, FirstName = p.First, LastName = p.Last, Kind = p.Kind, OhipBillingNumber = p.Billing, SpecialtyCode = p.SpecialtyCode,
            SpecialtyName = p.Specialty, OverheadPercent = p.Overhead, UserId = staff[p.UserName],
            RegistrationNumber = p.Billing is { } b ? $"9{b[..4]}" : $"NP{p.Last.Length * 1111 % 10000:0000}",
        })).ToList();
        db.Providers.AddRange(providers.Select(p => p.Provider));
        await db.SaveChangesAsync();

        var from = new DateOnly(2026, 1, 1);
        foreach (var (p, provider) in providers)
            foreach (var day in p.Days)
            {
                ScheduleTemplate Session(TimeOnly start, TimeOnly end) => new()
                {
                    ProviderId = provider.Id, LocationId = location.Id, DayOfWeek = day, StartTime = start, EndTime = end, SlotMinutes = 15,
                    AcceptsWalkIns = p.WalkIns || Weekend.Contains(day), EffectiveFrom = from,
                };
                // Weekend walk-in clinic 09:00–15:00; weekdays two sessions around lunch.
                if (Weekend.Contains(day))
                    db.ScheduleTemplates.Add(Session(new(9, 0), new(15, 0)));
                else
                {
                    db.ScheduleTemplates.Add(Session(new(8, 0), new(12, 0)));
                    db.ScheduleTemplates.Add(Session(new(13, 0), new(18, 0)));
                }
            }

        db.Patients.AddRange(SyntheticPatients(profile.Patients, nowUtc, profile.MrnPrefix, profile.Seed));
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

    public static IEnumerable<Patient> SyntheticPatients(int count, DateTime nowUtc, string mrnPrefix = "HV", int seed = 20261010)
    {
        var rng = new Random(seed);
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
                Mrn = $"{mrnPrefix}{i:000000}", FirstName = first, LastName = last, DateOfBirth = dob,
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

    private static async Task SeedDay(ClinicHubDbContext db, DemoClinic profile, DateOnly day, DateTime nowUtc)
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
        var creator = await db.Users.Where(u => u.UserName == profile.DeskUser).Select(u => u.Id).FirstAsync();
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
