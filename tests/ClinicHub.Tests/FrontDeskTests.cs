using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicHub.Tests;

/// <summary>The API over a fresh demo database.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ClinicHub", _db.ConnectionString);
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("RateLimits:LoginsPerMinute", "1000");
    }

    public async Task<HttpClient> SignInAsync(string user)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ClinicHub", "1");
        (await c.PostAsJsonAsync("/api/auth/demo-login", new { userName = user })).EnsureSuccessStatusCode();
        return c;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
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

/// <summary>Reads the factory's database directly to check side effects.</summary>
internal sealed class TestDatabaseReader(ApiFactory factory)
{
    public ClinicHub.Api.Data.ClinicHubDbContext Context() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<ClinicHub.Api.Data.ClinicHubDbContext>();
}
