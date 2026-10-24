using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>The allergy list and what is checked against it when prescribing, and renewing a prescription.</summary>
public class SafetyAndRenewalTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Amoxicillin = "99000011", Cephalexin = "99000021", Ibuprofen = "99000171", Warfarin = "99000141",
        Atorvastatin = "99000071", Rosuvastatin = "99000081", Metformin = "99000061";

    private OutboxDispatcher Dispatcher => factory.Services.GetRequiredService<OutboxDispatcher>();

    private async Task<(HttpClient Dr, int Encounter, int Patient)> Visit(int patient)
    {
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null).OrderBy(p => p.Id).Skip(patient).FirstAsync());
        await using var db = new TestDatabaseReader(factory).Context();
        var appointment = await db.Encounters.Where(e => e.Id == id).Select(e => e.Appointment!).SingleAsync();
        db.Attach(appointment);
        appointment.Status = AppointmentStatus.Completed;
        appointment.RoomId = null;
        await db.SaveChangesAsync();
        var patientId = await db.Encounters.Where(e => e.Id == id).Select(e => e.PatientId).SingleAsync();
        return (await factory.SignInAsync("dr.patel"), id, patientId);
    }

    private static object Line(string din, string directions = "1 tablet twice daily for 10 days", int quantity = 20, int? days = 10, int repeats = 0) =>
        new { din, directions, quantity, daysSupply = days, repeats, noSubstitution = false, note = (string?)null };

    private static object Write(object[] lines, object[]? overrides = null) => new { items = lines, overrides };

    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static async Task Allergy(HttpClient dr, int patient, string key, string severity = "Severe", string? reaction = "Hives") =>
        await Json(await dr.PostAsJsonAsync($"/api/patients/{patient}/allergies", new { allergenKey = key, severity, reaction }));

    [Fact]
    public async Task A_recorded_allergy_stops_the_drug_until_the_prescriber_gives_a_reason()
    {
        var (dr, encounter, patient) = await Visit(10);
        await Allergy(dr, patient, "penicillin");

        var stopped = await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Amoxicillin)]));
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        var alert = Assert.Single((await Body(stopped))["alerts"]!.AsArray())!;
        Assert.Equal("Allergy", (string)alert["kind"]!);
        Assert.Equal("Severe", (string)alert["severity"]!);
        var key = (string)alert["key"]!;

        // A severe alert needs a reason of some substance.
        var short_ = await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Amoxicillin)], [new { key, reason = "ok" }]));
        Assert.Equal(HttpStatusCode.Conflict, short_.StatusCode);
        var sent = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions",
            Write([Line(Amoxicillin)], [new { key, reason = "Tolerated it last year under supervision" }])));
        Assert.Single(sent.AsArray());

        await using var db = new TestDatabaseReader(factory).Context();
        var note = await db.Prescriptions.Where(p => p.EncounterId == encounter).Select(p => p.AlertNote).SingleAsync();
        Assert.Contains("Tolerated it last year", note);
    }

    [Fact]
    public async Task A_penicillin_allergy_reaches_cephalosporins_as_a_moderate_alert_that_is_only_acknowledged()
    {
        var (dr, encounter, patient) = await Visit(11);
        await Allergy(dr, patient, "penicillin", "Moderate");

        var checkedBefore = await Body(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions/check", Write([Line(Cephalexin)])));
        var alert = Assert.Single(checkedBefore["alerts"]!.AsArray())!;
        Assert.Equal("Moderate", (string)alert["severity"]!);
        Assert.True((bool)checkedBefore["allergiesRecorded"]!);

        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Cephalexin)]))).StatusCode);
        Assert.Single((await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions",
            Write([Line(Cephalexin)], [new { key = (string)alert["key"]!, reason = (string?)null }])))).AsArray());
    }

    [Fact]
    public async Task A_new_drug_is_checked_against_what_the_patient_already_takes()
    {
        var (dr, encounter, _) = await Visit(12);
        Assert.Single((await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Warfarin, days: 30, quantity: 30)])))).AsArray());

        var stopped = await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Ibuprofen)]));
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        var alert = Assert.Single((await Body(stopped))["alerts"]!.AsArray())!;
        Assert.Equal("Interaction", (string)alert["kind"]!);
        Assert.Equal("Major", (string)alert["severity"]!);
        Assert.Contains("warfarin", (string)alert["message"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_medicines_of_one_kind_are_a_duplicate()
    {
        var (dr, encounter, _) = await Visit(13);
        var stopped = await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Atorvastatin), Line(Rosuvastatin)]));
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains((await Body(stopped))["alerts"]!.AsArray(), a => (string)a!["kind"]! == "Duplicate");
    }

    [Fact]
    public async Task Drugs_without_an_alert_go_out_as_before_and_an_ended_allergy_no_longer_counts()
    {
        var (dr, encounter, patient) = await Visit(14);
        await Allergy(dr, patient, "penicillin");
        var list = await Json(await dr.GetAsync($"/api/patients/{patient}/allergies"));
        var id = list["items"]![0]!["id"]!.GetValue<int>();
        Assert.Equal(HttpStatusCode.BadRequest, (await dr.PostAsJsonAsync($"/api/allergies/{id}/end", new { reason = "" })).StatusCode);
        await Json(await dr.PostAsJsonAsync($"/api/allergies/{id}/end", new { reason = "Entered in error" }));
        var sent = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write([Line(Amoxicillin), Line(Metformin)])));
        Assert.Equal(2, sent.AsArray().Count);
    }

    [Fact]
    public async Task No_known_allergies_is_recorded_apart_from_nothing_recorded_and_an_allergy_replaces_it()
    {
        var (dr, _, patient) = await Visit(15);
        var before = await Json(await dr.GetAsync($"/api/patients/{patient}/allergies"));
        Assert.False((bool)before["recorded"]!);

        var none = await Json(await dr.PostAsync($"/api/patients/{patient}/allergies/none", null));
        Assert.True((bool)none["recorded"]!);
        Assert.True((bool)none["noKnownAllergies"]!);

        await Allergy(dr, patient, "nsaid");
        var after = await Json(await dr.GetAsync($"/api/patients/{patient}/allergies"));
        Assert.False((bool)after["noKnownAllergies"]!);
        Assert.Single(after["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsync($"/api/patients/{patient}/allergies/none", null)).StatusCode);
    }

    [Fact]
    public async Task A_delivered_prescription_is_renewed_at_a_later_visit_once()
    {
        var (dr, first, patient) = await Visit(16);
        factory.Fhir.FailWith = null;
        var written = await Json(await dr.PostAsJsonAsync($"/api/encounters/{first}/prescriptions", Write([Line(Metformin, "1 tablet daily", 30, 30, repeats: 2)])));
        var id = written[0]!["id"]!.GetValue<int>();

        // Not at the pharmacy yet: nothing to renew.
        var (_, second, _) = await Visit(16);
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsJsonAsync($"/api/prescriptions/{id}/renew", new { encounterId = second })).StatusCode);

        await Dispatcher.DispatchAsync();
        var meds = await Json(await dr.GetAsync($"/api/patients/{patient}/medications"));
        Assert.True((bool)meds["items"]!.AsArray().Single(m => m!["id"]!.GetValue<int>() == id)!["canRenew"]!);

        var before = factory.Fhir.Requests.Count;
        var renewed = await Json(await dr.PostAsJsonAsync($"/api/prescriptions/{id}/renew", new { encounterId = second, quantity = 60 }));
        var copy = Assert.Single(renewed.AsArray())!;
        Assert.Equal(60, copy["quantity"]!.GetValue<int>());
        Assert.Equal("1 tablet daily", (string)copy["directions"]!);

        await Dispatcher.DispatchAsync();
        var sent = factory.Fhir.Requests.Skip(before).Single(r => r.Url.Host == "pharmacy.test");
        var request = sent.Bundle["entry"]!.AsArray().Select(e => e!["resource"]!).Single(r => (string)r["resourceType"]! == "MedicationRequest");
        Assert.Equal(id.ToString(), (string)request["priorPrescription"]!["identifier"]!["value"]!);

        meds = await Json(await dr.GetAsync($"/api/patients/{patient}/medications"));
        var old = meds["items"]!.AsArray().Single(m => m!["id"]!.GetValue<int>() == id)!;
        Assert.False((bool)old["canRenew"]!);
        Assert.Equal(copy["id"]!.GetValue<int>(), old["renewedBy"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsJsonAsync($"/api/prescriptions/{id}/renew", new { encounterId = second })).StatusCode);
    }

    [Fact]
    public async Task A_renewal_is_checked_like_any_prescription()
    {
        var (dr, first, patient) = await Visit(17);
        factory.Fhir.FailWith = null;
        var id = (await Json(await dr.PostAsJsonAsync($"/api/encounters/{first}/prescriptions", Write([Line(Amoxicillin, "1 capsule three times daily", 21, 7)])))) [0]!["id"]!.GetValue<int>();
        await Dispatcher.DispatchAsync();
        await Allergy(dr, patient, "penicillin");
        var (_, second, _) = await Visit(17);

        var stopped = await dr.PostAsJsonAsync($"/api/prescriptions/{id}/renew", new { encounterId = second });
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains((await Body(stopped))["alerts"]!.AsArray(), a => (string)a!["kind"]! == "Allergy");
    }

    [Fact]
    public void The_knowledge_base_knows_every_drug_in_the_catalog()
    {
        foreach (var drug in ClinicHub.Api.Integrations.PrescriptionCatalog.All)
            Assert.NotEmpty(DrugSafety.TagsOf(drug.Din));
    }
}
