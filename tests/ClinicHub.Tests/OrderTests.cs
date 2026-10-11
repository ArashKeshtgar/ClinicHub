using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

public class OrderTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private OutboxDispatcher Dispatcher => factory.Services.GetRequiredService<OutboxDispatcher>();

    private async Task<(HttpClient Dr, int Encounter)> Visit(int patient)
    {
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null).OrderBy(p => p.Id).Skip(patient).FirstAsync());
        return (await factory.SignInAsync("dr.patel"), id);
    }

    private static object Order(params (string Target, string Code)[] items) =>
        new { items = items.Select(i => new { target = i.Target, code = i.Code }), priority = "Routine", notes = "Fatigue, ?anemia" };

    /// <summary>Checks a token the way LabFlow and ClinicReport do: against ClinicHub's published keys.</summary>
    private async Task<TokenValidationResult> Validate(string token, string audience)
    {
        var jwks = await factory.CreateClient().GetStringAsync("/.well-known/jwks");
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost/", ValidAudience = audience, IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
        });
    }

    [Fact]
    public async Task Lab_tests_and_an_echo_go_to_their_applications_as_FHIR_bundles()
    {
        var (dr, encounter) = await Visit(0);
        factory.Fhir.FailWith = null;
        var placed = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders",
            Order(("LabFlow", "CBC"), ("LabFlow", "4548-4"), ("ClinicReport", "echo-tte"))));
        Assert.Equal(3, placed.AsArray().Count);
        Assert.All(placed.AsArray(), o => Assert.Equal("Draft", (string)o!["status"]!));

        var before = factory.Fhir.Requests.Count;
        Assert.True(await Dispatcher.DispatchAsync() >= 2);
        var sent = factory.Fhir.Requests.Skip(before).ToList();
        var lab = sent.Single(r => r.Url.Host == "lab.test");
        var report = sent.Single(r => r.Url.Host == "report.test");

        var entries = lab.Bundle["entry"]!.AsArray().Select(e => e!["resource"]!).ToList();
        Assert.Equal(["Patient", "Practitioner", "ServiceRequest", "ServiceRequest"], entries.Select(r => (string)r["resourceType"]!));
        Assert.Contains(entries[0]["identifier"]!.AsArray(), i => (string)i!["system"]! == FhirSystems.OntarioHealthCard);
        Assert.Contains(entries[1]["identifier"]!.AsArray(), i => (string)i!["system"]! == FhirSystems.OhipBillingNumber && (string)i["value"]! == "123456");
        var codes = entries.Skip(2).Select(r => r["code"]!["coding"]![0]!).Select(c => ((string)c!["system"]!, (string)c["code"]!)).ToList();
        Assert.Contains((FhirSystems.LabFlowTest, "CBC"), codes);
        Assert.Contains((FhirSystems.Loinc, "4548-4"), codes);
        Assert.Equal("Fatigue, ?anemia", (string)entries[2]["note"]![0]!["text"]!);
        Assert.Equal("echo-tte", (string)report.Bundle["entry"]![2]!["resource"]!["code"]!["coding"]![0]!["code"]!);

        // The token is ClinicHub's, for that application, naming the clinic.
        var check = await Validate(lab.Token, "labflow");
        Assert.True(check.IsValid, check.Exception?.Message);
        Assert.Equal("harbourview", check.Claims["clinic_slug"]);
        Assert.Equal("orders", check.Claims["scope"]);
        Assert.False((await Validate(lab.Token, "clinicreport")).IsValid);

        var after = await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders"));
        Assert.All(after.AsArray(), o => Assert.Equal("Accepted", (string)o!["status"]!));
        Assert.All(after.AsArray(), o => Assert.NotNull(o!["externalId"]));
    }

    [Fact]
    public async Task An_application_that_is_down_gets_the_order_later()
    {
        var (dr, encounter) = await Visit(1);
        await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "3016-3"))));
        factory.Fhir.FailWith = HttpStatusCode.ServiceUnavailable;
        await Dispatcher.DispatchAsync();
        var order = (await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders")))[0]!;
        Assert.Equal("Draft", (string)order["status"]!);
        Assert.Equal(1, (int)order["attempts"]!);
        Assert.Contains("down for maintenance", (string)order["lastError"]!);

        // Not retried before its pause is over.
        var count = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        Assert.Equal(count, factory.Fhir.Requests.Count);

        factory.Fhir.FailWith = null;
        (await dr.PostAsync($"/api/orders/{(int)order["id"]!}/resend", null)).EnsureSuccessStatusCode();
        await Dispatcher.DispatchAsync();
        Assert.Equal("Accepted", (string)(await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders")))[0]!["status"]!);
    }

    [Fact]
    public async Task Only_clinicians_order_and_only_from_the_catalog()
    {
        var (dr, encounter) = await Visit(2);
        Assert.Equal(HttpStatusCode.BadRequest, (await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "99999-9")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order())).StatusCode);

        var nurse = await factory.SignInAsync("nurse");
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "CBC")))).StatusCode);
        // The administrator may order but is no provider: an order needs a clinician who signs for it.
        var admin = await factory.SignInAsync("admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "CBC")))).StatusCode);

        var catalog = await Json(await dr.GetAsync("/api/orders/catalog"));
        Assert.All(catalog.AsArray(), i => Assert.True((bool)i!["connected"]!));
    }

    [Fact]
    public async Task The_order_and_its_message_are_saved_together()
    {
        var (dr, encounter) = await Visit(3);
        await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "CBC"), ("LabFlow", "2857-1"))));
        await using var db = new TestDatabaseReader(factory).Context();
        var orders = await db.ServiceRequests.Where(s => s.EncounterId == encounter).ToListAsync();
        Assert.Single(orders.Select(o => o.OutboxMessageId).Distinct());
        var message = await db.Outbox.SingleAsync(m => m.Id == orders[0].OutboxMessageId);
        Assert.Equal("LabFlow", message.Destination);
        Assert.Equal("transaction", (string)JsonNode.Parse(message.PayloadJson)!["type"]!);
        Assert.Contains(db.PatientAccessLog, l => l.Context == $"Ordered 2 from encounter {encounter}");
    }
    private async Task<JsonNode> Cancel(HttpClient dr, int order, string reason) =>
        await Json(await dr.PostAsJsonAsync($"/api/orders/{order}/cancel", new { reason }));

    private static JsonNode Find(JsonNode orders, string code) => orders.AsArray().Single(o => (string)o!["code"]! == code)!;

    [Fact]
    public async Task An_order_not_yet_sent_is_cancelled_here()
    {
        var (dr, encounter) = await Visit(4);
        var placed = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "CBC"), ("LabFlow", "2857-1"))));
        await using var db = new TestDatabaseReader(factory).Context();
        var messageId = (await db.ServiceRequests.FirstAsync(s => s.EncounterId == encounter)).OutboxMessageId;

        var after = await Cancel(dr, (int)Find(placed, "CBC")["id"]!, "Ordered in error");
        Assert.Equal("Cancelled", (string)Find(after, "CBC")["status"]!);
        Assert.Equal("Ordered in error", (string)Find(after, "CBC")["cancelReason"]!);
        // The message now carries only the other test.
        db.ChangeTracker.Clear();
        var payload = JsonNode.Parse((await db.Outbox.SingleAsync(m => m.Id == messageId)).PayloadJson)!;
        var requests = payload["entry"]!.AsArray().Select(e => e!["resource"]!).Where(r => (string)r["resourceType"]! == "ServiceRequest").ToList();
        Assert.Equal("2857-1", (string)Assert.Single(requests)["code"]!["coding"]![0]!["code"]!);

        // The last one cancelled: nothing is left to send.
        await Cancel(dr, (int)Find(placed, "2857-1")["id"]!, "Ordered in error");
        Assert.False(await db.Outbox.AnyAsync(m => m.Id == messageId));
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsJsonAsync($"/api/orders/{(int)Find(placed, "CBC")["id"]!}/cancel", new { reason = "" })).StatusCode);
    }

    [Fact]
    public async Task A_delivered_order_is_withdrawn_from_the_application()
    {
        var (dr, encounter) = await Visit(5);
        factory.Fhir.FailWith = null;
        var placed = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("ClinicReport", "echo-tte"))));
        await Dispatcher.DispatchAsync();
        var id = (int)placed[0]!["id"]!;

        var cancelling = await Cancel(dr, id, "Patient declined");
        Assert.Equal("Cancelling", (string)cancelling[0]!["status"]!);
        var before = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        var sent = factory.Fhir.Requests.Skip(before).Single();
        Assert.Equal("report.test", sent.Url.Host);
        var entry = Assert.Single(sent.Bundle["entry"]!.AsArray())!;
        Assert.Equal("PUT", (string)entry["request"]!["method"]!);
        Assert.Equal($"ServiceRequest?identifier=urn:clinichub:harbourview:service-request|{id}", (string)entry["request"]!["url"]!);
        Assert.Equal("revoked", (string)entry["resource"]!["status"]!);
        Assert.Equal("Patient declined", (string)entry["resource"]!["note"]![0]!["text"]!);

        var after = await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders"));
        Assert.Equal("Cancelled", (string)after[0]!["status"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsJsonAsync($"/api/orders/{id}/cancel", new { reason = "" })).StatusCode);
    }

    [Fact]
    public async Task The_application_may_refuse_and_the_order_carries_on()
    {
        var (dr, encounter) = await Visit(6);
        factory.Fhir.FailWith = null;
        var placed = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders", Order(("LabFlow", "CBC"))));
        await Dispatcher.DispatchAsync();
        var id = (int)placed[0]!["id"]!;

        await Cancel(dr, id, "Duplicate");
        factory.Fhir.FailWith = HttpStatusCode.Conflict;
        factory.Fhir.FailText = "Results have been entered for Complete blood count; call the lab.";
        try
        {
            Assert.Equal(1, await Dispatcher.DispatchAsync());
        }
        finally
        {
            factory.Fhir.FailWith = null;
            factory.Fhir.FailText = "down for maintenance";
        }
        var order = (await Json(await dr.GetAsync($"/api/encounters/{encounter}/orders")))[0]!;
        Assert.Equal("Accepted", (string)order["status"]!);
        Assert.Contains("did not cancel: Results have been entered", (string)order["statusNote"]!);
        Assert.Null(order["cancelReason"]);
        // Answered, not retried.
        var count = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        Assert.Equal(count, factory.Fhir.Requests.Count);
    }
}
