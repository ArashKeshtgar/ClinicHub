namespace ClinicHub.Api.Auth;

/// <summary>
/// Everything a role can allow, in every application that signs in through ClinicHub. ClinicHub's own keys
/// are plain ("encounters.sign"); another application's keys carry its client id ("clinicreport:reports.sign")
/// and reach that application without the prefix, so its own permission checks stay as they are.
/// Endpoints check these, never role names.
/// </summary>
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

    /// <summary>Application client ids. ClinicHub itself has none: its keys have no prefix.</summary>
    public static class Apps
    {
        public const string ClinicReport = "clinicreport";
        public const string LabFlow = "labflow";
    }

    // ClinicReport: structured diagnostic reports (echo, ECG).
    public const string ReportsView = "clinicreport:reports.view";
    public const string ReportsCreate = "clinicreport:reports.create";
    public const string ReportsEdit = "clinicreport:reports.edit";
    public const string ReportsSign = "clinicreport:reports.sign";
    public const string ReportPatientsManage = "clinicreport:patients.manage";
    public const string TemplatesDesign = "clinicreport:templates.design";
    public const string ReportUsersManage = "clinicreport:users.manage";
    public const string ReportAuditView = "clinicreport:audit.view";

    // LabFlow: the laboratory.
    public const string LabFrontDesk = "labflow:frontdesk";
    public const string LabResultsView = "labflow:results.view";
    public const string LabAdmin = "labflow:admin";

    public record Info(string Key, string Group, string Label, string? App = null);

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
        new(UsersManage, "Administration", "Manage people and roles"),
        new(AuditView, "Administration", "View the access and security logs"),
        new(SettingsManage, "Administration", "Clinic settings, price lists, schedules"),

        new(ReportsView, "ClinicReport", "View reports", Apps.ClinicReport),
        new(ReportsCreate, "ClinicReport", "Start reports", Apps.ClinicReport),
        new(ReportsEdit, "ClinicReport", "Fill reports", Apps.ClinicReport),
        new(ReportsSign, "ClinicReport", "Sign reports", Apps.ClinicReport),
        new(ReportPatientsManage, "ClinicReport", "Manage report patients", Apps.ClinicReport),
        new(TemplatesDesign, "ClinicReport", "Design report templates", Apps.ClinicReport),
        new(ReportUsersManage, "ClinicReport", "ClinicReport settings for users (services)", Apps.ClinicReport),
        new(ReportAuditView, "ClinicReport", "View ClinicReport's report-access log", Apps.ClinicReport),

        new(LabFrontDesk, "LabFlow", "Lab front desk: bookings, check-in, collection", Apps.LabFlow),
        new(LabResultsView, "LabFlow", "View lab results", Apps.LabFlow),
        new(LabAdmin, "LabFlow", "Lab administration", Apps.LabFlow),
    ];

    public static readonly string[] All = Catalog.Select(p => p.Key).ToArray();

    /// <summary>What an application sees: its own keys, without the prefix.</summary>
    public static string[] ForApp(IEnumerable<string> granted, string app) =>
        granted.Where(p => p.StartsWith(app + ":", StringComparison.Ordinal)).Select(p => p[(app.Length + 1)..]).ToArray();

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
    public const string ReportTypist = "Report typist";

    /// <summary>
    /// The roles every clinic starts with. When a new version adds a permission, it is added to the built-in
    /// roles listed with it here, once; a clinic's own changes to its roles are never overwritten.
    /// </summary>
    public static readonly (string Name, string Description, string[] Permissions)[] Defaults =
    [
        (Administrator, "Full access.", Permissions.All),
        (FrontDesk, "Registration, booking and check-in; lab bookings.",
            [Permissions.PatientsView, Permissions.PatientsEdit, Permissions.ScheduleView, Permissions.ScheduleManage, Permissions.BillingView,
             Permissions.LabFrontDesk]),
        (Nurse, "Rooming, vitals and nursing notes; lab collection.",
            [Permissions.PatientsView, Permissions.ScheduleView, Permissions.ScheduleManage, Permissions.EncountersView, Permissions.EncountersDocument,
             Permissions.LabFrontDesk, Permissions.LabResultsView]),
        (Physician, "Assesses, orders, signs and bills; reads and signs diagnostic reports.",
            [Permissions.PatientsView, Permissions.ScheduleView, Permissions.EncountersView, Permissions.EncountersDocument, Permissions.EncountersSign,
             Permissions.OrdersManage, Permissions.ResultsReview, Permissions.BillingView, Permissions.ConsentOverride,
             Permissions.ReportsView, Permissions.ReportsCreate, Permissions.ReportsEdit, Permissions.ReportsSign, Permissions.ReportPatientsManage,
             Permissions.LabResultsView]),
        (Billing, "OHIP claims, remittance and invoices.",
            [Permissions.PatientsView, Permissions.ScheduleView, Permissions.BillingView, Permissions.BillingManage]),
        (PrivacyOfficer, "Consent directives and the PHIPA access logs.",
            [Permissions.PatientsView, Permissions.ConsentManage, Permissions.AuditView, Permissions.ReportAuditView]),
        (ReportTypist, "Starts and fills diagnostic reports during the exam (ClinicReport).",
            [Permissions.ReportsView, Permissions.ReportsCreate, Permissions.ReportsEdit, Permissions.ReportPatientsManage]),
    ];
}
