using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// sec schema: accounts, roles and the PHIPA trail. Same model as ClinicReport.

public class User
{
    public int Id { get; set; }
    [MaxLength(64)] public string UserName { get; set; } = "";
    [MaxLength(128)] public string DisplayName { get; set; } = "";
    [MaxLength(200)] public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    [MaxLength(64)] public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    [MaxLength(1000)] public string? TwoFactorSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public long TwoFactorLastStep { get; set; }

    /// <summary>Locations this user works at, one per line; empty means all.</summary>
    [MaxLength(500)] public string? Locations { get; set; }

    public List<UserRole> Roles { get; set; } = [];
}

public class Role
{
    public int Id { get; set; }
    [MaxLength(64)] public string Name { get; set; } = "";
    [MaxLength(300)] public string? Description { get; set; }

    /// <summary>Space-separated permission keys.</summary>
    [MaxLength(1000)] public string Permissions { get; set; } = "";
    public bool RequireTwoFactor { get; set; }
    public bool IsSystem { get; set; }
    public List<UserRole> Users { get; set; } = [];
}

public class UserRole
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

/// <summary>Sign-ins, account and role changes.</summary>
public class SecurityEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "";
    public int? ActorUserId { get; set; }
    public int? TargetUserId { get; set; }
    [MaxLength(500)] public string? Detail { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
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
