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

    /// <summary>What IntegrationHub posts for an HL7 ORU^R01: a DiagnosticReport on the order's number, with LOINC-coded Observations.</summary>
    private static JsonObject HubReport(string slug, int order) => new()
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["entry"] = new JsonArray(
            new JsonObject
            {
                ["fullUrl"] = "urn:uuid:report",
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "DiagnosticReport", ["status"] = "final",
                    // As IntegrationHub writes it from the result's ORC-3/OBR-3 (OruToFhir).
                    ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:integrationhub:hl7:EXTLAB-LAB1:filler-order", ["value"] = $"F-{order}" }),
                    ["basedOn"] = new JsonArray(new JsonObject { ["identifier"] = new JsonObject { ["system"] = $"urn:clinichub:{slug}:service-request", ["value"] = order.ToString() } }),
                    ["result"] = new JsonArray(new JsonObject { ["reference"] = "urn:uuid:obs-0" }),
                },
            },
            new JsonObject
            {
                ["fullUrl"] = "urn:uuid:obs-0",
                ["resource"] = Observation("Hemoglobin A1c", 7.4m, "%", "H", "4.0 - 6.0"),
            }),
    };

    [Fact]
    public async Task An_HL7_result_from_IntegrationHub_answers_an_order_placed_to_the_external_lab()
    {
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null).OrderByDescending(p => p.Id).Skip(4).FirstAsync());
        var dr = await factory.SignInAsync("dr.patel");
        factory.Fhir.FailWith = null;
        var before = factory.Fhir.Requests.Count;
        var placed = await Json(await dr.PostAsJsonAsync($"/api/encounters/{id}/orders",
            new { items = new[] { new { target = "ExternalLab", code = "4548-4" } }, priority = "Routine" }));
        await factory.Services.GetRequiredService<OutboxDispatcher>().DispatchAsync();
        var order = (int)placed[0]!["id"]!;

        // Recorded, not sent: nothing went out, and the order says so.
        Assert.Equal(before, factory.Fhir.Requests.Count);
        Assert.Equal("AwaitingResult", (string)placed[0]!["status"]!);
        Assert.Contains("not sent the order", (string)placed[0]!["statusNote"]!);

        var token = await AppToken("integrationhub", SsoTests.HubSecret);
        await Json(await Post(token, HubReport("harbourview", order)));

        var orders = await Json(await dr.GetAsync($"/api/encounters/{id}/orders"));
        Assert.Equal("Completed", (string)orders[0]!["status"]!);
        Assert.Equal($"F-{order}", (string)orders[0]!["externalId"]!);   // the lab's own number, from the result
        var row = Assert.Single((await Json(await dr.GetAsync("/api/results"))).AsArray(), r => (int)r!["orderId"]! == order)!;
        Assert.Equal("ExternalLab", (string)row["target"]!);
        Assert.True((bool)row["isAbnormal"]!);
        Assert.Contains("Hemoglobin A1c: 7.4 % (H) [4.0 - 6.0]", (string)row["summary"]!);
    }

    [Fact]
    public async Task With_the_hub_route_set_an_external_lab_order_is_sent_through_the_outbox_and_then_answered()
    {
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<IntegrationOptions>>().CurrentValue;
        options.ExternalLab.FhirBaseUrl = "https://hub.test/route/externallab/fhir";
        try
        {
            var (dr, encounter, order) = await Ordered(7, "ExternalLab", "3016-3");
            var sent = Assert.Single(factory.Fhir.Requests, r => r.Url.Host == "hub.test");
            Assert.Equal("/route/externallab/fhir", sent.Url.AbsolutePath);
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(sent.Token);
            Assert.Contains("externallab", jwt.Audiences);
            Assert.Contains(jwt.Claims, c => c.Type == "scope" && c.Value == "orders");
            var request = sent.Bundle["entry"]!.AsArray().Select(e => e!["resource"]!).Single(r => (string)r["resourceType"]! == "ServiceRequest");
            Assert.Equal("3016-3", (string)request["code"]!["coding"]![0]!["code"]!);
            Assert.Equal($"urn:clinichub:harbourview:service-request", (string)request["identifier"]![0]!["system"]!);
            Assert.Equal(order.ToString(), (string)request["identifier"]![0]!["value"]!);

            var orders = await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders"));
            Assert.Equal("Accepted", (string)orders[0]!["status"]!);
            Assert.Null(orders[0]!["statusNote"]);

            // The lab's HL7 result, mapped by the hub, answers it.
            var token = await AppToken("integrationhub", SsoTests.HubSecret);
            await Json(await Post(token, HubReport("harbourview", order)));
            Assert.Equal("Completed", (string)(await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders")))[0]!["status"]!);
        }
        finally
        {
            options.ExternalLab.FhirBaseUrl = null;
        }
    }

    [Fact]
    public async Task IntegrationHub_answers_only_external_lab_orders_at_the_clinic_they_were_placed()
    {
        var (_, _, labflowOrder) = await Ordered(5, "LabFlow", "CBC");
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null).OrderByDescending(p => p.Id).Skip(6).FirstAsync());
        var dr = await factory.SignInAsync("dr.patel");
        var external = (int)(await Json(await dr.PostAsJsonAsync($"/api/encounters/{id}/orders",
            new { items = new[] { new { target = "ExternalLab", code = "3016-3" } }, priority = "Routine" })))[0]!["id"]!;

        var hub = await AppToken("integrationhub", SsoTests.HubSecret);
        // An order sent to LabFlow is not the external lab's to answer, and the other way round.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(hub, HubReport("harbourview", labflowOrder))).StatusCode);
        var lab = await AppToken("labflow", "test-labflow-secret");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(lab, HubReport("harbourview", external))).StatusCode);
        // The order number alone is not enough: it has to be that clinic's order.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(hub, HubReport("lakeshore", external))).StatusCode);
        Assert.Null((await Json(await dr.GetAsync($"/api/encounters/{id}/orders")))[0]!["result"]);
    }

    [Fact]
    public async Task A_token_without_the_results_scope_is_refused()
    {
        var (_, _, order) = await Ordered(3, "ClinicReport", "echo-tte");
        var token = await AppToken("clinicreport", SsoTests.Secret, scope: "");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(token, Report(order, "final", "x"))).StatusCode);
    }

    [Fact]
    public async Task An_application_gets_a_token_for_the_finance_application()
    {
        var token = await AppToken("labflow", "test-labflow-secret", scope: "finance");
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Contains("finance", jwt.Audiences);
        Assert.Equal("labflow", jwt.Claims.Single(c => c.Type == "client_id").Value);
        Assert.Contains(jwt.Claims, c => c.Type == "scope" && c.Value == "finance");
        // Results alone: no finance audience.
        var results = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(await AppToken("labflow", "test-labflow-secret"));
        Assert.DoesNotContain("finance", results.Audiences);
    }
}
