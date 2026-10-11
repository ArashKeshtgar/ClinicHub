using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>ClinicHub as the OpenID Connect sign-in of another application (here, a stand-in for ClinicReport).</summary>
public class SsoTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public const string Secret = "test-clinicreport-secret";
    public const string RedirectUri = "https://report.test/signin-oidc";

    private record Pkce(string Verifier, string Challenge);

    private static Pkce NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new Pkce(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string AuthorizeUrl(Pkce pkce, string extra = "") =>
        $"/connect/authorize?client_id=clinicreport&response_type=code&scope=openid%20profile%20clinic%20offline_access" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state=xyz&nonce=n1&code_challenge={pkce.Challenge}&code_challenge_method=S256{extra}";

    private static Dictionary<string, string> Query(Uri location) =>
        QueryHelpers.ParseQuery(location.Query).ToDictionary(k => k.Key, k => k.Value.ToString());

    private static JsonNode Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonNode.Parse(Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=')))!;
    }

    /// <summary>Authorize as a signed-in person, then exchange the code like the application's server does.</summary>
    private async Task<JsonNode> SignInToApp(HttpClient person)
    {
        var pkce = NewPkce();
        var authorize = await person.GetAsync(AuthorizeUrl(pkce));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var back = Query(authorize.Headers.Location!);
        Assert.Equal("xyz", back["state"]);

        var app = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await app.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = back["code"], ["redirect_uri"] = RedirectUri,
            ["client_id"] = "clinicreport", ["client_secret"] = Secret, ["code_verifier"] = pkce.Verifier,
        }));
        return await Json(token);
    }

    [Fact]
    public async Task Discovery_document_describes_the_endpoints()
    {
        var doc = await Json(await factory.CreateClient().GetAsync("/.well-known/openid-configuration"));
        Assert.EndsWith("/connect/authorize", (string)doc["authorization_endpoint"]!);
        Assert.Contains("S256", doc["code_challenge_methods_supported"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task A_physician_gets_a_token_with_the_clinic_and_only_the_apps_permissions()
    {
        var dr = await factory.SignInAsync("dr.patel", "harbourview", followRedirects: false);
        var tokens = await SignInToApp(dr);
        var id = Payload((string)tokens["id_token"]!);
        Assert.Equal("Dr. Nadia Patel", (string)id["name"]!);
        Assert.Equal("harbourview", (string)id["clinic_slug"]!);
        Assert.Equal("n1", (string)id["nonce"]!);
        var permissions = id["permission"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.Contains("reports.sign", permissions);
        Assert.DoesNotContain(permissions, p => p.Contains(':') || p == "encounters.sign");
        Assert.Null(id["stamp"]);
        Assert.NotNull(tokens["refresh_token"]);

        // The same account at the other clinic: a token for that clinic.
        var lake = await factory.SignInAsync("dr.patel", "lakeshore", followRedirects: false);
        Assert.Equal("lakeshore", (string)Payload((string)(await SignInToApp(lake))["id_token"]!)["clinic_slug"]!);

        var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)tokens["access_token"]!);
        var info = await Json(await app.GetAsync("/connect/userinfo"));
        Assert.Equal("dr.patel", (string)info["preferred_username"]!);
        Assert.Contains("reports.view", info["permission"]!.AsArray().Select(p => (string)p!));
    }

    [Fact]
    public async Task Someone_not_signed_in_is_sent_to_ClinicHub_sign_in_and_back()
    {
        var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await anonymous.GetAsync(AuthorizeUrl(NewPkce()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith("/signin?returnUrl=", location);
        Assert.StartsWith("/connect/authorize?", Uri.UnescapeDataString(location["/signin?returnUrl=".Length..]));
    }

    [Fact]
    public async Task Someone_whose_roles_do_not_include_the_app_is_refused()
    {
        var billing = await factory.SignInAsync("billing", followRedirects: false);
        var res = await billing.GetAsync(AuthorizeUrl(NewPkce()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var back = Query(res.Headers.Location!);
        Assert.Equal("access_denied", back["error"]);
        Assert.Contains("Harbourview", back["error_description"]);
    }

    [Fact]
    public async Task Ending_ClinicHub_sessions_stops_the_apps_refresh()
    {
        var dr = await factory.SignInAsync("dr.patel", "harbourview", followRedirects: false);
        var tokens = await SignInToApp(dr);
        (await dr.PostAsync("/api/auth/sessions/revoke", null)).EnsureSuccessStatusCode();

        var app = factory.CreateClient();
        var refresh = await app.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = (string)tokens["refresh_token"]!, ["client_id"] = "clinicreport", ["client_secret"] = Secret,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        Assert.Contains("invalid_grant", await refresh.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClinicHub_lists_the_apps_a_person_can_open()
    {
        var dr = await factory.SignInAsync("dr.patel");
        Assert.Contains((await Json(await dr.GetAsync("/api/apps"))).AsArray(), a => (string)a!["clientId"]! == "clinicreport");
        var billing = await factory.SignInAsync("billing");
        Assert.Empty((await Json(await billing.GetAsync("/api/apps"))).AsArray());
    }
}
