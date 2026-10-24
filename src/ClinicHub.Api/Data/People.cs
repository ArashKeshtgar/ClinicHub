using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// people schema: the master patient index. The only place names, health card and contact details live.

public class Patient
{
    public int Id { get; set; }
    [MaxLength(12)] public string Mrn { get; set; } = "";
    [MaxLength(60)] public string FirstName { get; set; } = "";
    [MaxLength(60)] public string? MiddleName { get; set; }
    [MaxLength(60)] public string LastName { get; set; } = "";
    [MaxLength(60)] public string? PreferredName { get; set; }
    public DateOnly DateOfBirth { get; set; }

    /// <summary>M, F, X or U.</summary>
    [MaxLength(1)] public string Sex { get; set; } = "U";

    /// <summary>10 digits with a Luhn check digit. Null for patients without a card.</summary>
    [MaxLength(10)] public string? HealthCardNumber { get; set; }
    [MaxLength(2)] public string? HealthCardVersion { get; set; }
    public DateOnly? HealthCardExpiry { get; set; }

    /// <summary>Province whose plan covers the patient: ON for OHIP, another code for reciprocal billing.</summary>
    [MaxLength(2)] public string? CoverageProvince { get; set; }

    /// <summary>Health number issued by the covering province, for reciprocal billing (RMB).</summary>
    [MaxLength(12)] public string? OutOfProvinceNumber { get; set; }

    [MaxLength(20)] public string? Phone { get; set; }
    [MaxLength(120)] public string? Email { get; set; }
    [MaxLength(200)] public string? AddressLine1 { get; set; }
    [MaxLength(80)] public string? City { get; set; }
    [MaxLength(2)] public string? Province { get; set; }
    [MaxLength(7)] public string? PostalCode { get; set; }
    [MaxLength(2)] public string PreferredLanguage { get; set; } = "en";

    /// <summary>Family doctor, where reports are sent (HRM).</summary>
    [MaxLength(120)] public string? FamilyProviderName { get; set; }
    [MaxLength(6)] public string? FamilyProviderOhipNumber { get; set; }

    public bool IsDeceased { get; set; }
    public DateOnly? DeceasedDate { get; set; }

    /// <summary>Set when this record was merged into another one; it then stays only for history.</summary>
    public int? MergedIntoPatientId { get; set; }
    public Patient? MergedInto { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];

    public string DisplayName => $"{LastName}, {FirstName}";
}

public static class DuplicateStatus
{
    public const string Open = "Open";
    public const string Merged = "Merged";
    public const string NotDuplicate = "NotDuplicate";
}

/// <summary>Two records that may be the same person, waiting for a person to decide.</summary>
public class PatientDuplicateCandidate
{
    public int Id { get; set; }
    public int PatientAId { get; set; }
    public Patient PatientA { get; set; } = null!;
    public int PatientBId { get; set; }
    public Patient PatientB { get; set; } = null!;

    /// <summary>0–100; how alike the two records are.</summary>
    public int Score { get; set; }
    [MaxLength(300)] public string Reasons { get; set; } = "";
    [MaxLength(20)] public string Status { get; set; } = DuplicateStatus.Open;
    public DateTime FoundUtc { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedUtc { get; set; }
}

/// <summary>
/// What one merge moved, so that it can be undone: the ids of the rows that went to the surviving record, the
/// survivor's fields before and after the empty ones were filled, and the messages the applications were told with.
/// </summary>
public class PatientMergeLog
{
    public int Id { get; set; }
    public int SurvivorId { get; set; }
    public Patient Survivor { get; set; } = null!;
    public int MergedId { get; set; }
    public Patient Merged { get; set; } = null!;
    public DateTime AtUtc { get; set; }
    public int ByUserId { get; set; }

    /// <summary>Table name to the ids that moved from the merged record to the survivor (JSON).</summary>
    public string MovedJson { get; set; } = "{}";

    /// <summary>The survivor's fields before the merge and after it, only those that were filled in (JSON).</summary>
    public string FieldsBeforeJson { get; set; } = "{}";
    public string FieldsAfterJson { get; set; } = "{}";

    /// <summary>The messages to the applications that came with the merge (JSON list of outbox ids).</summary>
    public string OutboxIdsJson { get; set; } = "[]";
    public DateTime? UndoneUtc { get; set; }
    public int? UndoneByUserId { get; set; }
}

public static class AllergySeverities
{
    public const string Mild = "Mild";
    public const string Moderate = "Moderate";
    public const string Severe = "Severe";
}

/// <summary>
/// A recorded allergy or intolerance, checked when a drug is prescribed. A row with <see cref="NoKnown"/> set says that
/// the patient was asked and has none, which is different from nothing having been recorded.
/// </summary>
public class PatientAllergy
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;

    /// <summary>A class or ingredient from <c>DrugSafety.Allergens</c>; null for free text.</summary>
    [MaxLength(40)] public string? AllergenKey { get; set; }

    /// <summary>What the patient is allergic to, as written ("Penicillins (amoxicillin…)", "Latex").</summary>
    [MaxLength(100)] public string Substance { get; set; } = "";
    [MaxLength(200)] public string? Reaction { get; set; }
    [MaxLength(10)] public string Severity { get; set; } = AllergySeverities.Moderate;
    public bool NoKnown { get; set; }
    public int RecordedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Set when it was entered in error or no longer applies; it stays for history.</summary>
    public DateTime? EndedUtc { get; set; }
    public int? EndedByUserId { get; set; }
    [MaxLength(200)] public string? EndReason { get; set; }
}

public static class ConsentScopes
{
    /// <summary>The whole record is withheld from everyone except the patient's own care team for the visit.</summary>
    public const string EntireRecord = "EntireRecord";

    /// <summary>One encounter is withheld.</summary>
    public const string Encounter = "Encounter";

    /// <summary>The record is withheld from one named provider.</summary>
    public const string Provider = "Provider";
}

/// <summary>A PHIPA consent directive ("lockbox"): the patient withholds some or all of their record.</summary>
public class ConsentDirective
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    [MaxLength(20)] public string Scope { get; set; } = ConsentScopes.EntireRecord;
    public int? EncounterId { get; set; }
    public int? ExcludedProviderId { get; set; }
    [MaxLength(300)] public string? Note { get; set; }
    public int RecordedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public List<ConsentOverride> Overrides { get; set; } = [];
}

/// <summary>An emergency override of a consent directive. Always logged and reported to the privacy officer.</summary>
public class ConsentOverride
{
    public int Id { get; set; }
    public int ConsentDirectiveId { get; set; }
    public ConsentDirective ConsentDirective { get; set; } = null!;
    public int UserId { get; set; }
    [MaxLength(300)] public string Reason { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}
