using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Endpoints;

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";
    public const int MaxFailedLogins = 5;

    public record LoginRequest(string UserName, string Password);
    public record DemoLoginRequest(string UserName);
    public record MeDto(int Id, string UserName, string DisplayName, string[] Roles, string[] Permissions, int? ProviderId);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/auth").WithTags("Auth");

        api.MapPost("/login", async (LoginRequest req, ClinicHubDbContext db, IPasswordHasher<User> hasher, TimeProvider clock, HttpContext http) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var user = await Load(db).SingleOrDefaultAsync(u => u.UserName == (req.UserName ?? "").Trim() && u.IsActive);
            if (user?.LockoutEndUtc > now)
                return Results.Problem("Too many failed attempts. Try again later or ask an administrator.", statusCode: StatusCodes.Status423Locked);
            var result = hasher.VerifyHashedPassword(user ?? new User(), user?.PasswordHash ?? DummyHash, req.Password ?? "");
            if (user is null || result == PasswordVerificationResult.Failed)
            {
                if (user is not null && ++user.FailedLoginCount >= MaxFailedLogins)
                {
                    user.FailedLoginCount = 0;
                    user.LockoutEndUtc = now.AddMinutes(15);
                }
                db.SecurityEvents.Add(new SecurityEvent { AtUtc = now, Type = "LoginFailed", TargetUserId = user?.Id, IpAddress = Ip(http) });
                await db.SaveChangesAsync();
                return Results.Problem("Wrong user name or password.", statusCode: StatusCodes.Status401Unauthorized);
            }
            return await SignIn(db, user, now, http, "Password");
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        // Public demo only: one click signs in as a synthetic staff member.
        api.MapPost("/demo-login", async (DemoLoginRequest req, ClinicHubDbContext db, IOptions<DemoOptions> demo, TimeProvider clock, HttpContext http) =>
        {
            if (!demo.Value.Enabled || !DemoSeeder.DemoUsers.Contains(req.UserName))
                return Results.NotFound();
            var user = await Load(db).SingleOrDefaultAsync(u => u.UserName == req.UserName && u.IsActive);
            return user is null ? Results.NotFound() : await SignIn(db, user, clock.GetUtcNow().UtcDateTime, http, "Demo");
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        api.MapGet("/me", async (ClaimsPrincipal principal, ClinicHubDbContext db) =>
        {
            var id = principal.UserId();
            var user = id is null ? null : await Load(db).AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.IsActive);
            return user is null ? Results.NoContent() : Results.Ok(await ToMe(db, user));
        }).AllowAnonymous();
    }

    private static IQueryable<User> Load(ClinicHubDbContext db) => db.Users.Include(u => u.Roles).ThenInclude(ur => ur.Role);

    private static async Task<IResult> SignIn(ClinicHubDbContext db, User user, DateTime now, HttpContext http, string how)
    {
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = now;
        db.SecurityEvents.Add(new SecurityEvent { AtUtc = now, Type = "LoginSucceeded", ActorUserId = user.Id, Detail = how, IpAddress = Ip(http) });
        await db.SaveChangesAsync();
        await AuthSetup.SignInAsync(http, user);
        return Results.Ok(await ToMe(db, user));
    }

    private static async Task<MeDto> ToMe(ClinicHubDbContext db, User u)
    {
        var access = Access.For(u);
        var providerId = await db.Providers.Where(p => p.UserId == u.Id).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        return new MeDto(u.Id, u.UserName, u.DisplayName, access.Roles, access.Permissions, providerId);
    }

    public static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(new User(), Guid.NewGuid().ToString());
}
