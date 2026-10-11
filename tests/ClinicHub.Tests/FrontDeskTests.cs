using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicHub.Tests;

/// <summary>The API over a fresh demo platform: its own catalog and its own database per demo clinic, dropped afterwards.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _server = Environment.GetEnvironmentVariable("CLINICHUB_TEST_SQL")
        ?? "Server=.;Integrated Security=True;TrustServerCertificate=True";
    private readonly string _prefix = $"ClinicHub_Test_{Guid.NewGuid():N}_";

    private string Catalog => new SqlConnectionStringBuilder(_server) { InitialCatalog = _prefix + "catalog" }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Catalog", Catalog);
        builder.UseSetting("ConnectionStrings:ClinicServer", _server);
        builder.UseSetting("Clinics:DatabasePrefix", _prefix);
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("RateLimits:LoginsPerMinute", "1000");
        builder.UseSetting("Sso:Clients:0:ClientId", "clinicreport");
        builder.UseSetting("Sso:Clients:0:DisplayName", "ClinicReport");
        builder.UseSetting("Sso:Clients:0:ClientSecret", SsoTests.Secret);
        builder.UseSetting("Sso:Clients:0:RedirectUris:0", SsoTests.RedirectUri);
        builder.UseSetting("Sso:Clients:0:PostLogoutRedirectUris:0", "https://report.test/");
        builder.UseSetting("Sso:Clients:0:LaunchUrl", "https://report.test/");
        builder.UseSetting("Sso:Issuer", "http://localhost/");
        // Orders: no background worker (tests dispatch on demand), and stand-in applications.
        builder.UseSetting("Integrations:PollSeconds", "0");
        builder.UseSetting("Integrations:LabFlow:FhirBaseUrl", "https://lab.test/fhir");
        builder.UseSetting("Integrations:ClinicReport:FhirBaseUrl", "https://report.test/fhir");
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(ClinicHub.Api.Integrations.OutboxDispatcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Fhir));
    }

    /// <summary>LabFlow and ClinicReport as seen by ClinicHub's outbox: records each request, answers as told.</summary>
    public FakeFhirServer Fhir { get; } = new();

    /// <summary>Signs in a demo user; someone who works at several clinics is put in <paramref name="clinic"/>.</summary>
    public async Task<HttpClient> SignInAsync(string user, string clinic = "harbourview", bool followRedirects = true)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = followRedirects });
        c.DefaultRequestHeaders.Add("X-ClinicHub", "1");
        var me = await (await c.PostAsJsonAsync("/api/auth/demo-login", new { userName = user })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonNode>();
        if (me!["clinic"] is null)
        {
            var id = me["clinics"]!.AsArray().Single(x => (string)x!["slug"]! == clinic)!["id"]!.GetValue<int>();
            (await c.PostAsJsonAsync("/api/auth/clinic", new { clinicId = id })).EnsureSuccessStatusCode();
        }
        return c;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(_server);
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var list = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE @p", conn))
        {
            list.Parameters.AddWithValue("@p", _prefix + "%");
            await using var r = await list.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        }
        foreach (var name in names)
        {
            await using var drop = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

public class FrontDeskTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    /// <summary>A new walk-in today for a patient with a valid Ontario card.</summary>
    private static async Task<int> NewWalkIn(HttpClient desk)
    {
        var patients = (await desk.GetFromJsonAsync<JsonArray>("/api/patients?q=HV000001"))!;
        var created = await Json(await desk.PostAsJsonAsync("/api/frontdesk/walk-ins", new { patientId = patients[0]!["id"]!.GetValue<int>(), reason = "Cough" }));
        return created["id"]!.GetValue<int>();
    }

    [Fact]
    public async Task Api_requires_sign_in() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/frontdesk/day")).StatusCode);

    [Fact]
    public async Task Demo_clinic_has_a_week_of_appointments()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        Assert.Equal(5, day["providers"]!.AsArray().Count);
        Assert.Equal(9, day["rooms"]!.AsArray().Count);
        var tuesday = await Json(await desk.GetAsync($"/api/frontdesk/day?date={NextWeekday(DayOfWeek.Tuesday):yyyy-MM-dd}"));
        Assert.InRange(tuesday["appointments"]!.AsArray().Count, 60, 160);
    }

    [Fact]
    public async Task Patient_moves_through_the_visit_in_order()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var id = await NewWalkIn(desk);

        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/status", new { status = "Roomed", roomId = 1 })).StatusCode);

        var checkIn = await Json(await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/check-in", new { }));
        Assert.Equal("HCP", (string)checkIn["eligibility"]!["paymentProgram"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/check-in", new { })).StatusCode);

        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        var busy = day["appointments"]!.AsArray().Where(a => a!["roomId"] is not null && (string)a["status"]! is "Roomed" or "WithProvider").Select(a => (int)a!["roomId"]!).ToHashSet();
        var room = day["rooms"]!.AsArray().Select(r => (int)r!["id"]!).First(r => !busy.Contains(r));

        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/status", new { status = "Roomed", roomId = room })).EnsureSuccessStatusCode();
        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/status", new { status = "WithProvider" })).EnsureSuccessStatusCode();
        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/status", new { status = "Completed" })).EnsureSuccessStatusCode();

        day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        var row = day["appointments"]!.AsArray().Single(a => (int)a!["id"]! == id)!;
        Assert.Equal("Completed", (string)row["status"]!);
        Assert.NotNull(row["arrivedUtc"]);
        Assert.NotNull(row["completedUtc"]);
        Assert.NotNull(row["encounterId"]);
    }

    [Fact]
    public async Task An_occupied_room_cannot_take_a_second_patient()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var first = await NewWalkIn(desk);
        var second = await NewWalkIn(desk);
        foreach (var id in new[] { first, second })
            (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/check-in", new { })).EnsureSuccessStatusCode();

        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        var busy = day["appointments"]!.AsArray().Where(a => a!["roomId"] is not null && (string)a["status"]! is "Roomed" or "WithProvider").Select(a => (int)a!["roomId"]!).ToHashSet();
        var room = day["rooms"]!.AsArray().Select(r => (int)r!["id"]!).First(r => !busy.Contains(r));

        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{first}/status", new { status = "Roomed", roomId = room })).EnsureSuccessStatusCode();
        var clash = await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{second}/status", new { status = "Roomed", roomId = room });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("occupied", await clash.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Check_in_is_logged_for_phipa()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var id = await NewWalkIn(desk);
        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{id}/check-in", new { presentedVersion = "ZZ" })).EnsureSuccessStatusCode();

        await using var db = new TestDatabaseReader(factory).Context();
        Assert.Contains(db.PatientAccessLog, l => l.Context == $"Check-in, appointment {id}");
    }

    [Fact]
    public async Task Billing_clerk_can_view_but_not_move_patients()
    {
        var billing = await factory.SignInAsync("billing");
        Assert.Equal(HttpStatusCode.OK, (await billing.GetAsync("/api/frontdesk/day")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await billing.PostAsJsonAsync("/api/frontdesk/walk-ins", new { patientId = 1 })).StatusCode);

        var privacy = await factory.SignInAsync("privacy");
        Assert.Equal(HttpStatusCode.Forbidden, (await privacy.GetAsync("/api/frontdesk/day")).StatusCode);
    }

    private static DateOnly NextWeekday(DayOfWeek day)
    {
        var d = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ClinicHub.Api.Data.DemoSeeder.Toronto)).AddDays(1);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d;
    }
}

/// <summary>Reads a demo clinic's database (Harbourview unless named) directly to check side effects.</summary>
internal sealed class TestDatabaseReader(ApiFactory factory, string clinic = "harbourview")
{
    public ClinicHub.Api.Data.ClinicHubDbContext Context()
    {
        using var scope = factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ClinicHub.Api.Catalog.CatalogDbContext>();
        var c = catalog.Clinics.Single(x => x.Slug == clinic);
        return factory.Services.GetRequiredService<ClinicHub.Api.Catalog.ClinicDatabases>().Open(c);
    }

    public ClinicHub.Api.Catalog.CatalogDbContext Catalog() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<ClinicHub.Api.Catalog.CatalogDbContext>();
}

/// <summary>Answers FHIR transaction Bundles the way LabFlow and ClinicReport do, or fails on request.</summary>
public sealed class FakeFhirServer : HttpMessageHandler
{
    public record Received(Uri Url, string Token, JsonNode Bundle);

    public List<Received> Requests { get; } = [];
    public HttpStatusCode? FailWith { get; set; }
    private int _next = 100;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bundle = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        lock (Requests)
            Requests.Add(new Received(request.RequestUri!, request.Headers.Authorization?.Parameter ?? "", bundle));
        if (FailWith is { } status)
            return new HttpResponseMessage(status) { Content = new StringContent("""{"resourceType":"OperationOutcome","issue":[{"severity":"error","diagnostics":"down for maintenance"}]}""") };
        var entries = new JsonArray();
        foreach (var e in bundle["entry"]!.AsArray())
            entries.Add(new JsonObject { ["response"] = new JsonObject { ["status"] = "201 Created", ["location"] = $"{(string)e!["resource"]!["resourceType"]!}/{_next++}" } });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction-response", ["entry"] = entries }.ToJsonString()),
        };
    }
}
