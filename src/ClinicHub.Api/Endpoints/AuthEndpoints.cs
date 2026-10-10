using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// Sign-in (password, then an authenticator code when two-factor is on), choosing the clinic, and the
/// signed-in person's own account: password, two-factor, sessions. One account serves every clinic.
/// </summary>
public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string TwoFactorPurpose = "ClinicHub.TwoFactorSecret";

    public record LoginRequest(string UserName, string Password, string? Code);
    public record DemoLoginRequest(string UserName);
    public record ClinicBrief(int Id, string Slug, string Name);
    public record MeDto(
        int Id, string UserName, string DisplayName, string? Credentials, ClinicBrief? Clinic, ClinicBrief[] Clinics,
        string[] Roles, string[] Permissions, int? ProviderId, bool TwoFactorEnabled, bool TwoFactorRequired, string? PendingAction,
        bool IsPlatformAdmin);
    public record SelectClinicRequest(int ClinicId);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public record TwoFactorSetup(string Secret, string OtpAuthUri);
    public record CodeBody(string Code);
    public record PasswordBody(string Password);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/auth").WithTags("Auth");

        api.MapPost("/login", async (LoginRequest req, CatalogDbContext catalog, ClinicDatabases clinics, IPasswordHasher<Account> hasher,
            IDataProtectionProvider protection, SecurityLog log, TimeProvider clock, HttpContext http) =>
        {
            var now = clock.GetUtcNow();
            var userName = (req.UserName ?? "").Trim();
            var account = await catalog.Accounts.WithRoles().SingleOrDefaultAsync(a => a.UserName == userName && a.IsActive);

            if (account?.LockoutEndUtc is { } until && until > now.UtcDateTime)
            {
                await log.WriteAsync(SecurityEventTypes.LoginFailed, null, account.Id, "Account locked");
                return Results.Problem($"Too many failed attempts. Try again after {Math.Ceiling((until - now.UtcDateTime).TotalMinutes)} minutes, or ask an administrator to unlock the account.",
                    statusCode: StatusCodes.Status423Locked);
            }

            // Hash even when the account does not exist so timing does not reveal valid user names.
            var result = hasher.VerifyHashedPassword(account ?? new Account(), account?.PasswordHash ?? DummyHash, req.Password ?? "");
            if (account is null || result == PasswordVerificationResult.Failed)
                return await Failed(account, "Wrong password", "Wrong user name or password.");
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                account.PasswordHash = hasher.HashPassword(account, req.Password!);

            if (account.TwoFactorEnabled)
            {
                if (string.IsNullOrWhiteSpace(req.Code))
                    return Results.Problem("Enter the 6-digit code from your authenticator app.", statusCode: StatusCodes.Status401Unauthorized,
                        extensions: new Dictionary<string, object?> { ["twoFactorRequired"] = true });
                var secret = protection.CreateProtector(TwoFactorPurpose).Unprotect(account.TwoFactorSecret!);
                if (Totp.Verify(secret, req.Code, now, account.TwoFactorLastStep) is not { } step)
                    return await Failed(account, "Wrong two-factor code", "That code is wrong or already used.", twoFactor: true);
                account.TwoFactorLastStep = step;
            }
            return await SignIn(catalog, clinics, log, account, now.UtcDateTime, http, "Password");

            async Task<IResult> Failed(Account? a, string detail, string message, bool twoFactor = false)
            {
                if (a is not null && ++a.FailedLoginCount >= MaxFailedLogins)
                {
                    a.FailedLoginCount = 0;
                    a.LockoutEndUtc = now.UtcDateTime + LockoutDuration;
                    log.Add(SecurityEventTypes.LockedOut, a.Id);
                }
                log.Add(SecurityEventTypes.LoginFailed, a?.Id, detail: a is null ? $"Unknown user name \"{Short(userName)}\"" : detail);
                await catalog.SaveChangesAsync();
                return Results.Problem(message, statusCode: StatusCodes.Status401Unauthorized,
                    extensions: twoFactor ? new Dictionary<string, object?> { ["twoFactorRequired"] = true } : null);
            }
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        // Public demo only: one click signs in as a synthetic staff member.
        api.MapPost("/demo-login", async (DemoLoginRequest req, CatalogDbContext catalog, ClinicDatabases clinics, IOptions<DemoOptions> demo,
            SecurityLog log, TimeProvider clock, HttpContext http) =>
        {
            if (!demo.Value.Enabled || !DemoSeeder.DemoUsers.Contains(req.UserName))
                return Results.NotFound();
            var account = await catalog.Accounts.WithRoles().SingleOrDefaultAsync(a => a.UserName == req.UserName && a.IsActive);
            return account is null ? Results.NotFound() : await SignIn(catalog, clinics, log, account, clock.GetUtcNow().UtcDateTime, http, "Demo");
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        api.MapPost("/logout", async (ClaimsPrincipal principal, SecurityLog log, HttpContext http) =>
        {
            if (principal.UserId() is { } id)
                await log.WriteAsync(SecurityEventTypes.Logout, id, clinicId: principal.ClinicId());
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        api.MapGet("/me", async (ClaimsPrincipal principal, CatalogDbContext catalog) =>
        {
            var account = await LoadAccount(catalog, principal.UserId());
            return account is null ? Results.NoContent() : Results.Ok(ToMe(account, principal));
        }).AllowAnonymous();

        // Switching clinic is a new sign-in to that clinic: its database, its roles.
        api.MapPost("/clinic", async (SelectClinicRequest req, ClaimsPrincipal principal, CatalogDbContext catalog, ClinicDatabases clinics,
            SecurityLog log, TimeProvider clock, HttpContext http) =>
        {
            var account = (await LoadAccount(catalog, principal.RequiredUserId()))!;
            if (!Access.Clinics(account).Any(c => c.Id == req.ClinicId))
                return Results.Problem("You are not a member of that clinic.", statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(await EnterClinic(clinics, log, account, req.ClinicId, clock.GetUtcNow().UtcDateTime, http));
        });

        var account = api.MapGroup("").AddEndpointFilter(UserEndpoints.DemoReadOnly);

        account.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal principal, CatalogDbContext catalog,
            IPasswordHasher<Account> hasher, SecurityLog log, HttpContext http) =>
        {
            var a = (await LoadAccount(catalog, principal.RequiredUserId(), tracked: true))!;
            if (hasher.VerifyHashedPassword(a, a.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is wrong."] });
            if (UserEndpoints.PasswordProblem(req.NewPassword, a.UserName) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [problem] });
            if (hasher.VerifyHashedPassword(a, a.PasswordHash, req.NewPassword) != PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = ["Choose a password different from the current one."] });

            a.PasswordHash = hasher.HashPassword(a, req.NewPassword);
            a.MustChangePassword = false;
            a.SecurityStamp = NewStamp();
            log.Add(SecurityEventTypes.PasswordChanged, a.Id);
            await catalog.SaveChangesAsync();
            // Other sessions end; this one continues with the new stamp.
            await AuthSetup.SignInAsync(http, a, principal.ClinicId());
            return Results.Ok(ToMe(a, AuthSetup.CreatePrincipal(a, principal.ClinicId())));
        }).RequireRateLimiting(LoginRateLimit);

        account.MapPost("/two-factor/setup", async (ClaimsPrincipal principal, CatalogDbContext catalog, IDataProtectionProvider protection) =>
        {
            var a = (await LoadAccount(catalog, principal.RequiredUserId(), tracked: true))!;
            if (a.TwoFactorEnabled)
                return Results.Problem("Two-factor sign-in is already on. Turn it off first to use a new device.", statusCode: StatusCodes.Status409Conflict);
            var secret = Totp.NewSecret();
            a.TwoFactorSecret = protection.CreateProtector(TwoFactorPurpose).Protect(secret);
            await catalog.SaveChangesAsync();
            return Results.Ok(new TwoFactorSetup(secret, Totp.OtpAuthUri("ClinicHub", a.UserName, secret)));
        });

        account.MapPost("/two-factor/enable", async (CodeBody body, ClaimsPrincipal principal, CatalogDbContext catalog,
            IDataProtectionProvider protection, SecurityLog log, TimeProvider clock, HttpContext http) =>
        {
            var a = (await LoadAccount(catalog, principal.RequiredUserId(), tracked: true))!;
            if (a.TwoFactorEnabled || a.TwoFactorSecret is null)
                return Results.Problem("Start the setup first.", statusCode: StatusCodes.Status409Conflict);
            var secret = protection.CreateProtector(TwoFactorPurpose).Unprotect(a.TwoFactorSecret);
            if (Totp.Verify(secret, body.Code, clock.GetUtcNow(), a.TwoFactorLastStep) is not { } step)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["That code doesn't match. Check the phone's clock and try the newest code."] });
            a.TwoFactorEnabled = true;
            a.TwoFactorLastStep = step;
            log.Add(SecurityEventTypes.TwoFactorEnabled, a.Id);
            await catalog.SaveChangesAsync();
            await AuthSetup.SignInAsync(http, a, principal.ClinicId());
            return Results.Ok(ToMe(a, AuthSetup.CreatePrincipal(a, principal.ClinicId())));
        });

        account.MapPost("/two-factor/disable", async (PasswordBody body, ClaimsPrincipal principal, CatalogDbContext catalog,
            IPasswordHasher<Account> hasher, SecurityLog log) =>
        {
            var a = (await LoadAccount(catalog, principal.RequiredUserId(), tracked: true))!;
            if (Access.For(a, null).TwoFactorRequired)
                return Results.Problem("One of your roles requires two-factor sign-in.", statusCode: StatusCodes.Status409Conflict);
            if (hasher.VerifyHashedPassword(a, a.PasswordHash, body.Password ?? "") == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["Password is wrong."] });
            a.TwoFactorEnabled = false;
            a.TwoFactorSecret = null;
            log.Add(SecurityEventTypes.TwoFactorDisabled, a.Id);
            await catalog.SaveChangesAsync();
            return Results.Ok(ToMe(a, principal));
        }).RequireRateLimiting(LoginRateLimit);

        // Ends every other session of this person (a lost laptop, a shared computer).
        account.MapPost("/sessions/revoke", async (ClaimsPrincipal principal, CatalogDbContext catalog, SecurityLog log, HttpContext http) =>
        {
            var a = (await LoadAccount(catalog, principal.RequiredUserId(), tracked: true))!;
            a.SecurityStamp = NewStamp();
            log.Add(SecurityEventTypes.SessionsRevoked, a.Id);
            await catalog.SaveChangesAsync();
            await AuthSetup.SignInAsync(http, a, principal.ClinicId());
            return Results.NoContent();
        });
    }

    /// <summary>Signs in, straight into the clinic when the person belongs to only one.</summary>
    private static async Task<IResult> SignIn(CatalogDbContext catalog, ClinicDatabases clinics, SecurityLog log, Account account,
        DateTime now, HttpContext http, string how)
    {
        account.FailedLoginCount = 0;
        account.LockoutEndUtc = null;
        account.LastLoginUtc = now;
        log.Add(SecurityEventTypes.LoginSucceeded, account.Id, detail: how);
        await catalog.SaveChangesAsync();
        var memberOf = Access.Clinics(account).ToList();
        if (memberOf.Count == 1)
            return Results.Ok(await EnterClinic(clinics, log, account, memberOf[0].Id, now, http));
        await AuthSetup.SignInAsync(http, account, null);
        return Results.Ok(ToMe(account, AuthSetup.CreatePrincipal(account, null)));
    }

    private static async Task<MeDto> EnterClinic(ClinicDatabases clinics, SecurityLog log, Account account, int clinicId, DateTime now, HttpContext http)
    {
        int? providerId;
        await using (var clinicDb = await clinics.OpenAsync(clinicId))
        {
            await StaffSync.EnsureAsync(clinicDb, account, member: true, now);
            providerId = await clinicDb.Providers.Where(p => p.UserId == account.Id).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        }
        await log.WriteAsync(SecurityEventTypes.ClinicSelected, account.Id, clinicId: clinicId);
        var principal = AuthSetup.CreatePrincipal(account, clinicId, providerId);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return ToMe(account, principal);
    }

    public static async Task<Account?> LoadAccount(CatalogDbContext catalog, int? id, bool tracked = false)
    {
        if (id is null)
            return null;
        var q = catalog.Accounts.WithRoles().Where(a => a.Id == id && a.IsActive);
        return await (tracked ? q : q.AsNoTracking()).SingleOrDefaultAsync();
    }

    /// <summary>The person as the request principal sees them: its clinic, roles and permissions.</summary>
    public static MeDto ToMe(Account a, ClaimsPrincipal principal)
    {
        var clinicId = principal.ClinicId();
        var access = Access.For(a, clinicId);
        var memberOf = Access.Clinics(a).Select(c => new ClinicBrief(c.Id, c.Slug, c.Name)).ToArray();
        int? providerId = int.TryParse(principal.FindFirstValue(AppClaims.ProviderId), out var p) ? p : null;
        return new MeDto(a.Id, a.UserName, a.DisplayName, a.Credentials, memberOf.FirstOrDefault(c => c.Id == clinicId), memberOf,
            access.Roles, access.Permissions, providerId, a.TwoFactorEnabled, access.TwoFactorRequired, access.Pending, a.IsPlatformAdmin);
    }

    public static string NewStamp() => Guid.NewGuid().ToString("N");

    public static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static string Short(string s) => s.Length > 40 ? s[..40] : s;

    private static readonly string DummyHash = new PasswordHasher<Account>().HashPassword(new Account(), Guid.NewGuid().ToString());
}
