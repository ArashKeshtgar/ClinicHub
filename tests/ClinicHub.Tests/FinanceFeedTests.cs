using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

public class FinanceFeedTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>A visit signed by Dr. Patel, claimed, sent, and paid by a simulated remittance.</summary>
    private async Task<(int Encounter, string Accounting)> PaidVisit()
    {
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null && p.HealthCardExpiry > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30))
            .OrderByDescending(p => p.Id).Skip(40).FirstAsync());
        var dr = await factory.SignInAsync("dr.patel");
        (await dr.PutAsJsonAsync($"/api/encounters/{id}/note", new { assessment = "Viral URI.", plan = "Supportive care." })).EnsureSuccessStatusCode();
        (await dr.PutAsJsonAsync($"/api/encounters/{id}/coding", new
        {
            diagnoses = new[] { new { code = "465", isPrimary = true } }, services = new[] { new { feeCode = "A007A", units = 1 } },
        })).EnsureSuccessStatusCode();
        (await dr.PostAsync($"/api/encounters/{id}/sign", null)).EnsureSuccessStatusCode();
        var billing = await factory.SignInAsync("billing");
        foreach (var b in (await Json(await billing.PostAsync("/api/billing/batches", null))).AsArray())
            (await billing.PostAsync($"/api/billing/batches/{(int)b!["id"]!}/submit", null)).EnsureSuccessStatusCode();
        (await billing.PostAsync("/api/billing/remittances/simulate", null)).EnsureSuccessStatusCode();
        return (id, id.ToString("D8"));
    }

    private async Task<(ClinicHubDbContext Db, Clinic Clinic)> Harbourview()
    {
        using var scope = factory.Services.CreateScope();
        var clinic = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Clinics.AsNoTracking().SingleAsync(c => c.Slug == "harbourview");
        return (factory.Services.GetRequiredService<ClinicDatabases>().Open(clinic), clinic);
    }

    [Fact]
    public async Task The_clinics_money_becomes_documents_without_health_information()
    {
        var (encounter, accounting) = await PaidVisit();
        var (db, clinic) = await Harbourview();
        await using var _ = db;
        var docs = await FinanceFeed.BuildAsync(db, clinic);

        var claimId = (await db.Claims.SingleAsync(c => c.EncounterId == encounter)).Id;
        var claim = docs.Single(d => (string)d["externalId"]! == $"claim:{claimId}:raised");
        Assert.Equal(("Invoice", "OHIP", accounting), ((string)claim["kind"]!, (string)claim["party"]!["type"]!, (string)claim["reference"]!));
        Assert.Equal("OHIP.Professional", (string)claim["lines"]![0]!["category"]!);
        Assert.Equal(37.95m, (decimal)claim["lines"]![0]!["amount"]!);
        Assert.Equal("123456", (string)claim["lines"]![0]!["providerCode"]!);

        var receipt = docs.Single(d => (string)d["kind"]! == "Receipt" && d["lines"]!.AsArray().Any(l => (string?)l!["appliesTo"] == accounting));
        Assert.Equal("EFT", (string)receipt["method"]!);
        var share = docs.Single(d => (string)d["externalId"]! == $"{receipt["externalId"]}:provider:123456");
        var paidToPatel = receipt["lines"]!.AsArray().Where(l => (string?)l!["providerCode"] == "123456").Sum(l => (decimal)l!["amount"]!);
        Assert.Equal(paidToPatel - Math.Round(paidToPatel * 0.30m, 2, MidpointRounding.AwayFromZero), (decimal)share["lines"]![0]!["amount"]!);

        // Nothing about the patient: no name, no health card.
        var patient = await db.Claims.Where(c => c.EncounterId == encounter).Select(c => c.Patient).SingleAsync();
        var all = new JsonArray(docs.Select(d => (JsonNode)d.DeepClone()).ToArray()).ToJsonString();
        Assert.DoesNotContain(patient.LastName, all);
        Assert.DoesNotContain(patient.HealthCardNumber!, all);
    }

    [Fact]
    public async Task Each_document_is_sent_once()
    {
        await PaidVisit();
        var (db, clinic) = await Harbourview();
        await using var _ = db;
        var finance = new RecordingFinance();
        var feed = new FinanceFeed(new OneClient(finance), new Options(new IntegrationOptions { Finance = { BaseUrl = "https://finance.test" } }),
            TimeProvider.System, NullLogger<FinanceFeed>.Instance);
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ServiceTokens>();

        var first = await feed.SendAsync(db, clinic, tokens);
        Assert.True(first > 0);
        Assert.Equal("https://finance.test/api/integration/documents", finance.Urls[0]);
        Assert.Equal(first, finance.Documents.Count);
        Assert.Equal(0, await feed.SendAsync(db, clinic, tokens));

        finance.Fail = true;   // a later document waits while the accounting is down, and goes once it is back
        await db.Database.ExecuteSqlRawAsync("DELETE TOP (1) FROM billing.FinanceExport");
        Assert.Equal(0, await feed.SendAsync(db, clinic, tokens));
        finance.Fail = false;
        Assert.Equal(1, await feed.SendAsync(db, clinic, tokens));
    }

    private sealed class RecordingFinance : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<JsonNode> Documents { get; } = [];
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Urls.Add(request.RequestUri!.ToString());
            Assert.StartsWith("Bearer ", request.Headers.Authorization!.ToString());
            Documents.AddRange(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsArray().Select(d => d!));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Options(IntegrationOptions value) : IOptionsMonitor<IntegrationOptions>
    {
        public IntegrationOptions CurrentValue => value;
        public IntegrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<IntegrationOptions, string?> listener) => null;
    }
}
