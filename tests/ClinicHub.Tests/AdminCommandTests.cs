using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Catalog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>The server's one-off commands: the first administrator of a platform nobody can sign in to yet.</summary>
public class AdminCommandTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Anonymous()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-ClinicHub", "1");
        return c;
    }

    private async Task<(string Summary, string Password)> Admin(params string[] words)
    {
        using var scope = factory.Services.CreateScope();
        return await AdminCommands.AdminAsync(scope.ServiceProvider, words[0], words[1..]);
    }

    [Fact]
    public async Task The_first_administrator_gets_a_temporary_password_and_the_clinics_named()
    {
        Anonymous();
        var (summary, password) = await Admin("Owner.One", "Owner One", "harbourview");
        Assert.Equal("Created owner.one (Owner One), platform administrator, Administrator at harbourview.", summary);

        var c = Anonymous();
        var me = await Json(await c.PostAsJsonAsync("/api/auth/login", new { userName = "owner.one", password }));
        Assert.Equal("change-password", (string)me["pendingAction"]!);
        Assert.True((bool)me["isPlatformAdmin"]!);
        Assert.Equal(["harbourview"], me["clinics"]!.AsArray().Select(x => (string)x!["slug"]!));
    }

    [Fact]
    public async Task Running_it_again_resets_the_password_and_the_old_one_stops_working()
    {
        Anonymous();
        var (_, first) = await Admin("owner.two", "Owner Two");
        var (summary, second) = await Admin("owner.two");
        Assert.StartsWith("Reset owner.two (Owner Two)", summary);
        Assert.NotEqual(first, second);

        var refused = await Anonymous().PostAsJsonAsync("/api/auth/login", new { userName = "owner.two", password = first });
        Assert.False(refused.IsSuccessStatusCode);
        var me = await Json(await Anonymous().PostAsJsonAsync("/api/auth/login", new { userName = "owner.two", password = second }));
        Assert.Equal("change-password", (string)me["pendingAction"]!);
    }

    [Fact]
    public async Task Words_that_are_not_commands_print_the_usage_and_fail()
    {
        Anonymous();
        using var scope = factory.Services.CreateScope();
        var previous = Environment.ExitCode;
        Assert.False(await AdminCommands.RunAsync(["--Demo:Enabled=true"], scope.ServiceProvider));
        Assert.True(await AdminCommands.RunAsync(["admni"], scope.ServiceProvider));
        Assert.Equal(2, Environment.ExitCode);
        Environment.ExitCode = previous;
    }
}
