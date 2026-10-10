using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// care schema: the visit itself.

/// <summary>A health card validation at check-in (simulated: the real HCV service needs Ministry onboarding).</summary>
public class EligibilityCheck
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public DateTime CheckedUtc { get; set; }
    [MaxLength(10)] public string HealthCardNumber { get; set; } = "";
    [MaxLength(2)] public string? HealthCardVersion { get; set; }
    [MaxLength(4)] public string ResponseCode { get; set; } = "";
    public bool IsEligible { get; set; }
    [MaxLength(200)] public string Message { get; set; } = "";
    public bool Simulated { get; set; } = true;
    public int CheckedByUserId { get; set; }
}

public static class EncounterKinds
{
    public const string Office = "Office";
    public const string WalkIn = "WalkIn";
    public const string Virtual = "Virtual";
    public const string Procedure = "Procedure";
}

public static class EncounterStatus
{
    public const string Open = "Open";
    public const string Signed = "Signed";
    public const string Voided = "Voided";
}

public static class PaymentPrograms
{
    /// <summary>Ontario Health Insurance Plan.</summary>
    public const string Hcp = "HCP";

    /// <summary>Reciprocal medical billing: insured in another province (not Quebec).</summary>
    public const string Rmb = "RMB";

    /// <summary>Workplace injury (WSIB).</summary>
    public const string Wcb = "WCB";

    public const string Uninsured = "Uninsured";
}

public class Encounter
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int? EligibilityCheckId { get; set; }
    public EligibilityCheck? EligibilityCheck { get; set; }

    [MaxLength(20)] public string Kind { get; set; } = EncounterKinds.Office;
    [MaxLength(20)] public string Status { get; set; } = EncounterStatus.Open;
    [MaxLength(10)] public string PaymentProgram { get; set; } = PaymentPrograms.Hcp;
    [MaxLength(300)] public string? ReasonForVisit { get; set; }

    /// <summary>Canadian Triage and Acuity Scale, 1 (resuscitation) to 5 (non-urgent). Urgent-care clinics only.</summary>
    public byte? CtasLevel { get; set; }

    public DateTime CheckInUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public int? SignedByUserId { get; set; }
    public DateTime? SignedUtc { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];

    public List<VitalSigns> Vitals { get; set; } = [];
    public ClinicalNote? Note { get; set; }
    public List<EncounterDiagnosis> Diagnoses { get; set; } = [];
    public List<EncounterService> Services { get; set; } = [];
    public List<Referral> Referrals { get; set; } = [];
}

public class VitalSigns
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    public DateTime TakenUtc { get; set; }
    public short? SystolicMmHg { get; set; }
    public short? DiastolicMmHg { get; set; }
    public short? HeartRate { get; set; }
    public short? RespiratoryRate { get; set; }
    public decimal? TemperatureC { get; set; }
    public byte? SpO2 { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? HeightCm { get; set; }
    public int TakenByUserId { get; set; }
}

/// <summary>SOAP note. Locked when the encounter is signed; corrections become addenda.</summary>
public class ClinicalNote
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    public string? Subjective { get; set; }
    public string? Objective { get; set; }
    public string? Assessment { get; set; }
    public string? Plan { get; set; }
    public string? Addendum { get; set; }
    public int AuthorUserId { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class EncounterDiagnosis
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    [MaxLength(4)] public string DiagnosticCodeCode { get; set; } = "";
    public DiagnosticCode DiagnosticCode { get; set; } = null!;
    public bool IsPrimary { get; set; }
    [MaxLength(200)] public string? Note { get; set; }
}

/// <summary>Something done in the visit that is billed: an OHIP fee code or an uninsured service.</summary>
public class EncounterService
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    [MaxLength(5)] public string? FeeCodeServiceCode { get; set; }
    public FeeCode? FeeCode { get; set; }
    public int? UninsuredServiceId { get; set; }
    public UninsuredService? UninsuredService { get; set; }
    public byte Units { get; set; } = 1;
    public decimal Fee { get; set; }
    public DateOnly ServiceDate { get; set; }
}

public static class ReferralStatus
{
    public const string Draft = "Draft";
    public const string Sent = "Sent";
    public const string Booked = "Booked";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public class Referral
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    public int PatientId { get; set; }
    [MaxLength(60)] public string ToSpecialty { get; set; } = "";
    [MaxLength(120)] public string? ToProviderName { get; set; }
    [MaxLength(10)] public string Urgency { get; set; } = "Routine";
    [MaxLength(1000)] public string Reason { get; set; } = "";

    /// <summary>Fax or eReferral (e.g. Ocean).</summary>
    [MaxLength(20)] public string Channel { get; set; } = "eReferral";
    [MaxLength(20)] public string Status { get; set; } = ReferralStatus.Draft;
    public DateTime? SentUtc { get; set; }
}
