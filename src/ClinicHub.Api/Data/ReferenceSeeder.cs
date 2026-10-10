using ClinicHub.Api.Auth;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Data;

/// <summary>
/// Lists every installation needs. Adds what is missing and never overwrites, because a clinic edits
/// its own price list and roles. OHIP codes here are a small ILLUSTRATIVE subset with approximate fees:
/// a real installation imports the current Schedule of Benefits and diagnostic code list.
/// </summary>
public static class ReferenceSeeder
{
    public static async Task SeedAsync(ClinicHubDbContext db)
    {
        await AddMissing(db, db.Provinces, p => p.Code,
        [
            P("ON", "Ontario", false), P("BC", "British Columbia", true), P("AB", "Alberta", true), P("SK", "Saskatchewan", true),
            P("MB", "Manitoba", true), P("QC", "Quebec", false), P("NB", "New Brunswick", true), P("NS", "Nova Scotia", true),
            P("PE", "Prince Edward Island", true), P("NL", "Newfoundland and Labrador", true), P("YT", "Yukon", true),
            P("NT", "Northwest Territories", true), P("NU", "Nunavut", true),
        ]);

        var from = new DateOnly(2026, 4, 1);
        await AddMissing(db, db.FeeCodes, f => f.ServiceCode,
        [
            F("A001A", "Minor assessment", 24.15m), F("A003A", "General assessment", 87.15m),
            F("A004A", "General re-assessment", 38.45m), F("A007A", "Intermediate assessment", 37.95m),
            F("A008A", "Mini assessment", 13.70m), F("K005A", "Primary mental health care (per unit)", 72.75m),
            F("K013A", "Counselling, individual care (per unit)", 72.75m), F("G538A", "Other immunization", 4.50m),
        ]);
        FeeCode F(string code, string description, decimal fee) =>
            new() { ServiceCode = code, Description = description, Fee = fee, EffectiveFrom = from, IsIllustrative = true };

        await AddMissing(db, db.DiagnosticCodes, d => d.Code,
        [
            D("034", "Streptococcal sore throat"), D("250", "Diabetes mellitus"), D("300", "Anxiety"), D("311", "Depression"),
            D("382", "Otitis media"), D("401", "Hypertension"), D("460", "Common cold"), D("465", "Upper respiratory infection"),
            D("466", "Bronchitis"), D("486", "Pneumonia"), D("493", "Asthma"), D("599", "Urinary tract infection"),
            D("692", "Dermatitis, eczema"), D("724", "Back pain"), D("784", "Headache"),
        ]);

        await AddMissing(db, db.UninsuredServices, u => u.Code,
        [
            U("VISIT", "Private visit (no provincial coverage)", 85m), U("NOTE", "Sick note", 25m), U("FORM", "Form completion (short)", 40m), U("FORM-L", "Form completion (long)", 90m),
            U("TRAVEL", "Travel medicine consultation", 60m), U("DRIVER", "Driver's medical examination", 90m),
            U("RX-PHONE", "Prescription renewal by phone", 15m), U("RECORDS", "Transfer of medical records", 30m),
        ]);

        await AddMissing(db, db.AppointmentTypes, t => t.Name,
        [
            T("Walk-in visit", 10, "amber", false), T("Follow-up", 15, "blue", true), T("Annual physical", 30, "green", true),
            T("Prenatal visit", 20, "rose", true), T("Procedure", 30, "violet", false), T("Virtual visit", 15, "teal", true),
            T("ECG", 15, "slate", false),
        ]);

        await AddMissing(db, db.Roles, r => r.Name,
            SystemRoles.Defaults.Select(d => new Role { Name = d.Name, Description = d.Description, Permissions = Permissions.Format(d.Permissions), IsSystem = true }).ToArray());
        var admin = await db.Roles.SingleAsync(r => r.Name == SystemRoles.Administrator);
        admin.Permissions = Permissions.Format(Permissions.All); // a new version's permissions reach it
        await db.SaveChangesAsync();
    }

    private static Province P(string code, string name, bool reciprocal) => new() { Code = code, Name = name, ReciprocalBilling = reciprocal };
    private static DiagnosticCode D(string code, string description) => new() { Code = code, Description = description };
    private static UninsuredService U(string code, string name, decimal price) => new() { Code = code, Name = name, Price = price };
    private static AppointmentType T(string name, int minutes, string color, bool online) =>
        new() { Name = name, DurationMinutes = minutes, Color = color, BookableOnline = online };

    private static async Task AddMissing<T, TKey>(ClinicHubDbContext db, DbSet<T> set, Func<T, TKey> key, T[] rows) where T : class
    {
        var existing = (await set.AsNoTracking().ToListAsync()).Select(key).ToHashSet();
        set.AddRange(rows.Where(r => !existing.Contains(key(r))));
        await db.SaveChangesAsync();
    }
}
