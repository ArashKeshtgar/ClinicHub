using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicHub.Api.Data;

/// <summary>
/// The whole ClinicHub database. Tables are grouped into SQL schemas by concern (see docs/schema.md);
/// every relationship is a foreign key, and CHECK constraints guard the values code also validates,
/// so a bad import or a manual UPDATE cannot store an impossible row.
/// </summary>
public class ClinicHubDbContext(DbContextOptions<ClinicHubDbContext> options) : DbContext(options)
{
    public DbSet<FeeCode> FeeCodes => Set<FeeCode>();
    public DbSet<DiagnosticCode> DiagnosticCodes => Set<DiagnosticCode>();
    public DbSet<UninsuredService> UninsuredServices => Set<UninsuredService>();
    public DbSet<AppointmentType> AppointmentTypes => Set<AppointmentType>();
    public DbSet<Province> Provinces => Set<Province>();

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientDuplicateCandidate> DuplicateCandidates => Set<PatientDuplicateCandidate>();
    public DbSet<ConsentDirective> ConsentDirectives => Set<ConsentDirective>();
    public DbSet<ConsentOverride> ConsentOverrides => Set<ConsentOverride>();

    public DbSet<ScheduleTemplate> ScheduleTemplates => Set<ScheduleTemplate>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentStatusChange> AppointmentStatusChanges => Set<AppointmentStatusChange>();

    public DbSet<EligibilityCheck> EligibilityChecks => Set<EligibilityCheck>();
    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<VitalSigns> VitalSigns => Set<VitalSigns>();
    public DbSet<ClinicalNote> ClinicalNotes => Set<ClinicalNote>();
    public DbSet<EncounterDiagnosis> EncounterDiagnoses => Set<EncounterDiagnosis>();
    public DbSet<EncounterService> EncounterServices => Set<EncounterService>();
    public DbSet<Referral> Referrals => Set<Referral>();

    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();
    public DbSet<DiagnosticResult> DiagnosticResults => Set<DiagnosticResult>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboundMessage> Inbox => Set<InboundMessage>();

    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<ClaimItem> ClaimItems => Set<ClaimItem>();
    public DbSet<ClaimBatch> ClaimBatches => Set<ClaimBatch>();
    public DbSet<RemittanceAdvice> Remittances => Set<RemittanceAdvice>();
    public DbSet<RemittanceItem> RemittanceItems => Set<RemittanceItem>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<PatientAccessLog> PatientAccessLog => Set<PatientAccessLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(10, 2);
        b.Properties<decimal?>().HavePrecision(10, 2);
        // UTC instants: datetime2 without time zone, the "Utc" suffix says what they hold.
        b.Properties<DateTime>().HaveColumnType("datetime2(3)");
        b.Properties<DateTime?>().HaveColumnType("datetime2(3)");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        Reference(b);
        ClinicSchema(b);
        People(b);
        Scheduling(b);
        Care(b);
        Orders(b);
        Billing(b);
        Security(b);

        // SQL Server refuses multiple cascade paths, and clinical history must not vanish with a parent
        // row anyway: nothing cascades except pure child rows configured explicitly below.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            if (fk.DeleteBehavior == DeleteBehavior.Cascade && !CascadingChildren.Contains(fk.DeclaringEntityType.ClrType))
                fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    /// <summary>Rows that are part of their parent and go with it (template edits, draft lines).</summary>
    private static readonly HashSet<Type> CascadingChildren = [typeof(UserRole), typeof(InvoiceLine), typeof(Room)];

    private static void Reference(ModelBuilder b)
    {
        b.Entity<FeeCode>(e =>
        {
            e.ToTable("FeeCode", "ref", t => t.HasCheckConstraint("CK_FeeCode_Fee", "Fee >= 0"));
        });
        b.Entity<DiagnosticCode>().ToTable("DiagnosticCode", "ref");
        b.Entity<UninsuredService>(e =>
        {
            e.ToTable("UninsuredService", "ref", t => t.HasCheckConstraint("CK_UninsuredService_Price", "Price >= 0 AND TaxRatePercent BETWEEN 0 AND 100"));
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.TaxRatePercent).HasPrecision(5, 2);
        });
        b.Entity<AppointmentType>(e =>
        {
            e.ToTable("AppointmentType", "ref", t => t.HasCheckConstraint("CK_AppointmentType_Duration", "DurationMinutes BETWEEN 5 AND 480"));
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<Province>().ToTable("Province", "ref");
    }

    private static void ClinicSchema(ModelBuilder b)
    {
        b.Entity<Location>().ToTable("Location", "clinic");
        b.Entity<Room>(e =>
        {
            e.ToTable("Room", "clinic");
            e.HasIndex(x => new { x.LocationId, x.Name }).IsUnique();
        });
        b.Entity<Provider>(e =>
        {
            e.ToTable("Provider", "clinic", t =>
            {
                t.HasCheckConstraint("CK_Provider_Billing", "OhipBillingNumber IS NULL OR (LEN(OhipBillingNumber) = 6 AND OhipBillingNumber NOT LIKE '%[^0-9]%')");
                t.HasCheckConstraint("CK_Provider_Overhead", "OverheadPercent BETWEEN 0 AND 100");
            });
            e.Property(x => x.OverheadPercent).HasPrecision(5, 2);
            e.HasIndex(x => x.OhipBillingNumber).IsUnique().HasFilter("OhipBillingNumber IS NOT NULL");
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("UserId IS NOT NULL");
            e.Ignore(x => x.DisplayName);
        });
    }

    private static void People(ModelBuilder b)
    {
        b.Entity<Patient>(e =>
        {
            e.ToTable("Patient", "people", t =>
            {
                t.HasCheckConstraint("CK_Patient_Sex", "Sex IN ('M','F','X','U')");
                t.HasCheckConstraint("CK_Patient_HealthCard", "HealthCardNumber IS NULL OR (LEN(HealthCardNumber) = 10 AND HealthCardNumber NOT LIKE '%[^0-9]%')");
                t.HasCheckConstraint("CK_Patient_Version", "HealthCardVersion IS NULL OR HealthCardVersion NOT LIKE '%[^A-Z]%'");
                t.HasCheckConstraint("CK_Patient_NotSelfMerged", "MergedIntoPatientId IS NULL OR MergedIntoPatientId <> Id");
            });
            e.HasIndex(x => x.Mrn).IsUnique();
            // One live record per health card; merged records keep theirs for history.
            e.HasIndex(x => x.HealthCardNumber).IsUnique().HasFilter("HealthCardNumber IS NOT NULL AND MergedIntoPatientId IS NULL");
            e.HasIndex(x => new { x.LastName, x.FirstName, x.DateOfBirth });
            e.HasOne(x => x.MergedInto).WithMany().HasForeignKey(x => x.MergedIntoPatientId);
            e.Ignore(x => x.DisplayName);
        });
        b.Entity<PatientDuplicateCandidate>(e =>
        {
            e.ToTable("PatientDuplicateCandidate", "people", t =>
            {
                t.HasCheckConstraint("CK_Duplicate_Order", "PatientAId < PatientBId");
                t.HasCheckConstraint("CK_Duplicate_Score", "Score BETWEEN 0 AND 100");
            });
            e.HasOne(x => x.PatientA).WithMany().HasForeignKey(x => x.PatientAId);
            e.HasOne(x => x.PatientB).WithMany().HasForeignKey(x => x.PatientBId);
            e.HasIndex(x => new { x.PatientAId, x.PatientBId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewedByUserId);
        });
        b.Entity<ConsentDirective>(e =>
        {
            e.ToTable("ConsentDirective", "people");
            e.HasOne<Encounter>().WithMany().HasForeignKey(x => x.EncounterId);
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ExcludedProviderId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RecordedByUserId);
            e.HasIndex(x => x.PatientId);
        });
        b.Entity<ConsentOverride>(e =>
        {
            e.ToTable("ConsentOverride", "people", t => t.HasCheckConstraint("CK_ConsentOverride_Expiry", "ExpiresUtc > AtUtc"));
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });
    }

    private static void Scheduling(ModelBuilder b)
    {
        b.Entity<ScheduleTemplate>(e => e.ToTable("ScheduleTemplate", "sched", t =>
        {
            t.HasCheckConstraint("CK_ScheduleTemplate_Hours", "EndTime > StartTime");
            t.HasCheckConstraint("CK_ScheduleTemplate_Slot", "SlotMinutes BETWEEN 5 AND 120");
        }));
        b.Entity<ScheduleException>(e => e.ToTable("ScheduleException", "sched"));
        b.Entity<Appointment>(e =>
        {
            e.ToTable("Appointment", "sched", t =>
            {
                t.HasCheckConstraint("CK_Appointment_Time", "EndUtc > StartUtc");
                t.HasCheckConstraint("CK_Appointment_Status", In("Status", AppointmentStatus.All));
            });
            e.HasIndex(x => new { x.ProviderId, x.StartUtc });
            e.HasIndex(x => new { x.LocationId, x.StartUtc });
            e.HasIndex(x => new { x.PatientId, x.StartUtc });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId);
        });
        b.Entity<AppointmentStatusChange>(e =>
        {
            e.ToTable("AppointmentStatusChange", "sched");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });
    }

    private static void Care(ModelBuilder b)
    {
        b.Entity<EligibilityCheck>(e =>
        {
            e.ToTable("EligibilityCheck", "care");
            e.HasIndex(x => new { x.PatientId, x.CheckedUtc });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CheckedByUserId);
        });
        b.Entity<Encounter>(e =>
        {
            e.ToTable("Encounter", "care", t =>
            {
                t.HasCheckConstraint("CK_Encounter_Ctas", "CtasLevel IS NULL OR CtasLevel BETWEEN 1 AND 5");
                t.HasCheckConstraint("CK_Encounter_Program", In("PaymentProgram", [PaymentPrograms.Hcp, PaymentPrograms.Rmb, PaymentPrograms.Wcb, PaymentPrograms.Uninsured]));
                t.HasCheckConstraint("CK_Encounter_Signed", "Status <> 'Signed' OR (SignedUtc IS NOT NULL AND SignedByUserId IS NOT NULL)");
                t.HasCheckConstraint("CK_Encounter_Status", In("Status", [EncounterStatus.Open, EncounterStatus.Signed, EncounterStatus.Voided]));
            });
            e.HasIndex(x => new { x.PatientId, x.CheckInUtc });
            e.HasIndex(x => new { x.ProviderId, x.CheckInUtc });
            e.HasIndex(x => x.AppointmentId).IsUnique().HasFilter("AppointmentId IS NOT NULL");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SignedByUserId);
        });
        b.Entity<VitalSigns>(e =>
        {
            e.ToTable("VitalSigns", "care", t => t.HasCheckConstraint("CK_Vitals_Ranges",
                "(SpO2 IS NULL OR SpO2 <= 100) AND (TemperatureC IS NULL OR TemperatureC BETWEEN 25 AND 45) AND (HeartRate IS NULL OR HeartRate BETWEEN 10 AND 300)"));
            e.Property(x => x.TemperatureC).HasPrecision(4, 1);
            e.Property(x => x.WeightKg).HasPrecision(5, 1);
            e.Property(x => x.HeightCm).HasPrecision(5, 1);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TakenByUserId);
        });
        b.Entity<ClinicalNote>(e =>
        {
            e.ToTable("ClinicalNote", "care");
            e.HasOne(x => x.Encounter).WithOne(x => x.Note).HasForeignKey<ClinicalNote>(x => x.EncounterId);
            foreach (var p in new[] { nameof(ClinicalNote.Subjective), nameof(ClinicalNote.Objective), nameof(ClinicalNote.Assessment), nameof(ClinicalNote.Plan), nameof(ClinicalNote.Addendum) })
                e.Property(p).HasMaxLength(-1);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AuthorUserId);
        });
        b.Entity<EncounterDiagnosis>(e =>
        {
            e.ToTable("EncounterDiagnosis", "care");
            e.HasOne(x => x.DiagnosticCode).WithMany().HasForeignKey(x => x.DiagnosticCodeCode);
            // At most one primary diagnosis per encounter.
            e.HasIndex(x => x.EncounterId).IsUnique().HasFilter("IsPrimary = 1").HasDatabaseName("UX_EncounterDiagnosis_Primary");
        });
        b.Entity<EncounterService>(e =>
        {
            e.ToTable("EncounterService", "care", t =>
            {
                // Exactly one of: an OHIP fee code, or an uninsured service.
                t.HasCheckConstraint("CK_EncounterService_OneKind",
                    "(FeeCodeServiceCode IS NOT NULL AND UninsuredServiceId IS NULL) OR (FeeCodeServiceCode IS NULL AND UninsuredServiceId IS NOT NULL)");
                t.HasCheckConstraint("CK_EncounterService_Units", "Units BETWEEN 1 AND 99 AND Fee >= 0");
            });
            e.HasOne(x => x.FeeCode).WithMany().HasForeignKey(x => x.FeeCodeServiceCode);
        });
        b.Entity<Referral>(e =>
        {
            e.ToTable("Referral", "care");
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId);
        });
    }

    private static void Orders(ModelBuilder b)
    {
        b.Entity<ServiceRequest>(e =>
        {
            e.ToTable("ServiceRequest", "orders", t =>
            {
                t.HasCheckConstraint("CK_ServiceRequest_Target", In("TargetSystem", [TargetSystems.LabFlow, TargetSystems.ClinicReport]));
                t.HasCheckConstraint("CK_ServiceRequest_Priority", "Priority IN ('Routine','Stat')");
            });
            e.HasOne(x => x.RequesterProvider).WithMany().HasForeignKey(x => x.RequesterProviderId);
            e.HasIndex(x => new { x.PatientId, x.CreatedUtc });
            e.HasIndex(x => new { x.TargetSystem, x.ExternalId }).IsUnique().HasFilter("ExternalId IS NOT NULL");
        });
        b.Entity<DiagnosticResult>(e =>
        {
            e.ToTable("DiagnosticResult", "orders");
            e.Property(x => x.Summary).HasMaxLength(-1);
            // The results inbox: everything not yet reviewed.
            e.HasIndex(x => x.ReviewedUtc).HasFilter("ReviewedUtc IS NULL").HasDatabaseName("IX_DiagnosticResult_Unreviewed");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ReviewedByUserId);
        });
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessage", "orders");
            e.Property(x => x.PayloadJson).HasMaxLength(-1);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.NextAttemptUtc).HasFilter("SentUtc IS NULL").HasDatabaseName("IX_Outbox_Pending");
            e.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.ServiceRequestId);
        });
        b.Entity<InboundMessage>(e =>
        {
            e.ToTable("InboundMessage", "orders");
            e.Property(x => x.PayloadJson).HasMaxLength(-1);
            e.Property(x => x.Error).HasMaxLength(1000);
        });
    }

    private static void Billing(ModelBuilder b)
    {
        b.Entity<Claim>(e =>
        {
            e.ToTable("Claim", "billing", t =>
            {
                t.HasCheckConstraint("CK_Claim_Program", In("PaymentProgram", [PaymentPrograms.Hcp, PaymentPrograms.Rmb, PaymentPrograms.Wcb]));
                t.HasCheckConstraint("CK_Claim_Status", In("Status", [ClaimStatus.Draft, ClaimStatus.Ready, ClaimStatus.Submitted, ClaimStatus.Paid, ClaimStatus.PartiallyPaid, ClaimStatus.Rejected]));
                t.HasCheckConstraint("CK_Claim_Amounts", "AmountSubmitted >= 0 AND AmountPaid >= 0");
            });
            e.HasIndex(x => x.AccountingNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.ProviderId });
            e.HasIndex(x => new { x.EncounterId, x.PaymentProgram }).IsUnique();
        });
        b.Entity<ClaimItem>(e => e.ToTable("ClaimItem", "billing", t => t.HasCheckConstraint("CK_ClaimItem_Units", "Units BETWEEN 1 AND 99 AND FeeSubmitted >= 0")));
        b.Entity<ClaimBatch>(e =>
        {
            e.ToTable("ClaimBatch", "billing");
            e.Property(x => x.FileContent).HasMaxLength(-1);
            e.HasIndex(x => new { x.ProviderId, x.BatchNumber }).IsUnique();
        });
        b.Entity<RemittanceAdvice>(e => e.ToTable("RemittanceAdvice", "billing"));
        b.Entity<RemittanceItem>(e =>
        {
            e.ToTable("RemittanceItem", "billing");
            e.HasIndex(x => x.AccountingNumber);
        });
        b.Entity<Invoice>(e =>
        {
            e.ToTable("Invoice", "billing", t =>
            {
                t.HasCheckConstraint("CK_Invoice_BillTo", "PatientId IS NOT NULL OR ThirdPartyName IS NOT NULL");
                t.HasCheckConstraint("CK_Invoice_Totals", "Total = Subtotal + Tax AND Balance BETWEEN 0 AND Total");
            });
            e.HasIndex(x => x.Number).IsUnique();
        });
        b.Entity<InvoiceLine>(e =>
        {
            e.ToTable("InvoiceLine", "billing", t => t.HasCheckConstraint("CK_InvoiceLine_Amounts", "Quantity > 0 AND UnitPrice >= 0"));
            e.Property(x => x.TaxRatePercent).HasPrecision(5, 2);
        });
        b.Entity<Payment>(e =>
        {
            e.ToTable("Payment", "billing", t => t.HasCheckConstraint("CK_Payment_Amount", "Amount > 0"));
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ReceivedByUserId);
        });
    }

    private static void Security(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User", "sec");
            e.HasIndex(x => x.UserName).IsUnique();
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("Role", "sec");
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<UserRole>(e =>
        {
            e.ToTable("UserRole", "sec");
            e.HasKey(x => new { x.UserId, x.RoleId });
        });
        b.Entity<SecurityEvent>(e =>
        {
            e.ToTable("SecurityEvent", "sec");
            e.HasIndex(x => x.AtUtc);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId);
        });
        b.Entity<PatientAccessLog>(e =>
        {
            e.ToTable("PatientAccessLog", "sec");
            e.HasIndex(x => new { x.PatientId, x.AtUtc });
            e.HasIndex(x => new { x.UserId, x.AtUtc });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne<ConsentOverride>().WithMany().HasForeignKey(x => x.ConsentOverrideId);
        });
    }

    private static string In(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(",", values.Select(v => $"'{v}'"))})";
}
