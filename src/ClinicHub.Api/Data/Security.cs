using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// sec schema: the clinic's copy of its staff, and the PHIPA trail. Accounts, passwords, roles and the
// sign-in log live in the catalog database (Catalog/), shared by every clinic.

/// <summary>
/// A member of this clinic, copied from the catalog account with the same Id when they join or sign in, so
/// "who signed this visit" stays a foreign key inside the clinic's own database. Holds no credentials.
/// </summary>
public class User
{
    public int Id { get; set; }
    [MaxLength(64)] public string UserName { get; set; } = "";
    [MaxLength(128)] public string DisplayName { get; set; } = "";
    [MaxLength(64)] public string? Credentials { get; set; }

    /// <summary>False once the person no longer holds a role here; their past records keep pointing at them.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime SyncedUtc { get; set; }
}

public static class AccessActions
{
    public const string View = "View";
    public const string Edit = "Edit";
    public const string Print = "Print";
    public const string Export = "Export";
}

/// <summary>Every open, edit, print or export of a patient's record (PHIPA: "who has looked at my record?").</summary>
public class PatientAccessLog
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public int UserId { get; set; }
    public int PatientId { get; set; }
    [MaxLength(10)] public string Action { get; set; } = AccessActions.View;

    /// <summary>What was opened, e.g. "Encounter 120", "Chart", "Invoice 33".</summary>
    [MaxLength(60)] public string Context { get; set; } = "";
    public int? ConsentOverrideId { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
}
