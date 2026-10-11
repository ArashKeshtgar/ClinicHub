using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;

namespace ClinicHub.Tests;

public class ConsentRuleTests
{
    private const int DrA = 1, DrB = 2;
    private static ConsentDirective D(string scope, int id = 10, int? encounter = null, int? provider = null) =>
        new() { Id = id, Scope = scope, EncounterId = encounter, ExcludedProviderId = provider };

    [Fact]
    public void Entire_record_leaves_each_visit_to_its_own_provider_and_open_visits_to_the_team()
    {
        var directives = new[] { D(ConsentScopes.EntireRecord) };
        Assert.False(ConsentView.Of(DrA, directives, []).Decide(1, DrA, EncounterStatus.Signed).Withheld);
        Assert.True(ConsentView.Of(DrB, directives, []).Decide(1, DrA, EncounterStatus.Signed).Withheld);
        Assert.True(ConsentView.Of(null, directives, []).Decide(1, DrA, EncounterStatus.Signed).Withheld);   // a nurse, a past visit
        Assert.False(ConsentView.Of(null, directives, []).Decide(1, DrA, EncounterStatus.Open).Withheld);    // rooming today's visit
    }

    [Fact]
    public void One_visit_is_withheld_even_while_open()
    {
        var directives = new[] { D(ConsentScopes.Encounter, encounter: 7) };
        Assert.True(ConsentView.Of(DrB, directives, []).Decide(7, DrA, EncounterStatus.Open).Withheld);
        Assert.False(ConsentView.Of(DrB, directives, []).Decide(8, DrA, EncounterStatus.Signed).Withheld);
        Assert.False(ConsentView.Of(DrA, directives, []).Decide(7, DrA, EncounterStatus.Signed).Withheld);
    }

    [Fact]
    public void One_provider_is_kept_out_except_from_their_own_visits()
    {
        var directives = new[] { D(ConsentScopes.Provider, provider: DrB) };
        Assert.True(ConsentView.Of(DrB, directives, []).Decide(1, DrA, EncounterStatus.Open).Withheld);
        Assert.False(ConsentView.Of(DrB, directives, []).Decide(2, DrB, EncounterStatus.Signed).Withheld);
        Assert.False(ConsentView.Of(DrA, directives, []).Decide(1, DrA, EncounterStatus.Signed).Withheld);
        Assert.False(ConsentView.Of(null, directives, []).Decide(1, DrA, EncounterStatus.Signed).Withheld);
    }

    [Fact]
    public void An_override_opens_every_directive_it_covers_and_is_named_for_the_log()
    {
        var directives = new[] { D(ConsentScopes.EntireRecord, 10), D(ConsentScopes.Provider, 11, provider: DrB) };
        var one = ConsentView.Of(DrB, directives, [new ConsentOverride { Id = 5, ConsentDirectiveId = 10 }]);
        Assert.True(one.Decide(1, DrA, EncounterStatus.Signed).Withheld);                 // directive 11 still applies
        var both = ConsentView.Of(DrB, directives, [new ConsentOverride { Id = 5, ConsentDirectiveId = 10 }, new ConsentOverride { Id = 6, ConsentDirectiveId = 11 }]);
        Assert.Equal(new ConsentView.Decision(false, 5), both.Decide(1, DrA, EncounterStatus.Signed));
        Assert.Equal(new ConsentView.Decision(false, null), ConsentView.Of(DrA, directives, []).Decide(1, DrA, EncounterStatus.Signed));
    }
}

public class ConsentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    /// <summary>A new patient checked in as a walk-in with Grace Mensah (NP, takes walk-ins every day): an open visit not Dr. Patel's.</summary>
    private async Task<(int Patient, int Encounter)> OpenVisit(string last)
    {
        var desk = await factory.SignInAsync("frontdesk");
        var patient = await Json(await desk.PostAsJsonAsync("/api/patients", new
        {
            patient = new { firstName = "Test", lastName = last, dateOfBirth = "1966-04-04", sex = "F", isDeceased = false }, confirmNew = true,
        }));
        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        var mensah = day["providers"]!.AsArray().Single(p => ((string)p!["name"]!).Contains("Mensah"))!["id"]!.GetValue<int>();
        var walkIn = await Json(await desk.PostAsJsonAsync("/api/frontdesk/walk-ins", new { patientId = patient["id"]!.GetValue<int>(), providerId = mensah, reason = "Follow-up" }));
        await Json(await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{walkIn["id"]}/check-in", new { presentedVersion = (string?)null }));
        day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        var encounter = day["appointments"]!.AsArray().Single(a => a!["id"]!.GetValue<int>() == walkIn["id"]!.GetValue<int>())!["encounterId"]!.GetValue<int>();
        return (patient["id"]!.GetValue<int>(), encounter);
    }

    private async Task<int> PatelProviderId()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        return day["providers"]!.AsArray().Single(p => ((string)p!["name"]!).Contains("Patel"))!["id"]!.GetValue<int>();
    }

    [Fact]
    public async Task A_provider_kept_out_sees_withheld_until_an_emergency_override()
    {
        var (patient, encounter) = await OpenVisit("Lockbox");
        var privacy = await factory.SignInAsync("privacy");
        await Json(await privacy.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "Provider", excludedProviderId = await PatelProviderId(), note = "Not Dr. Patel" }));

        var patel = await factory.SignInAsync("dr.patel", "harbourview");
        var blocked = await patel.GetAsync($"/api/encounters/{encounter}");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var problem = (await blocked.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.True(problem["consentWithheld"]!.GetValue<bool>());
        Assert.True(problem["canOverride"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await patel.GetAsync($"/api/encounters/{encounter}/orders")).StatusCode);
        // The nurse is not the one kept out.
        var nurse = await factory.SignInAsync("nurse");
        await Json(await nurse.GetAsync($"/api/encounters/{encounter}"));

        Assert.Equal(HttpStatusCode.BadRequest, (await patel.PostAsJsonAsync($"/api/patients/{patient}/consent/override", new { reason = "need" })).StatusCode);
        var opened = await Json(await patel.PostAsJsonAsync($"/api/patients/{patient}/consent/override", new { reason = "Chest pain in clinic, needs prior ECG history" }));
        Assert.NotNull(opened["expiresUtc"]);
        await Json(await patel.GetAsync($"/api/encounters/{encounter}"));

        var overrides = (await privacy.GetFromJsonAsync<JsonArray>("/api/consent/overrides"))!;
        Assert.Contains(overrides, o => o!["patientId"]!.GetValue<int>() == patient && ((string)o["reason"]!).StartsWith("Chest pain") && (string)o["user"]! == "Dr. Nadia Patel");
    }

    [Fact]
    public async Task Entire_record_still_lets_the_team_work_on_todays_open_visit_and_withdrawing_ends_it()
    {
        var (patient, encounter) = await OpenVisit("Wholebox");
        var privacy = await factory.SignInAsync("privacy");
        var directives = (await Json(await privacy.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "EntireRecord" }))).AsArray();
        var nurse = await factory.SignInAsync("nurse");
        await Json(await nurse.GetAsync($"/api/encounters/{encounter}"));

        // That one visit withheld too: now the nurse is kept out, even while it is open.
        await Json(await privacy.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "Encounter", encounterId = encounter }));
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync($"/api/encounters/{encounter}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PostAsJsonAsync($"/api/patients/{patient}/consent/override", new { reason = "Nurses cannot override this" })).StatusCode);

        var info = await Json(await privacy.GetAsync($"/api/patients/{patient}/consent"));
        foreach (var d in info["directives"]!.AsArray())
            await Json(await privacy.PostAsync($"/api/consent/{d!["id"]}/revoke", null));
        await Json(await nurse.GetAsync($"/api/encounters/{encounter}"));
        Assert.All((await Json(await privacy.GetAsync($"/api/patients/{patient}/consent")))["directives"]!.AsArray(), d => Assert.NotNull(d!["revokedUtc"]));
        Assert.Single(directives);
    }

    [Fact]
    public async Task Only_the_privacy_officer_records_directives_and_they_are_checked()
    {
        var (patient, _) = await OpenVisit("Rules");
        var desk = await factory.SignInAsync("frontdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "EntireRecord" })).StatusCode);
        var patel = await factory.SignInAsync("dr.patel", "harbourview");
        Assert.Equal(HttpStatusCode.Forbidden, (await patel.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "EntireRecord" })).StatusCode);

        var privacy = await factory.SignInAsync("privacy");
        var bad = await privacy.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "Encounter", encounterId = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("encounterId", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await patel.PostAsJsonAsync($"/api/patients/{patient}/consent/override", new { reason = "Nothing is withheld here" })).StatusCode);
    }
}
