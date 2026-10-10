using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Catalog;

// The catalog database: who the clinics are, who the people are, and what each person may do in each clinic.
// Patient data never lives here; each clinic has its own database (see docs/architecture.md).

public class Clinic
{
    public int Id { get; set; }

    /// <summary>Short stable name used in URLs and database names, e.g. "harbourview".</summary>
    [MaxLength(40)] public string Slug { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";

    /// <summary>Database on the clinic server (ConnectionStrings:ClinicServer) unless <see cref="ConnectionString"/> is set.</summary>
    [MaxLength(128)] public string DatabaseName { get; set; } = "";

    /// <summary>A clinic hosted on its own server. Protected with Data Protection; null for the shared server.</summary>
    [MaxLength(2000)] public string? ConnectionString { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public List<Role> Roles { get; set; } = [];
}

/// <summary>A person who signs in. One account, used at every clinic they work at.</summary>
public class Account
{
    public int Id { get; set; }
    [MaxLength(64)] public string UserName { get; set; } = "";
    [MaxLength(128)] public string DisplayName { get; set; } = "";

    /// <summary>Printed under a signature, e.g. "MD, CCFP".</summary>
    [MaxLength(64)] public string? Credentials { get; set; }

    [MaxLength(200)] public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>Can create clinics and see every clinic's security log. Not a clinic role.</summary>
    public bool IsPlatformAdmin { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Changes whenever existing sessions must end (password, roles, deactivation, "sign out everywhere").</summary>
    [MaxLength(64)] public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Set by an admin-created or admin-reset password: the user chooses their own first.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>TOTP secret, encrypted with ASP.NET Data Protection. Null until set up.</summary>
    [MaxLength(1000)] public string? TwoFactorSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Last accepted TOTP time step, so a code cannot be replayed.</summary>
    public long TwoFactorLastStep { get; set; }

    public List<AccountRole> Roles { get; set; } = [];
}

/// <summary>A named set of permissions within one clinic, e.g. "Front desk" or "Echo typist".</summary>
public class Role
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public Clinic Clinic { get; set; } = null!;
    [MaxLength(64)] public string Name { get; set; } = "";
    [MaxLength(300)] public string? Description { get; set; }

    /// <summary>Space-separated permission keys, from every application the clinic uses (see Auth/Permissions).</summary>
    [MaxLength(2000)] public string Permissions { get; set; } = "";

    /// <summary>Members must sign in with an authenticator code.</summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>Created with the clinic; cannot be deleted. Administrator keeps every permission.</summary>
    public bool IsSystem { get; set; }

    public List<AccountRole> Accounts { get; set; } = [];
}

/// <summary>An account holding a role. Holding any role in a clinic is what makes someone a member of it.</summary>
public class AccountRole
{
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public static class SecurityEventTypes
{
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string LockedOut = "LockedOut";
    public const string Logout = "Logout";
    public const string ClinicSelected = "ClinicSelected";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string TwoFactorEnabled = "TwoFactorEnabled";
    public const string TwoFactorDisabled = "TwoFactorDisabled";
    public const string TwoFactorReset = "TwoFactorReset";
    public const string SessionsRevoked = "SessionsRevoked";
    public const string UserCreated = "UserCreated";
    public const string UserUpdated = "UserUpdated";
    public const string UserUnlocked = "UserUnlocked";
    public const string MemberAdded = "MemberAdded";
    public const string MemberRemoved = "MemberRemoved";
    public const string RoleCreated = "RoleCreated";
    public const string RoleUpdated = "RoleUpdated";
    public const string RoleDeleted = "RoleDeleted";
    public const string ClinicCreated = "ClinicCreated";
}

/// <summary>Sign-ins, account, membership and role changes. Rows are only ever added.</summary>
public class SecurityEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "";

    /// <summary>The clinic it happened in; null for account-wide events such as a sign-in before choosing a clinic.</summary>
    public int? ClinicId { get; set; }

    /// <summary>Who did it (null for a failed sign-in with an unknown user name).</summary>
    public int? ActorAccountId { get; set; }

    /// <summary>The account it was done to, when that is someone else.</summary>
    public int? TargetAccountId { get; set; }

    [MaxLength(500)] public string? Detail { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
}
