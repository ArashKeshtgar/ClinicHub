using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static ClinicHub.Tests.EncounterTests;

namespace ClinicHub.Tests;

/// <summary>Electronic prescriptions: written at a visit, delivered to the pharmacy (PharmaFlow) as FHIR MedicationRequests.</summary>
public class PrescriptionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Amoxicillin = "99000011", Ibuprofen = "99000171", Metformin = "99000061", Warfarin = "99000141";

    private OutboxDispatcher Dispatcher => factory.Services.GetRequiredService<OutboxDispatcher>();

    private async Task<(HttpClient Dr, int Encounter)> Visit(int patient)
    {
        var id = await RoomedVisitForDrPatel(factory, db => db.Patients.Where(p => p.HealthCardNumber != null).OrderBy(p => p.Id).Skip(patient).FirstAsync());
        // The visit stays; the exam room is freed, since this class sets up a dozen visits and the clinic has a few rooms.
        await using (var db = new TestDatabaseReader(factory).Context())
        {
            var appointment = await db.Encounters.Where(e => e.Id == id).Select(e => e.Appointment!).SingleAsync();
            db.Attach(appointment);
            appointment.Status = AppointmentStatus.Completed;
            appointment.RoomId = null;
            await db.SaveChangesAsync();
        }
        return (await factory.SignInAsync("dr.patel"), id);
    }

    private static object Line(string din, string directions = "1 tablet twice daily for 10 days", int quantity = 20, int? days = 10, int repeats = 0,
        bool noSub = false, string? note = null) =>
        new { din, directions, quantity, daysSupply = days, repeats, noSubstitution = noSub, note };

    private static object Write(params object[] lines) => new { items = lines };

    private async Task<TokenValidationResult> Validate(string token, string audience)
    {
        var jwks = await factory.CreateClient().GetStringAsync("/.well-known/jwks");
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost/", ValidAudience = audience, IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
        });
    }

    /// <summary>The body of any answer, also an error's (the shared Json helper insists on success).</summary>
    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private async Task<JsonArray> List(HttpClient client, int encounter) =>
        (await Json(await client.GetAsync($"/api/encounters/{encounter}/prescriptions"))).AsArray();

    /// <summary>Changes the signed-in physician's provider record for the length of a check, then puts it back.</summary>
    private async Task WithPatel(Action<Provider> change, Func<Task> check)
    {
        await using var db = new TestDatabaseReader(factory).Context();
        var patel = await db.Providers.SingleAsync(p => p.LastName == "Patel");
        var (kind, number) = (patel.Kind, patel.RegistrationNumber);
        change(patel);
        await db.SaveChangesAsync();
        try { await check(); }
        finally
        {
            patel.Kind = kind;
            patel.RegistrationNumber = number;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Prescriptions_go_to_the_pharmacy_as_one_FHIR_bundle_of_MedicationRequests()
    {
        var (dr, encounter) = await Visit(0);
        factory.Fhir.FailWith = null;
        var written = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(
            Line(Amoxicillin, "1 capsule three times daily for 7 days", 21, 7, repeats: 1, note: "Patient prefers capsules"),
            Line(Ibuprofen, "1 tablet with food as needed", 30, null, noSub: true))));
        Assert.Equal(2, written.AsArray().Count);
        Assert.All(written.AsArray(), p => Assert.Equal("Draft", (string)p!["status"]!));

        var before = factory.Fhir.Requests.Count;
        Assert.True(await Dispatcher.DispatchAsync() >= 1);
        var sent = factory.Fhir.Requests.Skip(before).Single(r => r.Url.Host == "pharmacy.test");

        var entries = sent.Bundle["entry"]!.AsArray().Select(e => e!["resource"]!).ToList();
        Assert.Equal(["Patient", "Practitioner", "MedicationRequest", "MedicationRequest"], entries.Select(r => (string)r["resourceType"]!));
        Assert.Contains(entries[0]["identifier"]!.AsArray(), i => (string)i!["system"]! == FhirSystems.Clinic("harbourview", "mrn"));
        Assert.Contains(entries[0]["identifier"]!.AsArray(), i => (string)i!["system"]! == FhirSystems.OntarioHealthCard);
        Assert.Contains(entries[1]["identifier"]!.AsArray(), i => (string)i!["system"]! == FhirSystems.Cpso);

        var amox = entries[3]; // newest first in the list, but the bundle is in the order they were written: id order
        var first = entries.Skip(2).Single(r => (string)r["medicationCodeableConcept"]!["coding"]![0]!["code"]! == Amoxicillin);
        var second = entries.Skip(2).Single(r => (string)r["medicationCodeableConcept"]!["coding"]![0]!["code"]! == Ibuprofen);
        Assert.NotNull(amox);
        Assert.Equal(FhirSystems.Din, (string)first["medicationCodeableConcept"]!["coding"]![0]!["system"]!);
        Assert.Equal("Amoxicillin 500 mg capsule", (string)first["medicationCodeableConcept"]!["text"]!);
        Assert.Equal("active", (string)first["status"]!);
        Assert.Equal("order", (string)first["intent"]!);
        Assert.Equal("1 capsule three times daily for 7 days", (string)first["dosageInstruction"]![0]!["text"]!);
        Assert.Equal(21, (int)first["dispenseRequest"]!["quantity"]!["value"]!);
        Assert.Equal("capsule", (string)first["dispenseRequest"]!["quantity"]!["unit"]!);
        Assert.Equal(1, (int)first["dispenseRequest"]!["numberOfRepeatsAllowed"]!);
        Assert.Equal(7, (int)first["dispenseRequest"]!["expectedSupplyDuration"]!["value"]!);
        Assert.True((bool)first["substitution"]!["allowedBoolean"]!);
        Assert.Equal("Patient prefers capsules", (string)first["note"]![0]!["text"]!);
        Assert.False((bool)second["substitution"]!["allowedBoolean"]!);
        Assert.Null(second["dispenseRequest"]!["expectedSupplyDuration"]);
        Assert.Null(second["note"]);

        // Each has its own identifier; the drugs written together share the group (and the Bundle names it).
        var ids = entries.Skip(2).Select(r => (string)r["identifier"]![0]!["value"]!).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(entries.Skip(2), r => Assert.Equal("urn:clinichub:harbourview:prescription", (string)r["identifier"]![0]!["system"]!));
        var group = (string)first["groupIdentifier"]!["value"]!;
        Assert.Equal($"G{ids.Min(int.Parse)}", group);
        Assert.Equal(group, (string)second["groupIdentifier"]!["value"]!);
        Assert.Equal(group, (string)sent.Bundle["identifier"]!["value"]!);

        // The token is ClinicHub's, for the pharmacy, with the prescriptions scope and the clinic.
        var check = await Validate(sent.Token, "pharmaflow");
        Assert.True(check.IsValid, check.Exception?.Message);
        Assert.Equal("prescriptions", check.Claims["scope"]);
        Assert.Equal("harbourview", check.Claims["clinic_slug"]);
        Assert.False((await Validate(sent.Token, "labflow")).IsValid);

        var after = await List(dr, encounter);
        Assert.All(after, p => Assert.Equal("Accepted", (string)p!["status"]!));
        Assert.All(after, p => Assert.NotNull(p!["externalId"]));
        Assert.Equal(2, after.Select(p => (string)p!["externalId"]!).Distinct().Count());
    }

    [Fact]
    public async Task Nothing_is_saved_unless_every_line_is_right()
    {
        var (dr, encounter) = await Visit(1);
        async Task<JsonNode> Bad(params object[] lines)
        {
            var response = await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(lines));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await Body(response))["errors"]!;
        }

        Assert.NotNull((await Bad())["items"]);
        Assert.NotNull((await Bad(Line("12345678")))["items[0].din"]);
        Assert.NotNull((await Bad(Line("99000201")))["items[0].din"]);   // oxycodone: controlled, not in the catalog
        var errors = await Bad(Line(Amoxicillin, directions: "x", quantity: 0, days: 400, repeats: 12), Line(Ibuprofen));
        Assert.NotNull(errors["items[0].directions"]);
        Assert.NotNull(errors["items[0].quantity"]);
        Assert.NotNull(errors["items[0].daysSupply"]);
        Assert.NotNull(errors["items[0].repeats"]);
        Assert.Null(errors["items[1].din"]);
        Assert.NotNull((await Bad(Line(Amoxicillin), Line(Amoxicillin)))["items"]);
        Assert.NotNull((await Bad(Line(Amoxicillin, note: new string('x', 501))))["items[0].note"]);
        Assert.NotNull((await Bad(Enumerable.Range(0, PrescriptionEndpoints.MaxLines + 1).Select(_ => Line(Amoxicillin)).ToArray()))["items"]);

        // The good line next to bad ones was not saved either.
        Assert.Empty(await List(dr, encounter));
    }

    [Fact]
    public async Task Only_a_prescriber_with_a_college_number_prescribes()
    {
        var (dr, encounter) = await Visit(2);
        var url = $"/api/encounters/{encounter}/prescriptions";

        var nurse = await factory.SignInAsync("nurse");
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PostAsJsonAsync(url, Write(Line(Amoxicillin)))).StatusCode);
        var admin = await factory.SignInAsync("admin");   // may do everything, but is no provider
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(url, Write(Line(Amoxicillin)))).StatusCode);

        await WithPatel(p => p.Kind = ProviderKinds.Nurse, async () =>
        {
            var response = await dr.PostAsJsonAsync(url, Write(Line(Amoxicillin)));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("physicians and nurse practitioners", (string)(await Body(response))["detail"]!);
        });
        await WithPatel(p => p.RegistrationNumber = null, async () =>
        {
            var response = await dr.PostAsJsonAsync(url, Write(Line(Amoxicillin)));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("registration number", (string)(await Body(response))["detail"]!);
        });
        Assert.Empty(await List(dr, encounter));

        // A nurse practitioner may prescribe.
        await WithPatel(p => p.Kind = ProviderKinds.NursePractitioner, async () =>
            Assert.Equal(HttpStatusCode.OK, (await dr.PostAsJsonAsync(url, Write(Line(Metformin)))).StatusCode));
    }

    [Fact]
    public async Task A_drug_is_not_prescribed_twice_at_the_same_visit_unless_the_first_was_cancelled()
    {
        var (dr, encounter) = await Visit(3);
        var url = $"/api/encounters/{encounter}/prescriptions";
        var first = await Json(await dr.PostAsJsonAsync(url, Write(Line(Warfarin))));
        var again = await dr.PostAsJsonAsync(url, Write(Line(Warfarin), Line(Ibuprofen)));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already prescribed", (string)(await Body(again))["detail"]!);
        Assert.Single(await List(dr, encounter));

        (await dr.PostAsJsonAsync($"/api/prescriptions/{(int)first[0]!["id"]!}/cancel", new { reason = "Wrong dose" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await dr.PostAsJsonAsync(url, Write(Line(Warfarin)))).StatusCode);
    }

    [Fact]
    public async Task The_prescriptions_and_their_message_are_saved_together()
    {
        var (dr, encounter) = await Visit(4);
        await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(Line(Amoxicillin), Line(Metformin))));
        await using var db = new TestDatabaseReader(factory).Context();
        var rows = await db.Prescriptions.Where(p => p.EncounterId == encounter).ToListAsync();
        Assert.Single(rows.Select(p => p.OutboxMessageId).Distinct());
        Assert.Single(rows.Select(p => p.GroupNumber).Distinct());
        Assert.Equal(rows.Min(p => p.Id), rows[0].GroupNumber);
        var message = await db.Outbox.SingleAsync(m => m.Id == rows[0].OutboxMessageId);
        Assert.Equal("PharmaFlow", message.Destination);
        Assert.Equal(OutboxKinds.Prescription, message.Kind);
        Assert.Equal(2, JsonNode.Parse(message.PayloadJson)!["entry"]!.AsArray().Count(e => (string)e!["resource"]!["resourceType"]! == "MedicationRequest"));
        // Written by the prescriber, in the clinic's access log.
        Assert.Contains(await db.PatientAccessLog.Where(l => l.PatientId == rows[0].PatientId).Select(l => l.Context).ToListAsync(), c => c == $"Prescribed 2 from encounter {encounter}");
    }

    [Fact]
    public async Task A_pharmacy_that_is_down_gets_it_later_and_after_twelve_tries_it_says_so()
    {
        var (dr, encounter) = await Visit(5);
        await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(Line(Amoxicillin))));
        factory.Fhir.FailWith = HttpStatusCode.ServiceUnavailable;
        await Dispatcher.DispatchAsync();
        var rx = (await List(dr, encounter))[0]!;
        Assert.Equal("Draft", (string)rx["status"]!);
        Assert.Equal(1, (int)rx["attempts"]!);
        Assert.Contains("down for maintenance", (string)rx["lastError"]!);

        // Not retried before its pause is over.
        var count = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        Assert.Equal(count, factory.Fhir.Requests.Count);

        factory.Fhir.FailWith = null;
        (await dr.PostAsync($"/api/prescriptions/{(int)rx["id"]!}/resend", null)).EnsureSuccessStatusCode();
        await Dispatcher.DispatchAsync();
        Assert.Equal("Accepted", (string)(await List(dr, encounter))[0]!["status"]!);

        // And one that never gets through is marked, so the clinician can try again.
        var (dr2, encounter2) = await Visit(6);
        await Json(await dr2.PostAsJsonAsync($"/api/encounters/{encounter2}/prescriptions", Write(Line(Amoxicillin))));
        factory.Fhir.FailWith = HttpStatusCode.ServiceUnavailable;
        await using (var db = new TestDatabaseReader(factory).Context())
        {
            var message = await db.Outbox.SingleAsync(m => m.Id == db.Prescriptions.Single(p => p.EncounterId == encounter2).OutboxMessageId);
            message.Attempts = OutboxDispatcher.MaxAttempts - 1;
            await db.SaveChangesAsync();
        }
        await Dispatcher.DispatchAsync();
        factory.Fhir.FailWith = null;
        Assert.Equal("Failed", (string)(await List(dr2, encounter2))[0]!["status"]!);
    }

    [Fact]
    public async Task Cancelling_before_delivery_takes_the_drug_out_of_the_message()
    {
        var (dr, encounter) = await Visit(7);
        var written = await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(Line(Amoxicillin), Line(Ibuprofen), Line(Metformin))));
        var ibuprofen = written.AsArray().Single(p => (string)p!["din"]! == Ibuprofen)!;

        var left = await Json(await dr.PostAsJsonAsync($"/api/prescriptions/{(int)ibuprofen["id"]!}/cancel", new { reason = "Allergic" }));
        Assert.Equal("Cancelled", (string)left.AsArray().Single(p => (int)p!["id"]! == (int)ibuprofen["id"]!)!["status"]!);
        Assert.Equal("Allergic", (string)left.AsArray().Single(p => (int)p!["id"]! == (int)ibuprofen["id"]!)!["cancelReason"]!);
        await using (var db = new TestDatabaseReader(factory).Context())
        {
            var message = await db.Outbox.SingleAsync(m => m.Id == db.Prescriptions.First(p => p.EncounterId == encounter && p.Status == "Draft").OutboxMessageId);
            var codes = JsonNode.Parse(message.PayloadJson)!["entry"]!.AsArray().Select(e => e!["resource"]!)
                .Where(r => (string)r["resourceType"]! == "MedicationRequest").Select(r => (string)r["medicationCodeableConcept"]!["coding"]![0]!["code"]!).ToList();
            Assert.Equal([Amoxicillin, Metformin], codes.Order());
        }

        // Cancel the rest: nothing is left to send, and nothing is sent.
        foreach (var p in left.AsArray().Where(p => (string)p!["status"]! == "Draft"))
            (await dr.PostAsJsonAsync($"/api/prescriptions/{(int)p!["id"]!}/cancel", new { })).EnsureSuccessStatusCode();
        // Other visits of this class may have messages waiting: what matters is that none of these reached the pharmacy.
        var ours = left.AsArray().Select(p => ((int)p!["id"]!).ToString()).ToHashSet();
        await Dispatcher.DispatchAsync();
        Assert.DoesNotContain(factory.Fhir.Requests, r => r.Bundle["entry"]!.AsArray().Any(e =>
            (string)e!["resource"]!["resourceType"]! == "MedicationRequest" && ours.Contains((string)e["resource"]!["identifier"]![0]!["value"]!)));
        Assert.All(await List(dr, encounter), p => Assert.Equal("Cancelled", (string)p!["status"]!));
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsJsonAsync($"/api/prescriptions/{(int)ibuprofen["id"]!}/cancel", new { })).StatusCode);
    }

    [Fact]
    public async Task Cancelling_after_delivery_asks_the_pharmacy_with_the_same_MedicationRequest()
    {
        var (dr, encounter) = await Visit(8);
        factory.Fhir.FailWith = null;
        var id = (int)(await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(Line(Amoxicillin)))))[0]!["id"]!;
        await Dispatcher.DispatchAsync();

        var asked = await Json(await dr.PostAsJsonAsync($"/api/prescriptions/{id}/cancel", new { reason = "Ordered in error" }));
        Assert.Equal("Cancelling", (string)asked[0]!["status"]!);
        var before = factory.Fhir.Requests.Count;
        await Dispatcher.DispatchAsync();
        var cancel = factory.Fhir.Requests.Skip(before).Single(r => r.Url.Host == "pharmacy.test");
        var entry = cancel.Bundle["entry"]![0]!;
        Assert.Equal("MedicationRequest", (string)entry["resource"]!["resourceType"]!);
        Assert.Equal("cancelled", (string)entry["resource"]!["status"]!);
        Assert.Equal("Ordered in error", (string)entry["resource"]!["note"]![0]!["text"]!);
        Assert.Equal("PUT", (string)entry["request"]!["method"]!);
        Assert.Equal($"MedicationRequest?identifier=urn:clinichub:harbourview:prescription|{id}", (string)entry["request"]!["url"]!);
        Assert.Equal("Cancelled", (string)(await List(dr, encounter))[0]!["status"]!);
    }

    [Fact]
    public async Task A_pharmacy_that_has_begun_to_dispense_refuses_the_cancel_and_the_prescription_stands()
    {
        var (dr, encounter) = await Visit(9);
        factory.Fhir.FailWith = null;
        var id = (int)(await Json(await dr.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(Line(Amoxicillin)))))[0]!["id"]!;
        await Dispatcher.DispatchAsync();
        await dr.PostAsJsonAsync($"/api/prescriptions/{id}/cancel", new { reason = "Changed my mind" });

        factory.Fhir.FailWith = HttpStatusCode.Conflict;
        factory.Fhir.FailText = "already being dispensed";
        await Dispatcher.DispatchAsync();
        factory.Fhir.FailWith = null;
        factory.Fhir.FailText = "down for maintenance";

        var rx = (await List(dr, encounter))[0]!;
        Assert.Equal("Accepted", (string)rx["status"]!);
        Assert.Contains("already being dispensed", (string)rx["statusNote"]!);
        Assert.Null(rx["cancelReason"]);
    }

    [Fact]
    public async Task A_visit_withheld_from_the_prescriber_cannot_be_prescribed_in()
    {
        var (dr, encounter) = await Visit(10);
        await using (var db = new TestDatabaseReader(factory).Context())
        {
            var patient = (await db.Encounters.SingleAsync(e => e.Id == encounter)).PatientId;
            // One visit withheld: only its own provider may open it, even while it is open.
            db.ConsentDirectives.Add(new ConsentDirective
            {
                PatientId = patient, Scope = ConsentScopes.Encounter, EncounterId = encounter,
                RecordedByUserId = (await db.Users.FirstAsync()).Id, CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var admin = await factory.SignInAsync("admin");   // holds the permission, but is not the visit's provider
        var response = await admin.PostAsJsonAsync($"/api/encounters/{encounter}/prescriptions", Write(Line(Amoxicillin)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True((bool)(await Body(response))["consentWithheld"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/api/encounters/{encounter}/prescriptions")).StatusCode);
        // Its own provider is not kept out.
        Assert.Equal(HttpStatusCode.OK, (await dr.GetAsync($"/api/encounters/{encounter}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task The_catalog_has_no_controlled_drugs_and_says_whether_the_pharmacy_is_connected()
    {
        var (dr, _) = await Visit(11);
        var catalog = await Json(await dr.GetAsync("/api/prescriptions/catalog"));
        Assert.All(catalog.AsArray(), d => Assert.True((bool)d!["connected"]!));
        Assert.DoesNotContain(catalog.AsArray(), d => new[] { "99000201", "99000211", "99000221", "99000231", "99000321" }.Contains((string)d!["din"]!));
        Assert.Equal(catalog.AsArray().Count, catalog.AsArray().Select(d => (string)d!["din"]!).Distinct().Count());
        Assert.All(catalog.AsArray(), d => Assert.Matches("^[0-9]{8}$", (string)d!["din"]!));
    }

    [Fact]
    public async Task Physicians_have_the_permission_and_nurses_and_front_desk_do_not()
    {
        foreach (var (user, allowed) in new[] { ("dr.patel", true), ("nurse", false), ("frontdesk", false), ("billing", false), ("admin", true) })
        {
            var me = await Json(await (await factory.SignInAsync(user)).GetAsync("/api/auth/me"));
            var permissions = me["permissions"]!.AsArray().Select(p => (string)p!).ToList();
            Assert.Equal(allowed, permissions.Contains("prescriptions.write"));
        }
    }
}
