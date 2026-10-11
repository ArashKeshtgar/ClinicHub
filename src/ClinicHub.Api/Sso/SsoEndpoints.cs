using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Permissions = ClinicHub.Api.Auth.Permissions;

namespace ClinicHub.Api.Sso;

/// <summary>
/// The OpenID Connect endpoints. An application sends the person here; if they are signed in to ClinicHub
/// with a clinic chosen, they go straight back with a code, otherwise ClinicHub's own pages sign them in,
/// have them choose a clinic, and return them here. The tokens carry the clinic and only that
/// application's permissions in that clinic, without the prefix.
/// </summary>
public static class SsoEndpoints
{
    public const string SsoSignIn = "SsoSignIn";

    public record AppLink(string ClientId, string Name, string? Url);

    public static void MapSsoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], Authorize).AllowAnonymous().ExcludeFromDescription();
        app.MapPost("/connect/token", Token).AllowAnonymous().ExcludeFromDescription();
        // Cast: a handler taking only HttpContext would otherwise bind as a RequestDelegate and its result be discarded.
        app.MapMethods("/connect/userinfo", ["GET", "POST"], (Func<HttpContext, Task<IResult>>)UserInfo).AllowAnonymous().ExcludeFromDescription();
        app.MapMethods("/connect/logout", ["GET", "POST"], Logout).AllowAnonymous().ExcludeFromDescription();

        // The applications this person can open from the current clinic.
        app.MapGet("/api/apps", (ClaimsPrincipal user, IOptions<SsoOptions> sso) =>
        {
            var granted = user.FindAll(AppClaims.Permission).Select(c => c.Value).ToList();
            return sso.Value.Clients.Where(c => Permissions.ForApp(granted, c.ClientId).Length > 0)
                .Select(c => new AppLink(c.ClientId, c.DisplayName, c.LaunchUrl));
        }).WithTags("Auth");
    }

    private static async Task<IResult> Authorize(HttpContext http, CatalogDbContext catalog, ClinicDatabases clinics, SecurityLog log)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        // Coming back here after ClinicHub's pages: the same request, without prompt=login (that was answered by signing in).
        var parameters = (http.Request.HasFormContentType
                ? http.Request.Form.Select(f => KeyValuePair.Create(f.Key, (string?)f.Value.ToString()))
                : http.Request.Query.Select(q => KeyValuePair.Create(q.Key, (string?)q.Value.ToString())))
            .Where(p => p.Key != Parameters.Prompt || p.Value != PromptValues.Login);
        var here = http.Request.PathBase + http.Request.Path + QueryString.Create(parameters);
        // ClinicHub's pages sign in, finish a pending password or two-factor step, or choose the clinic, then come back here.
        IResult ToClinicHub() => Results.Redirect($"/signin?returnUrl={Uri.EscapeDataString(here)}");

        var cookie = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (request.HasPromptValue(PromptValues.Login))
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return ToClinicHub();
        }
        if (!cookie.Succeeded)
            return request.HasPromptValue(PromptValues.None) ? Refuse(Errors.LoginRequired, "Not signed in to ClinicHub.") : ToClinicHub();

        var principal = cookie.Principal!;
        var account = await catalog.Accounts.AsNoTracking().WithRoles().SingleOrDefaultAsync(a => a.Id == principal.UserId() && a.IsActive);
        if (account is null)
            return ToClinicHub();
        var clinicId = principal.ClinicId();
        var access = Access.For(account, clinicId);
        if (access.Pending is not null || clinicId is null)
            return request.HasPromptValue(PromptValues.None) ? Refuse(Errors.InteractionRequired, "Finish signing in to ClinicHub first.") : ToClinicHub();

        var appPermissions = Permissions.ForApp(access.Permissions, request.ClientId!);
        var clinic = account.Roles.Select(r => r.Role.Clinic).First(c => c.Id == clinicId);
        if (appPermissions.Length == 0)
        {
            await log.WriteAsync(SecurityEventTypes.SsoRefused, account.Id, detail: $"{request.ClientId}: no permission", clinicId: clinicId);
            return Refuse(Errors.AccessDenied, $"No role you hold at {clinic.Name} gives access to {request.ClientId}.");
        }

        var identity = Identity(account, clinic, appPermissions, access.Roles, request.GetScopes());
        await log.WriteAsync(SecurityEventTypes.SsoSignIn, account.Id, detail: request.ClientId, clinicId: clinicId);
        return Results.SignIn(new ClaimsPrincipal(identity), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Exchanges a code or a refresh token. The person is read again: a deactivated account, a new security
    /// stamp, or a role taken away stops the application's session at its next refresh.
    /// </summary>
    private static async Task<IResult> Token(HttpContext http, CatalogDbContext catalog)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Not an OpenID Connect request.");
        if (request.IsClientCredentialsGrantType())
        {
            // An application on its own behalf (OpenIddict has already checked its secret): it may only send results.
            var app = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
            app.SetClaim(Claims.Subject, request.ClientId).SetClaim(Claims.ClientId, request.ClientId);
            app.SetScopes(request.GetScopes().Intersect([SsoClaims.ResultsScope]));
            app.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(app), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return Refuse(Errors.UnsupportedGrantType, "Only the authorization code, refresh token and client credentials grants are supported.");

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var previous = result.Principal!;
        var accountId = int.Parse(previous.GetClaim(Claims.Subject)!);
        var clinicId = int.Parse(previous.GetClaim(SsoClaims.ClinicId)!);
        var account = await catalog.Accounts.AsNoTracking().WithRoles().SingleOrDefaultAsync(a => a.Id == accountId && a.IsActive);
        if (account is null || account.SecurityStamp != previous.GetClaim(SsoClaims.Stamp))
            return Refuse(Errors.InvalidGrant, "The ClinicHub session has ended. Sign in again.");

        var access = Access.For(account, clinicId);
        var appPermissions = Permissions.ForApp(access.Permissions, request.ClientId!);
        var clinic = account.Roles.Select(r => r.Role.Clinic).FirstOrDefault(c => c.Id == clinicId);
        if (clinic is null || appPermissions.Length == 0)
            return Refuse(Errors.InvalidGrant, "Access to this application at this clinic has been removed.");

        var identity = Identity(account, clinic, appPermissions, access.Roles, previous.GetScopes());
        return Results.SignIn(new ClaimsPrincipal(identity), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> UserInfo(HttpContext http)
    {
        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Principal is not { } p)
            return Results.Challenge(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        var info = new Dictionary<string, object?>
        {
            [Claims.Subject] = p.GetClaim(Claims.Subject),
            [Claims.Name] = p.GetClaim(Claims.Name),
            [Claims.PreferredUsername] = p.GetClaim(Claims.PreferredUsername),
            [SsoClaims.Credentials] = p.GetClaim(SsoClaims.Credentials),
        };
        if (p.HasScope(SsoClaims.ClinicScope))
        {
            info[SsoClaims.ClinicId] = p.GetClaim(SsoClaims.ClinicId);
            info[SsoClaims.ClinicSlug] = p.GetClaim(SsoClaims.ClinicSlug);
            info[SsoClaims.ClinicName] = p.GetClaim(SsoClaims.ClinicName);
            info[Claims.Role] = p.GetClaims(Claims.Role).ToArray();
            info[SsoClaims.Permission] = p.GetClaims(SsoClaims.Permission).ToArray();
        }
        return Results.Ok(info);
    }

    /// <summary>Signing out of an application signs out of ClinicHub too, then returns to the application.</summary>
    private static async Task<IResult> Logout(HttpContext http, SecurityLog log)
    {
        if (http.User.UserId() is { } id)
            await log.WriteAsync(SecurityEventTypes.Logout, id, detail: "From an application", clinicId: http.User.ClinicId());
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static ClaimsIdentity Identity(Account account, Clinic clinic, string[] appPermissions, string[] roles, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString())
            .SetClaim(Claims.Name, account.DisplayName)
            .SetClaim(Claims.PreferredUsername, account.UserName)
            .SetClaim(SsoClaims.Credentials, account.Credentials)
            .SetClaim(SsoClaims.ClinicId, clinic.Id.ToString())
            .SetClaim(SsoClaims.ClinicSlug, clinic.Slug)
            .SetClaim(SsoClaims.ClinicName, clinic.Name)
            .SetClaim(SsoClaims.Stamp, account.SecurityStamp)
            .SetClaims(Claims.Role, [.. roles])
            .SetClaims(SsoClaims.Permission, [.. appPermissions]);
        if (account.TwoFactorEnabled)
            identity.SetClaim(Claims.AuthenticationMethodReference, "mfa");
        // Scopes first: which claims go into which token depends on them.
        identity.SetScopes(scopes);
        identity.SetDestinations(Destinations);
        return identity;
    }

    /// <summary>Everything reaches the access token; identity claims also the id token. The stamp stays with ClinicHub.</summary>
    private static IEnumerable<string> Destinations(Claim claim) => claim.Type switch
    {
        SsoClaims.Stamp => [],
        Claims.Name or Claims.PreferredUsername or SsoClaims.Credentials when claim.Subject!.HasScope(Scopes.Profile)
            => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
        SsoClaims.ClinicId or SsoClaims.ClinicSlug or SsoClaims.ClinicName or Claims.Role or SsoClaims.Permission when claim.Subject!.HasScope(SsoClaims.ClinicScope)
            => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
        Claims.Subject or Claims.AuthenticationMethodReference => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
        _ => [OpenIddictConstants.Destinations.AccessToken],
    };

    private static IResult Refuse(string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
