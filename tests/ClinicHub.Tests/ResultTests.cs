using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>Results back: an application's DiagnosticReport answers a ClinicHub order and lands in the inbox.</summary>
public class ResultTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>What LabFlow or ClinicReport does first: a token for itself, with its own secret.</summary>
    private async Task<string> AppToken(string clientId, string secret, string scope = "results")
    {
        var res = await factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = scope,
        }));
        return (string)(await Json(res))["access_token"]!;
    }

    private async Task<HttpResponseMessage> Post(string token, JsonNode bundle)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/fhir") { Content = new StringContent(bundle.ToJsonString(), Encoding.UTF8, "application/fhir+json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await factory.CreateClient().SendAsync(req);
    }

    /// <summary>A visit of Dr. Patel's with one delivered order.</summary>
    private async Task<(HttpClient Dr, int Encounter, int Order)> Ordered(int patient, string target, string code)
    {
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null).OrderByDescending(p => p.Id).Skip(patient).FirstAsync());
        var dr = await factory.SignInAsync("dr.patel");
        factory.Fhir.FailWith = null;
        var placed = await Json(await dr.PostAsJsonAsync($"/api/encounters/{id}/orders", new { items = new[] { new { target, code } }, priority = "Routine" }));
        await factory.Services.GetRequiredService<OutboxDispatcher>().DispatchAsync();
        return (dr, id, (int)placed[0]!["id"]!);
    }

    private static JsonObject Report(int order, string status, string? conclusion, params JsonObject[] observations) => new()
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["entry"] = new JsonArray([
            new JsonObject
            {
                ["fullUrl"] = "urn:uuid:report",
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "DiagnosticReport", ["status"] = status,
                    ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:test:report", ["value"] = $"R-{order}" }),
                    ["basedOn"] = new JsonArray(new JsonObject { ["identifier"] = new JsonObject { ["system"] = "urn:clinichub:harbourview:service-request", ["value"] = order.ToString() } }),
                    ["conclusion"] = conclusion,
                    ["presentedForm"] = new JsonArray(new JsonObject { ["contentType"] = "text/html", ["url"] = $"https://report.test/reports/{order}" }),
                    ["result"] = new JsonArray(observations.Select((_, i) => (JsonNode)new JsonObject { ["reference"] = $"urn:uuid:obs-{i}" }).ToArray()),
                },
            },
            .. observations.Select((o, i) => new JsonObject { ["fullUrl"] = $"urn:uuid:obs-{i}", ["resource"] = o }),
        ]),
    };

    private static JsonObject Observation(string name, decimal value, string unit, string flag, string range) => new()
    {
        ["resourceType"] = "Observation", ["status"] = "final", ["code"] = new JsonObject { ["text"] = name },
        ["valueQuantity"] = new JsonObject { ["value"] = value, ["unit"] = unit },
        ["interpretation"] = new JsonArray(new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = flag }) }),
        ["referenceRange"] = new JsonArray(new JsonObject { ["text"] = range }),
    };

    [Fact]
    public async Task A_signed_echo_report_reaches_the_ordering_physicians_inbox()
    {
        var (dr, encounter, order) = await Ordered(0, "ClinicReport", "echo-tte");
        var token = await AppToken("clinicreport", SsoTests.Secret);
        await Json(await Post(token, Report(order, "final", "Normal biventricular size and function. LVEF 60%.")));

        var orders = await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders"));
        Assert.Equal("Completed", (string)orders[0]!["status"]!);
        Assert.Contains("LVEF 60%", (string)orders[0]!["result"]!["summary"]!);

        var inbox = (await Json(await dr.GetAsync("/api/results"))).AsArray();
        var row = Assert.Single(inbox, r => (int)r!["orderId"]! == order)!;
        Assert.Equal("https://report.test/reports/" + order, (string)row["externalUrl"]!);
        (await dr.PostAsync($"/api/results/{(int)row["id"]!}/review", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await Json(await dr.GetAsync("/api/results"))).AsArray(), r => (int)r!["orderId"]! == order);

        // An addendum: the amended report needs acknowledging again.
        await Json(await Post(token, Report(order, "amended", "Normal biventricular function. Addendum: trace MR.")));
        var again = Assert.Single((await Json(await dr.GetAsync("/api/results"))).AsArray(), r => (int)r!["orderId"]! == order)!;
        Assert.Equal("Amended", (string)again["status"]!);
    }

    [Fact]
    public async Task Lab_results_come_with_their_values_and_flags()
    {
        var (dr, _, order) = await Ordered(1, "LabFlow", "CBC");
        var token = await AppToken("labflow", "test-labflow-secret");
        await Json(await Post(token, Report(order, "final", null,
            Observation("Hemoglobin", 112, "g/L", "L", "135 - 175"), Observation("White blood cell count", 6.2m, "10*9/L", "N", "4.0 - 11.0"))));
        var row = Assert.Single((await Json(await dr.GetAsync("/api/results"))).AsArray(), r => (int)r!["orderId"]! == order)!;
        Assert.True((bool)row["isAbnormal"]!);
        Assert.Contains("Hemoglobin: 112 g/L (L) [135 - 175]", (string)row["summary"]!);
        Assert.Contains("White blood cell count: 6.2 10*9/L [4.0 - 11.0]", (string)row["summary"]!);
    }

    [Fact]
    public async Task Only_the_application_the_order_went_to_may_answer_it()
    {
        var (_, _, order) = await Ordered(2, "ClinicReport", "ecg-12-lead");
        var lab = await AppToken("labflow", "test-labflow-secret");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(lab, Report(order, "final", "Sinus rhythm"))).StatusCode);

        var anonymous = await factory.CreateClient().PostAsync("/fhir", new StringContent("{}", Encoding.UTF8, "application/fhir+json"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var wrongSecret = await factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "clinicreport", ["client_secret"] = "nope", ["scope"] = "results",
        }));
        Assert.False(wrongSecret.IsSuccessStatusCode);

        var report = await AppToken("clinicreport", SsoTests.Secret);
        var unknown = await Post(report, Report(999999, "final", "x"));
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_results_scope_is_refused()
    {
        var (_, _, order) = await Ordered(3, "ClinicReport", "echo-tte");
        var token = await AppToken("clinicreport", SsoTests.Secret, scope: "");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(token, Report(order, "final", "x"))).StatusCode);
    }
}
