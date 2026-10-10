using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>One sign-in for every clinic, one database per clinic, and the account security moved from ClinicReport.</summary>
public class PlatformTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Anonymous()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ClinicHub", "1");
        return c;
    }

    private static async Task<JsonNode> Choose(HttpClient c, JsonNode me, string slug)
    {
        var id = me["clinics"]!.AsArray().Single(x => (string)x!["slug"]! == slug)!["id"]!.GetValue<int>();
        return await Json(await c.PostAsJsonAsync("/api/auth/clinic", new { clinicId = id }));
    }

    [Fact]
    public async Task A_physician_at_two_clinics_chooses_and_each_has_its_own_patients()
    {
        var c = Anonymous();
        var me = await Json(await c.PostAsJsonAsync("/api/auth/demo-login", new { userName = "dr.patel" }));
        Assert.Null(me["clinic"]);
        Assert.Equal(["harbourview", "lakeshore"], me["clinics"]!.AsArray().Select(x => (string)x!["slug"]!).Order());
        Assert.Empty(me["permissions"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/encounters")).StatusCode);

        var harbour = await Choose(c, me, "harbourview");
        var harbourProvider = (int)harbour["providerId"]!;
        Assert.NotEmpty(await c.GetFromJsonAsync<JsonArray>("/api/patients?q=HV000001") ?? []);
        Assert.Empty(await c.GetFromJsonAsync<JsonArray>("/api/patients?q=LS000001") ?? []);

        var lake = await Choose(c, me, "lakeshore");
        Assert.Equal("lakeshore", (string)lake["clinic"]!["slug"]!);
        Assert.NotNull(lake["providerId"]);
        Assert.Empty(await c.GetFromJsonAsync<JsonArray>("/api/patients?q=HV000001") ?? []);
        Assert.NotEmpty(await c.GetFromJsonAsync<JsonArray>("/api/patients?q=LS000001") ?? []);

        // The same billing number at both, each clinic's own group number.
        await using var harbourDb = new TestDatabaseReader(factory).Context();
        await using var lakeDb = new TestDatabaseReader(factory, "lakeshore").Context();
        var a = await harbourDb.Providers.SingleAsync(p => p.Id == harbourProvider);
        var b = await lakeDb.Providers.SingleAsync(p => p.Id == (int)lake["providerId"]!);
        Assert.Equal(("123456", "123456"), (a.OhipBillingNumber, b.OhipBillingNumber));
        Assert.NotEqual(await harbourDb.Locations.Select(l => l.OhipGroupNumber).FirstAsync(), await lakeDb.Locations.Select(l => l.OhipGroupNumber).FirstAsync());
    }

    [Fact]
    public async Task Someone_cannot_enter_a_clinic_they_do_not_belong_to()
    {
        var desk = await factory.SignInAsync("frontdesk");
        await using var catalog = new TestDatabaseReader(factory).Catalog();
        var lakeshore = await catalog.Clinics.Where(c => c.Slug == "lakeshore").Select(c => c.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync("/api/auth/clinic", new { clinicId = lakeshore })).StatusCode);
    }

    [Fact]
    public async Task Roles_belong_to_one_clinic()
    {
        var harbour = await factory.SignInAsync("admin", "harbourview");
        (await harbour.PostAsJsonAsync("/api/roles", new { name = "Echo typist", permissions = new[] { "patients.view" }, requireTwoFactor = false })).EnsureSuccessStatusCode();
        var lake = await factory.SignInAsync("admin", "lakeshore");
        var roles = await Json(await lake.GetAsync("/api/roles"));
        Assert.DoesNotContain(roles.AsArray(), r => (string)r!["name"]! == "Echo typist");
        Assert.Contains(roles.AsArray(), r => (string)r!["name"]! == SystemRoles.FrontDesk);
    }

    [Fact]
    public async Task An_existing_account_joins_another_clinic_keeping_its_own_password()
    {
        var admin = await factory.SignInAsync("admin", "harbourview");
        var physician = (await Json(await admin.GetAsync("/api/roles"))).AsArray().Single(r => (string)r!["name"]! == SystemRoles.Physician)!;
        var added = await Json(await admin.PostAsJsonAsync("/api/users", new { userName = "dr.okoye", roleIds = new[] { (int)physician["id"]! } }));
        Assert.Equal(["Lakeshore Family Health (demo)"], added["otherClinics"]!.AsArray().Select(x => (string)x!));
        var id = (int)added["id"]!;

        // Harbourview can't change the password of someone who also works at Lakeshore.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/users/{id}/password", new { password = "Another-pass-123" })).StatusCode);

        await using (var db = new TestDatabaseReader(factory).Context())
            Assert.True((await db.Users.SingleAsync(u => u.Id == id)).IsActive);

        // No roles left here: no longer a member, the clinic's copy stays for history.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/users/{id}", new { displayName = "x", roleIds = Array.Empty<int>() })).StatusCode);
        await using (var db = new TestDatabaseReader(factory).Context())
            Assert.False((await db.Users.SingleAsync(u => u.Id == id)).IsActive);
    }

    [Fact]
    public async Task A_new_person_chooses_a_password_then_works()
    {
        var admin = await factory.SignInAsync("admin", "harbourview");
        var desk = (await Json(await admin.GetAsync("/api/roles"))).AsArray().Single(r => (string)r!["name"]! == SystemRoles.FrontDesk)!;
        await Json(await admin.PostAsJsonAsync("/api/users", new
        {
            userName = "new.desk", displayName = "New Desk", roleIds = new[] { (int)desk["id"]! }, password = "Temporary-pass-1",
        }));

        var c = Anonymous();
        var me = await Json(await c.PostAsJsonAsync("/api/auth/login", new { userName = "new.desk", password = "Temporary-pass-1" }));
        Assert.Equal("change-password", (string)me["pendingAction"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/frontdesk/day")).StatusCode);

        var changed = await Json(await c.PostAsJsonAsync("/api/auth/password", new { currentPassword = "Temporary-pass-1", newPassword = "My-own-pass-2026" }));
        Assert.Null(changed["pendingAction"]);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/frontdesk/day")).StatusCode);
    }

    [Fact]
    public async Task Two_factor_codes_work_once_and_wrong_passwords_lock_the_account()
    {
        var admin = await factory.SignInAsync("admin", "harbourview");
        var billing = (await Json(await admin.GetAsync("/api/roles"))).AsArray().Single(r => (string)r!["name"]! == SystemRoles.Billing)!;
        await Json(await admin.PostAsJsonAsync("/api/users", new { userName = "two.factor", displayName = "Two Factor", roleIds = new[] { (int)billing["id"]! }, password = "Temporary-pass-1" }));

        var c = Anonymous();
        await Json(await c.PostAsJsonAsync("/api/auth/login", new { userName = "two.factor", password = "Temporary-pass-1" }));
        await Json(await c.PostAsJsonAsync("/api/auth/password", new { currentPassword = "Temporary-pass-1", newPassword = "Second-pass-2026" }));
        var setup = await Json(await c.PostAsync("/api/auth/two-factor/setup", null));
        var secret = (string)setup["secret"]!;
        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        await Json(await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = Totp.Code(secret, step) }));

        var other = Anonymous();
        var noCode = await other.PostAsJsonAsync("/api/auth/login", new { userName = "two.factor", password = "Second-pass-2026" });
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Contains("twoFactorRequired", await noCode.Content.ReadAsStringAsync());
        // The code used to enable is spent; the next step's code works once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsJsonAsync("/api/auth/login", new { userName = "two.factor", password = "Second-pass-2026", code = Totp.Code(secret, step) })).StatusCode);
        await Json(await other.PostAsJsonAsync("/api/auth/login", new { userName = "two.factor", password = "Second-pass-2026", code = Totp.Code(secret, step + 1) }));

        for (var i = 0; i < AuthEndpoints.MaxFailedLogins; i++)
            await Anonymous().PostAsJsonAsync("/api/auth/login", new { userName = "two.factor", password = "wrong-password!" });
        Assert.Equal(HttpStatusCode.Locked, (await Anonymous().PostAsJsonAsync("/api/auth/login", new { userName = "two.factor", password = "Second-pass-2026" })).StatusCode);
    }

    [Fact]
    public async Task A_platform_administrator_opens_a_clinic_with_its_own_database()
    {
        var admin = await factory.SignInAsync("admin", "harbourview");
        var created = await Json(await admin.PostAsJsonAsync("/api/platform/clinics", new { slug = "riverside", name = "Riverside Clinic" }));
        var database = (string)created["databaseName"]!;
        Assert.EndsWith("riverside", database);

        await using (var conn = new SqlConnection(new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CLINICHUB_TEST_SQL")
            ?? "Server=.;Integrated Security=True;TrustServerCertificate=True") { InitialCatalog = database }.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM ref.FeeCode", conn);
            Assert.True((int)(await cmd.ExecuteScalarAsync())! > 0);
        }

        var me = await Json(await admin.GetAsync("/api/auth/me"));
        var riverside = await Choose(admin, me, "riverside");
        Assert.Contains(SystemRoles.Administrator, riverside["roles"]!.AsArray().Select(r => (string)r!));
        Assert.Empty((await Json(await admin.GetAsync("/api/frontdesk/day")))["appointments"]!.AsArray());

        var desk = await factory.SignInAsync("frontdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync("/api/platform/clinics", new { slug = "nope", name = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task The_security_log_shows_who_entered_the_clinic()
    {
        await factory.SignInAsync("frontdesk");
        var privacy = await factory.SignInAsync("privacy");
        var log = await Json(await privacy.GetAsync("/api/audit"));
        Assert.Contains(log.AsArray(), e => (string)e!["type"]! == "ClinicSelected" && (string?)e["actor"] == "Jordan Lee");
        Assert.Equal(HttpStatusCode.OK, (await privacy.GetAsync("/api/audit/access")).StatusCode);
    }
}
