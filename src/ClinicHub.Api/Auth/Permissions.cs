namespace ClinicHub.Api.Auth;

/// <summary>Everything a role can allow. Endpoints check these, never role names (same model as ClinicReport).</summary>
public static class Permissions
{
    public const string PatientsView = "patients.view";
    public const string PatientsEdit = "patients.edit";
    public const string PatientsMerge = "patients.merge";
    public const string ScheduleView = "schedule.view";
    public const string ScheduleManage = "schedule.manage";
    public const string EncountersView = "encounters.view";
    public const string EncountersDocument = "encounters.document";
    public const string EncountersSign = "encounters.sign";
    public const string OrdersManage = "orders.manage";
    public const string ResultsReview = "results.review";
    public const string BillingView = "billing.view";
    public const string BillingManage = "billing.manage";
    public const string ConsentManage = "consent.manage";
    public const string ConsentOverride = "consent.override";
    public const string UsersManage = "users.manage";
    public const string AuditView = "audit.view";
    public const string SettingsManage = "settings.manage";

    public record Info(string Key, string Group, string Label);

    public static readonly Info[] Catalog =
    [
        new(PatientsView, "Patients", "Find and view patients"),
        new(PatientsEdit, "Patients", "Register and edit patients"),
        new(PatientsMerge, "Patients", "Merge duplicate records"),
        new(ScheduleView, "Schedule", "View the schedule"),
        new(ScheduleManage, "Schedule", "Book, check in and move patients"),
        new(EncountersView, "Care", "Read charts and visit notes"),
        new(EncountersDocument, "Care", "Record vitals and notes"),
        new(EncountersSign, "Care", "Sign visits"),
        new(OrdersManage, "Care", "Order tests and reports"),
        new(ResultsReview, "Care", "Review and acknowledge results"),
        new(BillingView, "Billing", "View claims and invoices"),
        new(BillingManage, "Billing", "Submit claims, reconcile, take payments"),
        new(ConsentManage, "Privacy", "Record consent directives"),
        new(ConsentOverride, "Privacy", "Override a consent directive in an emergency"),
        new(UsersManage, "Administration", "Manage users and roles"),
        new(AuditView, "Administration", "View the access and security logs"),
        new(SettingsManage, "Administration", "Clinic settings, price lists, schedules"),
    ];

    public static readonly string[] All = Catalog.Select(p => p.Key).ToArray();

    public static string[] Parse(string? stored) =>
        (stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(All.Contains).Distinct().ToArray();

    public static string Format(IEnumerable<string> permissions) => string.Join(' ', All.Where(permissions.Contains));
}

public static class SystemRoles
{
    public const string Administrator = "Administrator";
    public const string FrontDesk = "Front desk";
    public const string Nurse = "Nurse";
    public const string Physician = "Physician";
    public const string Billing = "Billing clerk";
    public const string PrivacyOfficer = "Privacy officer";

    public static readonly (string Name, string Description, string[] Permissions)[] Defaults =
    [
        (Administrator, "Full access.", Permissions.All),
        (FrontDesk, "Registration, booking and check-in.",
            [Permissions.PatientsView, Permissions.PatientsEdit, Permissions.ScheduleView, Permissions.ScheduleManage, Permissions.BillingView]),
        (Nurse, "Rooming, vitals and nursing notes.",
            [Permissions.PatientsView, Permissions.ScheduleView, Permissions.ScheduleManage, Permissions.EncountersView, Permissions.EncountersDocument]),
        (Physician, "Assesses, orders, signs and bills.",
            [Permissions.PatientsView, Permissions.ScheduleView, Permissions.EncountersView, Permissions.EncountersDocument, Permissions.EncountersSign,
             Permissions.OrdersManage, Permissions.ResultsReview, Permissions.BillingView, Permissions.ConsentOverride]),
        (Billing, "OHIP claims, remittance and invoices.",
            [Permissions.PatientsView, Permissions.ScheduleView, Permissions.BillingView, Permissions.BillingManage]),
        (PrivacyOfficer, "Consent directives and the PHIPA access log.",
            [Permissions.PatientsView, Permissions.ConsentManage, Permissions.AuditView]),
    ];
}
