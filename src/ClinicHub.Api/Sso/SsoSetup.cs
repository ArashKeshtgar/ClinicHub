using System.Security.Cryptography.X509Certificates;
using ClinicHub.Api.Catalog;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace ClinicHub.Api.Sso;

/// <summary>An application that signs its users in through ClinicHub.</summary>
public class SsoClient
{
    /// <summary>Also the prefix of the application's permissions, e.g. "clinicreport".</summary>
    public string ClientId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Server-side applications are confidential and authenticate with this secret.</summary>
    public string? ClientSecret { get; set; }
    public string[] RedirectUris { get; set; } = [];
    public string[] PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Where people open the application, for the list of applications on ClinicHub.</summary>
    public string? LaunchUrl { get; set; }
}

public class SsoOptions
{
    /// <summary>
    /// The address applications know ClinicHub by, e.g. https://hub.example.ca/. Set it when ClinicHub sits behind
    /// a reverse proxy or, in development, behind the Vite server; otherwise it comes from each request.
    /// </summary>
    public string? Issuer { get; set; }

    public List<SsoClient> Clients { get; set; } = [];

    /// <summary>A PFX used to sign tokens in production. Development uses ASP.NET's development certificate store.</summary>
    public string? SigningCertificatePath { get; set; }
    public string? SigningCertificatePassword { get; set; }
}

/// <summary>Claims ClinicHub puts in the tokens it issues, beyond the standard OpenID Connect ones.</summary>
public static class SsoClaims
{
    public const string ClinicId = "clinic_id";
    public const string ClinicSlug = "clinic_slug";
    public const string ClinicName = "clinic_name";
    public const string Permission = "permission";
    public const string Credentials = "credentials";

    /// <summary>The account's security stamp when the token was issued; kept in the token, never sent to the application.</summary>
    public const string Stamp = "stamp";

    /// <summary>The scope that asks for the clinic and the permissions in it.</summary>
    public const string ClinicScope = "clinic";

    /// <summary>An application sending results to ClinicHub (client credentials).</summary>
    public const string ResultsScope = "results";
}

public static class SsoSetup
{
    /// <summary>
    /// ClinicHub as an OpenID Connect provider: authorization code flow with PKCE, refresh tokens, userinfo
    /// and sign-out. Registered applications, authorizations and tokens are stored in the catalog database.
    /// </summary>
    public static void AddClinicSso(this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        services.Configure<SsoOptions>(config.GetSection("Sso"));
        var sso = config.GetSection("Sso").Get<SsoOptions>() ?? new SsoOptions();

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<CatalogDbContext>())
            // ClinicHub's own endpoints that applications call (results) accept its own tokens.
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
            })
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetEndSessionEndpointUris("connect/logout");
                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                o.AllowRefreshTokenFlow();
                // Applications calling ClinicHub on their own (results back): client credentials, scope "results".
                o.AllowClientCredentialsFlow();
                o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, SsoClaims.ClinicScope, SsoClaims.ResultsScope);
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(30));
                if (sso.Issuer is { Length: > 0 } issuer)
                    o.SetIssuer(new Uri(issuer));

                if (env.IsEnvironment("Testing"))
                    o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                else if (sso.SigningCertificatePath is { Length: > 0 } path)
                {
                    var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, sso.SigningCertificatePassword);
                    o.AddSigningCertificate(certificate).AddEncryptionCertificate(certificate);
                }
                else
                    o.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();

                // Applications read the access token's claims themselves (ClinicReport, LabFlow): signed, not encrypted.
                o.DisableAccessTokenEncryption();

                var aspNet = o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
                if (env.IsDevelopment() || env.IsEnvironment("Testing"))
                    aspNet.DisableTransportSecurityRequirement();
            });
    }

    /// <summary>Creates or updates the registered applications from configuration (Sso:Clients).</summary>
    public static async Task RegisterClientsAsync(IServiceProvider services)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SsoOptions>>().Value;
        foreach (var client in options.Clients)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret,
                ClientType = client.ClientSecret is null ? ClientTypes.Public : ClientTypes.Confidential,
                DisplayName = client.DisplayName,
                // First-party applications of the same clinics: no consent screen.
                ConsentType = ConsentTypes.Implicit,
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.EndSession,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    OpenIddictConstants.Permissions.Scopes.Profile,
                    OpenIddictConstants.Permissions.Prefixes.Scope + SsoClaims.ClinicScope,
                    OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
                    OpenIddictConstants.Permissions.Prefixes.Scope + SsoClaims.ResultsScope,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            };
            foreach (var uri in client.RedirectUris)
                descriptor.RedirectUris.Add(new Uri(uri));
            foreach (var uri in client.PostLogoutRedirectUris)
                descriptor.PostLogoutRedirectUris.Add(new Uri(uri));

            if (await manager.FindByClientIdAsync(client.ClientId) is { } existing)
                await manager.UpdateAsync(existing, descriptor);
            else
                await manager.CreateAsync(descriptor);
        }
    }
}
