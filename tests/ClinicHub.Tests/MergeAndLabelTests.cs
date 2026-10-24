using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>
/// IntegrationHub's flows 6 and 7: a merge is told to the applications that had something for the merged record, and a
/// consent directive travels as a confidentiality label on the order.
/// </summary>
public class MergeAndLabelTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private OutboxDispatcher Dispatcher => factory.Services.GetRequiredService<OutboxDispatcher>();

    private static object Patient(string first, string last, string dob, string? card = null) => new
    {
        patient = new
        {
            firstName = first, lastName = last, dateOfBirth = dob, sex = "F", healthCardNumber = card,
            preferredLanguage = "en", isDeceased = false,
        },
        confirmNew = true,
    };

    private async Task<int> Register(string first, string last, string dob, string? card = null)
    {
        var desk = await factory.SignInAsync("frontdesk");
        return (await Json(await desk.PostAsJsonAsync("/api/patients", Patient(first, last, dob, card))))["id"]!.GetValue<int>();
    }

    /// <summary>A delivered LabFlow order for the patient; returns what LabFlow was sent.</summary>
    private async Task<JsonNode> DeliveredOrder(int patientId, string code = "CBC")
    {
        var encounter = await RoomedVisitForDrPatel(factory, db => db.Patients.SingleAsync(p => p.Id == patientId));
        var dr = await factory.SignInAsync("dr.patel");
        factory.Fhir.FailWith = null;
        await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/orders",
            new { items = new[] { new { target = "LabFlow", code } }, priority = "Routine" }));
        var before = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        return factory.Fhir.Requests.Skip(before).Single(r => r.Url.Host == "lab.test").Bundle;
    }

    private static JsonNode? Label(JsonNode bundle) =>
        bundle["entry"]!.AsArray().Select(e => e!["resource"]!).First(r => (string)r["resourceType"]! == "ServiceRequest")["meta"]?["security"]?[0];

    [Fact]
    public async Task A_lockbox_on_the_whole_record_goes_with_the_order_as_very_restricted()
    {
        var patient = await Register("Lumen", "Blackwood", "1979-03-14");
        var privacy = await factory.SignInAsync("privacy");
        await Json(await privacy.PostAsJsonAsync($"/api/patients/{patient}/consent", new { scope = "EntireRecord", note = "Test" }));

        var label = Label(await DeliveredOrder(patient));
        Assert.Equal(ConfidentialityLabels.System, (string)label!["system"]!);
        Assert.Equal("V", (string)label["code"]!);
    }

    [Fact]
    public async Task An_order_without_a_directive_has_no_label()
    {
        var patient = await Register("Orrin", "Pellbrook", "1985-06-02");
        Assert.Null(Label(await DeliveredOrder(patient)));
    }

    [Fact]
    public async Task One_visit_or_one_provider_withheld_is_restricted()
    {
        var patient = await Register("Tamsin", "Greyfield", "1990-11-23");
        await using var db = new TestDatabaseReader(factory).Context();
        var someone = await db.Users.Select(u => u.Id).FirstAsync();
        var visits = await db.Encounters.OrderBy(e => e.Id).Select(e => e.Id).Take(2).ToListAsync();
        var (withheld, other) = (visits[0], visits[1]);
        var provider = await db.Providers.Select(p => p.Id).FirstAsync();
        db.ConsentDirectives.Add(new ConsentDirective { PatientId = patient, Scope = ConsentScopes.Encounter, EncounterId = withheld, RecordedByUserId = someone, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Null(await ConfidentialityLabels.ForAsync(db, patient, other));             // another visit: not covered
        Assert.Equal("R", await ConfidentialityLabels.ForAsync(db, patient, withheld));
        db.ConsentDirectives.Add(new ConsentDirective { PatientId = patient, Scope = ConsentScopes.Provider, ExcludedProviderId = provider, RecordedByUserId = someone, CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal("R", await ConfidentialityLabels.ForAsync(db, patient, other));
        await db.ConsentDirectives.Where(d => d.PatientId == patient).ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedUtc, DateTime.UtcNow));
        Assert.Null(await ConfidentialityLabels.ForAsync(db, patient, withheld));         // revoked: no label
    }

    [Fact]
    public async Task A_merge_is_sent_to_the_application_that_had_an_order_for_the_merged_record()
    {
        var keep = await Register("Isolde", "Fernhart", "1972-02-29");
        var copy = await Register("Isolde", "Fernhart", "1972-02-29");
        await DeliveredOrder(copy);
        await using (var before = new TestDatabaseReader(factory).Context())
        {
            var mrns = await before.Patients.Where(p => p.Id == keep || p.Id == copy).ToDictionaryAsync(p => p.Id, p => p.Mrn);
            var privacy = await factory.SignInAsync("privacy");
            await Json(await privacy.PostAsJsonAsync("/api/patients/merge", new { survivorId = keep, mergedId = copy }));

            var sentBefore = factory.Fhir.Requests.Count;
            Assert.True(await Dispatcher.DispatchAsync() >= 1);
            var merge = factory.Fhir.Requests.Skip(sentBefore).Single(r => r.Url.Host == "lab.test");
            var entry = merge.Bundle["entry"]![0]!;
            var patient = entry["resource"]!;
            Assert.Equal("Patient", (string)patient["resourceType"]!);
            Assert.False((bool)patient["active"]!);
            Assert.Equal(mrns[copy], (string)patient["identifier"]![0]!["value"]!);
            Assert.Equal("replaced-by", (string)patient["link"]![0]!["type"]!);
            Assert.Equal(mrns[keep], (string)patient["link"]![0]!["other"]!["identifier"]!["value"]!);
            Assert.Equal("PUT", (string)entry["request"]!["method"]!);
            Assert.Equal($"Patient?identifier=urn:clinichub:harbourview:mrn|{mrns[copy]}", (string)entry["request"]!["url"]!);
            Assert.Null(patient["name"]);                                       // nothing else about the person
            Assert.Null(patient["birthDate"]);
        }

        await using var db = new TestDatabaseReader(factory).Context();
        Assert.False(await db.ServiceRequests.AnyAsync(s => s.PatientId == copy));
        Assert.Contains(await db.Outbox.Where(m => m.Kind == OutboxKinds.PatientMerge).ToListAsync(), m => m.SentUtc != null && m.Destination == "LabFlow");
        // Delivered once: the next pass sends nothing more for it.
        var count = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        Assert.DoesNotContain(factory.Fhir.Requests.Skip(count), r => (string?)r.Bundle["entry"]?[0]?["resource"]?["resourceType"] == "Patient"
            && r.Bundle["entry"]!.AsArray().Count == 1);
    }

    [Fact]
    public async Task A_merge_of_records_no_application_had_is_sent_nowhere()
    {
        var keep = await Register("Bram", "Holloway", "1966-08-08");
        var copy = await Register("Bram", "Holloway", "1966-08-08");
        var privacy = await factory.SignInAsync("privacy");
        await Json(await privacy.PostAsJsonAsync("/api/patients/merge", new { survivorId = keep, mergedId = copy }));
        await using var db = new TestDatabaseReader(factory).Context();
        var mrn = await db.Patients.Where(p => p.Id == copy).Select(p => p.Mrn).SingleAsync();
        Assert.DoesNotContain(await db.Outbox.Where(m => m.Kind == OutboxKinds.PatientMerge).Select(m => m.PayloadJson).ToListAsync(), p => p.Contains(mrn));
    }

    [Fact]
    public async Task A_merge_can_be_undone_until_the_applications_were_told()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var keep = await Register("Wilhelmina", "Oakhaven", "1958-04-17");
        var copy = (await Json(await desk.PostAsJsonAsync("/api/patients", new
        {
            patient = new { firstName = "Wilhelmina", lastName = "Oakhaven", dateOfBirth = "1958-04-17", sex = "F", phone = "(905) 555-0142", preferredLanguage = "en", isDeceased = false },
            confirmNew = true,
        })))["id"]!.GetValue<int>();
        await Json(await desk.PostAsJsonAsync("/api/frontdesk/walk-ins", new { patientId = copy, reason = "Cough" }));
        var privacy = await factory.SignInAsync("privacy");
        var merged = await Json(await privacy.PostAsJsonAsync("/api/patients/merge", new { survivorId = keep, mergedId = copy }));
        Assert.Equal("(905) 555-0142", (string)merged["fields"]!["phone"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsync($"/api/patients/{copy}/unmerge", null)).StatusCode);
        var back = await Json(await privacy.PostAsync($"/api/patients/{copy}/unmerge", null));
        Assert.Null(back["mergedInto"]);

        await using var db = new TestDatabaseReader(factory).Context();
        Assert.True(await db.Appointments.AnyAsync(a => a.PatientId == copy && a.Reason == "Cough"));
        Assert.False(await db.Appointments.AnyAsync(a => a.PatientId == keep && a.Reason == "Cough"));
        Assert.Null(await db.Patients.Where(p => p.Id == copy).Select(p => p.MergedIntoPatientId).SingleAsync());
        Assert.Null(await db.Patients.Where(p => p.Id == keep).Select(p => p.Phone).SingleAsync());   // what the merge filled in is emptied again
        Assert.Equal("(905) 555-0142", await db.Patients.Where(p => p.Id == copy).Select(p => p.Phone).SingleAsync());
        var mrn = await db.Patients.Where(p => p.Id == copy).Select(p => p.Mrn).SingleAsync();
        Assert.DoesNotContain(await db.Outbox.Where(m => m.Kind == OutboxKinds.PatientMerge).Select(m => m.PayloadJson).ToListAsync(), p => p.Contains(mrn));
        Assert.Equal(DuplicateStatus.Open, await db.DuplicateCandidates.Where(c => c.PatientAId == Math.Min(keep, copy) && c.PatientBId == Math.Max(keep, copy)).Select(c => c.Status).SingleAsync());

        // Not merged any more: nothing to undo twice.
        Assert.Equal(HttpStatusCode.Conflict, (await privacy.PostAsync($"/api/patients/{copy}/unmerge", null)).StatusCode);
        // And it can be merged again.
        await Json(await privacy.PostAsJsonAsync("/api/patients/merge", new { survivorId = keep, mergedId = copy }));
    }

    [Fact]
    public async Task A_merge_the_applications_were_told_about_is_not_undone()
    {
        var keep = await Register("Cornelius", "Abernathy", "1949-12-01");
        var copy = await Register("Cornelius", "Abernathy", "1949-12-01");
        await DeliveredOrder(copy);
        var privacy = await factory.SignInAsync("privacy");
        await Json(await privacy.PostAsJsonAsync("/api/patients/merge", new { survivorId = keep, mergedId = copy }));
        await Dispatcher.DispatchAsync();

        var res = await privacy.PostAsync($"/api/patients/{copy}/unmerge", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("already been told", await res.Content.ReadAsStringAsync());
        await using var db = new TestDatabaseReader(factory).Context();
        Assert.Equal(keep, await db.Patients.Where(p => p.Id == copy).Select(p => p.MergedIntoPatientId).SingleAsync());
    }
}
