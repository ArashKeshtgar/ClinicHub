using System.Security.Claims;
using Claim = System.Security.Claims.Claim;
using ClinicHub.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Auth;

public static class AppClaims
{
    public const string Stamp = "stamp";
    public const string Permission = "perm";
    public const string ProviderId = "provider";
}

/// <summary>What a user may do, from their roles. Administrator always has everything.</summary>
public record Access(string[] Roles, string[] Permissions)
{
    public static Access For(User user)
    {
        var roles = user.Roles.Select(ur => ur.Role).ToList();
        var permissions = roles.Any(r => r.IsSystem && r.Name == SystemRoles.Administrator)
            ? Auth.Permissions.All
            : Auth.Permissions.All.Where(p => roles.Any(r => Auth.Permissions.Parse(r.Permissions).Contains(p))).ToArray();
        return new Access(roles.Select(r => r.Name).OrderBy(n => n).ToArray(), permissions);
    }
}

/// <summary>
/// Cookie sign-in as in ClinicReport: the cookie holds the user id and a security stamp; roles and
/// permissions are re-read on every request, so changes apply at once and a new stamp ends sessions.
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
                o.Cookie.SameSite = SameSiteMode.Strict;
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
    }

    private static async Task LoadCurrentAccess(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.UserId();
        var stamp = ctx.Principal?.FindFirstValue(AppClaims.Stamp);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ClinicHubDbContext>();
        var user = id is null ? null : await db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Id == id);
        if (user is null || !user.IsActive || user.SecurityStamp != stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var providerId = await db.Providers.Where(p => p.UserId == user.Id).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        ctx.ReplacePrincipal(CreatePrincipal(user, Access.For(user), providerId));
    }

    public static ClaimsPrincipal CreatePrincipal(User user, Access? access = null, int? providerId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(AppClaims.Stamp, user.SecurityStamp),
        };
        if (access is not null)
        {
            claims.AddRange(access.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
            claims.AddRange(access.Permissions.Select(p => new Claim(AppClaims.Permission, p)));
        }
        if (providerId is not null)
            claims.Add(new Claim(AppClaims.ProviderId, providerId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task SignInAsync(HttpContext http, User user) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user));

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int RequiredUserId(this ClaimsPrincipal user) =>
        user.UserId() ?? throw new InvalidOperationException("No signed-in user.");

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
