using System.Security.Claims;
using ClinicHub.Api.Catalog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Claim = System.Security.Claims.Claim;

namespace ClinicHub.Api.Auth;

public static class AppClaims
{
    public const string Stamp = "stamp";
    public const string Permission = "perm";
    public const string ProviderId = "provider";

    /// <summary>The clinic chosen after sign-in. Its database serves the request.</summary>
    public const string Clinic = "clinic";

    /// <summary>"change-password" or "setup-two-factor": the user must do this before anything else.</summary>
    public const string Pending = "pending";
}

public static class PendingActions
{
    public const string ChangePassword = "change-password";
    public const string SetupTwoFactor = "setup-two-factor";
}

/// <summary>What an account may do in one clinic, from its roles there. Administrator always has everything.</summary>
public record Access(string[] Roles, string[] Permissions, bool TwoFactorRequired, string? Pending)
{
    public static Access For(Account account, int? clinicId)
    {
        var roles = account.Roles.Select(ar => ar.Role).Where(r => clinicId != null && r.ClinicId == clinicId).ToList();
        var permissions = roles.Any(r => r.IsSystem && r.Name == SystemRoles.Administrator)
            ? Auth.Permissions.All
            : Auth.Permissions.All.Where(p => roles.Any(r => Auth.Permissions.Parse(r.Permissions).Contains(p))).ToArray();
        // A role anywhere that requires two-factor protects the whole account: it is one sign-in for every clinic.
        var twoFactorRequired = account.Roles.Any(ar => ar.Role.RequireTwoFactor);
        var pending = account.MustChangePassword ? PendingActions.ChangePassword
            : twoFactorRequired && !account.TwoFactorEnabled ? PendingActions.SetupTwoFactor
            : null;
        return new Access(roles.Select(r => r.Name).OrderBy(n => n).ToArray(), pending is null ? permissions : [], twoFactorRequired, pending);
    }

    /// <summary>Clinics where the account holds at least one role.</summary>
    public static IEnumerable<Clinic> Clinics(Account account) =>
        account.Roles.Select(ar => ar.Role.Clinic).Where(c => c.IsActive).DistinctBy(c => c.Id).OrderBy(c => c.Name);
}

/// <summary>
/// Cookie sign-in. The cookie holds the account id, its security stamp and the chosen clinic; roles,
/// permissions and the provider link are re-read on every request, so changes apply at once and a new
/// stamp ends every session.
/// </summary>
public static class AuthSetup
{
    public const string CsrfHeader = "X-ClinicHub";

    public static void AddClinicAuth(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "clinichub.auth";
                o.Cookie.HttpOnly = true;
                // Lax, not Strict: an application on another site (ClinicReport) sends people here to sign in, and
                // that navigation must carry the session. Unsafe API calls still need the X-ClinicHub header.
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(10);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = LoadCurrentAccess;
            });

        var auth = services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var p in Permissions.All)
            auth.AddPolicy(p, policy => policy.RequireClaim(AppClaims.Permission, p));
        auth.AddPolicy(PlatformAdmin, policy => policy.RequireClaim(PlatformAdmin, "true"));
    }

    public const string PlatformAdmin = "platform-admin";

    public static IQueryable<Account> WithRoles(this IQueryable<Account> accounts) =>
        accounts.Include(a => a.Roles).ThenInclude(ar => ar.Role).ThenInclude(r => r.Clinic);

    private static async Task LoadCurrentAccess(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.UserId();
        var stamp = ctx.Principal?.FindFirstValue(AppClaims.Stamp);
        var catalog = ctx.HttpContext.RequestServices.GetRequiredService<CatalogDbContext>();
        var account = id is null ? null : await catalog.Accounts.AsNoTracking().WithRoles().SingleOrDefaultAsync(a => a.Id == id);
        if (account is null || !account.IsActive || account.SecurityStamp != stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        // A clinic the person no longer belongs to (or that was closed) is dropped; they choose again.
        var clinicId = ctx.Principal!.ClinicId();
        if (clinicId is not null && !Access.Clinics(account).Any(c => c.Id == clinicId))
            clinicId = null;
        int? providerId = null;
        if (clinicId is not null)
        {
            await using var clinicDb = await ctx.HttpContext.RequestServices.GetRequiredService<ClinicDatabases>().OpenAsync(clinicId.Value);
            providerId = await clinicDb.Providers.Where(p => p.UserId == account.Id).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        }
        ctx.ReplacePrincipal(CreatePrincipal(account, clinicId, providerId));
    }

    public static ClaimsPrincipal CreatePrincipal(Account account, int? clinicId, int? providerId = null)
    {
        var access = Access.For(account, clinicId);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new(ClaimTypes.Name, account.DisplayName),
            new(AppClaims.Stamp, account.SecurityStamp),
        };
        if (clinicId is not null)
            claims.Add(new Claim(AppClaims.Clinic, clinicId.Value.ToString()));
        if (access.Pending is not null)
            claims.Add(new Claim(AppClaims.Pending, access.Pending));
        claims.AddRange(access.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(access.Permissions.Select(p => new Claim(AppClaims.Permission, p)));
        if (account.IsPlatformAdmin && access.Pending is null)
            claims.Add(new Claim(PlatformAdmin, "true"));
        if (providerId is not null)
            claims.Add(new Claim(AppClaims.ProviderId, providerId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task SignInAsync(HttpContext http, Account account, int? clinicId) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(account, clinicId));

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int RequiredUserId(this ClaimsPrincipal user) =>
        user.UserId() ?? throw new InvalidOperationException("No signed-in user.");

    public static int RequiredClinicId(this ClaimsPrincipal user) =>
        user.ClinicId() ?? throw new NoClinicException();

    public static bool Can(this ClaimsPrincipal user, string permission) => user.HasClaim(AppClaims.Permission, permission);

    /// <summary>Rejects unsafe /api requests without the custom header (CSRF defence on top of SameSite=Strict).</summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var unsafeMethod = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method));
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Headers.ContainsKey(CsrfHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { title = $"Missing {CsrfHeader} header." });
            return;
        }
        await next();
    });
}
