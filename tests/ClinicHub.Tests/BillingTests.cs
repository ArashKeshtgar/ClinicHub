using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

public class ClaimFileTests
{
    private static readonly DateOnly Day = new(2026, 10, 9);

    private static ClaimFile.Batch Batch(params ClaimFile.ClaimEntry[] claims) => new(Day, 4, "AB12", "123456", "00", claims);

    private static ClaimFile.ClaimEntry Ohip(string accounting = "00000042") => new(accounting, "HCP", "9876543225", "AB", new DateOnly(1980, 5, 17), null,
        [new("A007A", 37.95m, 1, Day, "034"), new("G538A", 4.50m, 1, Day, "034")], null);

    [Fact]
    public void Every_record_is_79_characters_with_counts_in_the_trailer()
    {
        var rmb = new ClaimFile.ClaimEntry("00000043", "RMB", "123456789", null, new DateOnly(1990, 1, 2), null,
            [new("A001A", 24.15m, 1, Day, "460")], ("Tremblay", "Jean-Luc", "M", "NS"));
        var lines = ClaimFile.Build(Batch(Ohip(), rmb)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, l => Assert.Equal(79, l.Length));
        Assert.Equal(["HEB", "HEH", "HET", "HET", "HEH", "HER", "HET", "HEE"], lines.Select(l => l[..3]));
        Assert.Equal("HEE000200010003", lines[^1][..15]);
    }

    [Fact]
    public void Fields_are_justified_and_money_is_in_cents()
    {
        var lines = ClaimFile.Build(Batch(Ohip())).Split("\r\n");
        Assert.StartsWith("HEBV03S202610090004000000AB1212345600", lines[0]);
        Assert.StartsWith("HEH9876543225AB1980051700000042HCPP", lines[1]);
        var item = FixedWidth.Read(lines[2], ClaimFile.Het);
        Assert.Equal(("A007A", "003795", "01", "20261009", "034"), (item["ServiceCode"], item["FeeSubmitted"], item["NumberOfServices"], item["ServiceDate"], item["DiagnosticCode"]));
        Assert.Equal("HJ123456.004", ClaimFile.FileName(Batch()));
    }

    [Fact]
    public void Reciprocal_claims_carry_the_patient_on_a_registration_record()
    {
        var rmb = new ClaimFile.ClaimEntry("00000043", "RMB", "123456789", null, new DateOnly(1990, 1, 2), null,
            [new("A001A", 24.15m, 1, Day, "460")], ("Tremblay", "Jean-Luc", "M", "NS"));
        var lines = ClaimFile.Build(Batch(rmb)).Split("\r\n");
        Assert.Equal("", FixedWidth.Read(lines[1], ClaimFile.Heh)["HealthNumber"]);
        var her = FixedWidth.Read(lines[2], ClaimFile.Her);
        Assert.Equal(("123456789", "TREMBLAY", "JEAN-", "1", "NS"), (her["RegistrationNumber"], her["LastName"], her["FirstName"], her["Sex"], her["Province"]));
    }

    [Fact]
    public void Remittance_round_trips_and_checks_its_total()
    {
        var advice = new RemittanceFile.Advice("AB12", null, Day, "Harbourview Medical Centre (demo)", "SIM00001",
        [
            new("26100900001", "123456", "00", "00000042", "9876543225", "AB", "HCP",
                [new(Day, 1, "A007A", 37.95m, 37.95m, null), new(Day, 1, "G538A", 5.00m, 4.50m, "AD")]),
        ], ["SIMULATED"]);
        var text = RemittanceFile.Build(advice);
        Assert.All(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), l => Assert.Equal(79, l.Length));

        var back = RemittanceFile.Parse(text);
        Assert.Equal(42.45m, back.TotalPaid);
        Assert.Equal("AD", back.Claims[0].Items[1].ExplanatoryCode);
        Assert.Equal(["SIMULATED"], back.Messages);
        Assert.Equal("PJAB1209.RA", RemittanceFile.FileName(advice));

        var tampered = text.Replace("000004245", "000009999");
        Assert.Contains("does not match", Assert.Throws<RemittanceFile.FormatException>(() => RemittanceFile.Parse(tampered)).Message);
        var broken = text.Replace("2026100901A007A", "2026XX0901A007A");
        Assert.StartsWith("Line 3:", Assert.Throws<RemittanceFile.FormatException>(() => RemittanceFile.Parse(broken)).Message);
        Assert.Throws<RemittanceFile.FormatException>(() => RemittanceFile.Parse("hello"));
    }
}

public class OhipSimulatorTests
{
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly Dictionary<string, decimal> Fees = new() { ["A007A"] = 37.95m, ["A001A"] = 24.15m };
    private static readonly OhipSimulator.PatientCard Card = new("9876543225", "AB", new DateOnly(2029, 1, 1));

    private static OhipSimulator.ItemOut One(OhipSimulator.ClaimIn claim, DateOnly? received = null, HashSet<(int, string, DateOnly)>? paid = null) =>
        OhipSimulator.Adjudicate([claim], Fees, paid ?? [], received ?? Day)[(claim.AccountingNumber, 0)];

    private static OhipSimulator.ClaimIn Claim(string code = "A007A", decimal fee = 37.95m, string version = "AB", OhipSimulator.PatientCard? card = null,
        string program = "HCP", string? number = "9876543225", DateOnly? date = null) =>
        new("00000001", 7, program, number, version, card ?? Card, [new(code, date ?? Day, 1, fee)]);

    [Fact]
    public void Pays_in_full_or_caps_at_the_schedule_fee()
    {
        Assert.Equal(new OhipSimulator.ItemOut(37.95m, null), One(Claim()));
        Assert.Equal(new OhipSimulator.ItemOut(37.95m, "AD"), One(Claim(fee: 45m)));
    }

    [Fact]
    public void Refuses_with_an_explanatory_code()
    {
        Assert.Equal("VC", One(Claim(version: "ZZ")).ExplanatoryCode);
        Assert.Equal("EX", One(Claim(card: Card with { Expiry = Day.AddDays(-1) })).ExplanatoryCode);
        Assert.Equal("EX", One(Claim(number: "9876543210")).ExplanatoryCode);
        Assert.Equal("ST", One(Claim(), received: Day.AddMonths(7)).ExplanatoryCode);
        Assert.Equal("SC", One(Claim(code: "Z999A")).ExplanatoryCode);
        Assert.Equal("RN", One(Claim(program: "RMB", number: null)).ExplanatoryCode);
        Assert.Equal("DU", One(Claim(), paid: [(7, "A007A", Day)]).ExplanatoryCode);
        Assert.Equal(0m, One(Claim(version: "ZZ")).Paid);
    }

    [Fact]
    public void A_service_claimed_twice_in_one_run_is_paid_once()
    {
        var a = Claim() with { AccountingNumber = "00000001" };
        var b = Claim() with { AccountingNumber = "00000002" };
        var outcome = OhipSimulator.Adjudicate([a, b], Fees, new HashSet<(int, string, DateOnly)>(), Day);
        Assert.Null(outcome[("00000001", 0)].ExplanatoryCode);
        Assert.Equal(new OhipSimulator.ItemOut(0m, "DU"), outcome[("00000002", 0)]);
    }

    [Fact]
    public void Reciprocal_claims_skip_ontario_card_checks()
    {
        var rmb = Claim(program: "RMB", number: "123456789", version: "", card: new(null, null, null));
        Assert.Equal(new OhipSimulator.ItemOut(37.95m, null), One(rmb));
    }
}

public class BillingFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>A different valid-card patient per test, so the simulator's duplicate rule never links two tests.</summary>
    private static Func<ClinicHubDbContext, Task<Patient>> Patient(int n) => db =>
        db.Patients.Where(p => p.HealthCardNumber != null && p.HealthCardExpiry > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30))
            .OrderByDescending(p => p.Id).Skip(n).FirstAsync();

    private async Task<int> SignedVisit(Func<ClinicHubDbContext, Task<Patient>> patient, string feeCode = "A007A")
    {
        var id = await RoomedVisitForDrPatel(factory, patient);
        var dr = await factory.SignInAsync("dr.patel");
        (await dr.PutAsJsonAsync($"/api/encounters/{id}/note", new { assessment = "Viral URI.", plan = "Supportive care." })).EnsureSuccessStatusCode();
        (await dr.PutAsJsonAsync($"/api/encounters/{id}/coding", new
        {
            diagnoses = new[] { new { code = "465", isPrimary = true } },
            services = new[] { new { feeCode, units = 1 } },
        })).EnsureSuccessStatusCode();
        (await dr.PostAsync($"/api/encounters/{id}/sign", null)).EnsureSuccessStatusCode();
        return id;
    }

    private async Task<JsonNode> ClaimFor(HttpClient billing, int encounterId)
    {
        var claims = await Json(await billing.GetAsync("/api/billing/claims"));
        return claims.AsArray().Single(c => (int)c!["encounterId"]! == encounterId)!;
    }

    [Fact]
    public async Task Signed_visit_is_batched_submitted_paid_and_reconciled()
    {
        var encounter = await SignedVisit(Patient(0));
        var billing = await factory.SignInAsync("billing");
        Assert.Equal("Ready", (string)(await ClaimFor(billing, encounter))["status"]!);

        var batches = (await Json(await billing.PostAsync("/api/billing/batches", null))).AsArray();
        var accounting = encounter.ToString("D8");
        var batchId = (int)batches.Single(b => (string)b!["provider"]! == "Dr. Nadia Patel")!["id"]!;
        var file = await billing.GetStringAsync($"/api/billing/batches/{batchId}/file");
        Assert.Contains(accounting + "HCPP", file);
        Assert.Equal(HttpStatusCode.Conflict, (await billing.PostAsync("/api/billing/batches", null)).StatusCode);

        foreach (var b in batches)
            (await billing.PostAsync($"/api/billing/batches/{(int)b!["id"]!}/submit", null)).EnsureSuccessStatusCode();
        Assert.Equal("Submitted", (string)(await ClaimFor(billing, encounter))["status"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await billing.PostAsync($"/api/billing/batches/{batchId}/submit", null)).StatusCode);

        var ra = await Json(await billing.PostAsync("/api/billing/remittances/simulate", null));
        var patel = ra["providers"]!.AsArray().Single(p => (string)p!["provider"]! == "Dr. Nadia Patel")!;
        Assert.Equal(Math.Round((decimal)patel["paid"]! * 0.30m, 2, MidpointRounding.AwayFromZero), (decimal)patel["overhead"]!);
        Assert.Contains("NOT ISSUED BY THE MINISTRY OF HEALTH. NO PAYMENT WAS MADE.", ra["messages"]!.AsArray().Select(m => (string)m!));

        var claim = await ClaimFor(billing, encounter);
        Assert.Equal("Paid", (string)claim["status"]!);
        Assert.Equal(37.95m, (decimal)claim["amountPaid"]!);
        var batchList = (await Json(await billing.GetAsync("/api/billing/batches"))).AsArray();
        Assert.Equal("Accepted", (string)batchList.Single(b => (int)b!["id"]! == batchId)!["status"]!);

        // The same file can't be applied twice.
        var raId = (int)ra["remittance"]!["id"]!;
        var name = (string)ra["remittance"]!["fileName"]!;
        var content = await billing.GetStringAsync($"/api/billing/remittances/{raId}/file");
        Assert.Equal(HttpStatusCode.Conflict, (await billing.PostAsJsonAsync("/api/billing/remittances", new { fileName = name, content })).StatusCode);
    }

    [Fact]
    public async Task Refused_claim_is_explained_and_can_be_resubmitted()
    {
        var encounter = await SignedVisit(Patient(1));
        int patientId;
        string version;
        await using (var db = new TestDatabaseReader(factory).Context())
        {
            // The patient got a new card after the visit was billed: the claim's version no longer matches.
            var p = await db.Encounters.Where(e => e.Id == encounter).Select(e => e.Patient).SingleAsync();
            patientId = p.Id;
            version = p.HealthCardVersion == "QQ" ? "QR" : "QQ";
            p.HealthCardVersion = version;
            await db.SaveChangesAsync();
        }
        var billing = await factory.SignInAsync("billing");
        foreach (var b in (await Json(await billing.PostAsync("/api/billing/batches", null))).AsArray())
            (await billing.PostAsync($"/api/billing/batches/{(int)b!["id"]!}/submit", null)).EnsureSuccessStatusCode();
        var ra = await Json(await billing.PostAsync("/api/billing/remittances/simulate", null));

        var claim = await ClaimFor(billing, encounter);
        Assert.Equal("Rejected", (string)claim["status"]!);
        Assert.Contains(ra["exceptions"]!.AsArray(), x => (int?)x!["claimId"] == (int)claim["id"]! && (string)x["explanatoryCode"]! == "VC");
        var detail = await Json(await billing.GetAsync($"/api/billing/claims/{(int)claim["id"]!}"));
        Assert.Contains("Version code", (string)detail["items"]![0]!["explanation"]!);

        (await billing.PostAsync($"/api/billing/claims/{(int)claim["id"]!}/resubmit", null)).EnsureSuccessStatusCode();
        await using (var db = new TestDatabaseReader(factory).Context())
        {
            var c = await db.Claims.Include(x => x.Items).SingleAsync(x => x.Id == (int)claim["id"]!);
            Assert.Equal((ClaimStatus.Ready, version, (decimal?)null), (c.Status, c.HealthCardVersion, c.Items[0].FeePaid));
            Assert.Contains(db.PatientAccessLog, l => l.PatientId == patientId && l.Context == $"Claim {c.AccountingNumber}");
        }
    }

    [Fact]
    public async Task Imports_a_received_file_and_reports_what_it_could_not_match()
    {
        var billing = await factory.SignInAsync("billing");
        var stranger = new RemittanceFile.Advice("AB12", null, new DateOnly(2026, 9, 15), "Somewhere else", "00012345",
            [new("26091500001", "999999", "00", "ZZ000001", "1234567897", "AA", "HCP", [new(new DateOnly(2026, 8, 1), 1, "A007A", 37.95m, 37.95m, null)])], []);
        var ra = await Json(await billing.PostAsJsonAsync("/api/billing/remittances", new { fileName = "PIAB1215.RA", content = RemittanceFile.Build(stranger) }));
        Assert.False((bool)ra["remittance"]!["simulated"]!);
        Assert.Equal(1, (int)ra["remittance"]!["unmatched"]!);
        Assert.Contains("No claim", (string)ra["exceptions"]![0]!["explanation"]!);

        var bad = await billing.PostAsJsonAsync("/api/billing/remittances", new { fileName = "x.RA", content = "not a remittance" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Only_billing_staff_send_claims()
    {
        var dr = await factory.SignInAsync("dr.patel");
        Assert.Equal(HttpStatusCode.OK, (await dr.GetAsync("/api/billing/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dr.PostAsync("/api/billing/batches", null)).StatusCode);
        var desk = await factory.SignInAsync("frontdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync("/api/billing/remittances/simulate", null)).StatusCode);
        var nurse = await factory.SignInAsync("nurse");
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/api/billing/claims")).StatusCode);
    }

    [Fact]
    public async Task Nurse_practitioner_visits_are_not_claimed()
    {
        await using var db = new TestDatabaseReader(factory).Context();
        var npSigned = await db.Encounters.Where(e => e.Status == EncounterStatus.Signed && e.Provider.OhipBillingNumber == null).Select(e => e.Id).ToListAsync();
        Assert.False(await db.Claims.AnyAsync(c => npSigned.Contains(c.EncounterId)));
    }
}
