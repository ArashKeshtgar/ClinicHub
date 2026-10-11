using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;

namespace ClinicHub.Tests;

public class PatientMatchingTests
{
    private static Patient P(string first, string last, string dob, string? card = null, string? phone = null, string sex = "F") => new()
    {
        FirstName = first, LastName = last, DateOfBirth = DateOnly.Parse(dob), HealthCardNumber = card, Phone = phone, Sex = sex,
    };

    [Fact]
    public void Same_person_registered_without_the_card_is_likely()
    {
        var m = PatientMatching.Score(P("Olivia", "Tremblay", "1980-03-14", "1286844020"), P("Olivia", "Tremblay", "1980-03-14"));
        Assert.True(m.Score >= PatientMatching.Likely, string.Join(", ", m.Reasons));
    }

    [Fact]
    public void Letters_swapped_in_the_last_name_match() =>
        AssertCandidate(P("Olivia", "Tremblay", "1980-03-14", phone: "(416) 555-1234"), P("Olivia", "Tremlbay", "1980-03-14", phone: "(416) 555-1234"));

    [Fact]
    public void Day_and_month_swapped_match() =>
        AssertCandidate(P("Olivia", "Tremblay", "1980-03-12", phone: "(416) 555-1234"), P("Olivia", "Tremblay", "1980-12-03", phone: "(416) 555-1234"));

    [Fact]
    public void Nickname_with_the_same_birth_date_and_phone_matches() =>
        AssertCandidate(P("Olivia", "Tremblay", "1980-03-14", phone: "(416) 555-1234"), P("Liv", "Tremblay", "1980-03-14", phone: "416-555-1234"));

    [Fact]
    public void Prefix_of_the_first_name_matches() =>
        AssertCandidate(P("Benjamin", "Roy", "1980-03-14", phone: "(416) 555-1234", sex: "M"), P("Ben", "Roy", "1980-03-14", phone: "416-555-1234", sex: "M"));

    private static void AssertCandidate(Patient a, Patient b)
    {
        var m = PatientMatching.Score(a, b);
        Assert.True(m.Score >= PatientMatching.Candidate, $"{m.Score}: {string.Join(", ", m.Reasons)}");
    }

    [Fact]
    public void Two_different_health_cards_are_two_people()
    {
        var m = PatientMatching.Score(P("Olivia", "Tremblay", "1980-03-14", "1286844020"), P("Olivia", "Tremblay", "1980-03-14", "9876543217"));
        Assert.True(m.Score < PatientMatching.Candidate);
    }

    [Fact]
    public void Same_name_different_birthday_is_not_a_candidate() =>
        Assert.True(PatientMatching.Score(P("Liam", "Smith", "1975-06-02", sex: "M"), P("Liam", "Smith", "1990-11-23", sex: "M")).Score < PatientMatching.Candidate);

    [Fact]
    public void Pairs_are_found_once_and_only_within_blocks()
    {
        var a = P("Olivia", "Tremblay", "1980-03-14"); a.Id = 1;
        var b = P("Olivia", "Tremblay", "1980-03-14", phone: "(416) 555-1234"); b.Id = 2;
        var c = P("Noah", "Kim", "1962-01-30", sex: "M"); c.Id = 3;
        var pairs = PatientMatching.FindPairs([a, b, c]);
        var pair = Assert.Single(pairs);
        Assert.Equal((1, 2), (pair.A.Id, pair.B.Id));
    }

    [Theory]
    [InlineData("Robert", "R163")]
    [InlineData("Rupert", "R163")]
    [InlineData("Tymczak", "T522")]
    [InlineData("Ashcraft", "A261")]
    public void Soundex(string name, string code) => Assert.Equal(code, PatientMatching.Soundex(PatientMatching.Name(name)));
}

public class PatientTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static object Fields(string first, string last, string dob, string? card = null, string? phone = null, string? postal = null) => new
    {
        firstName = first, lastName = last, dateOfBirth = dob, sex = "F", healthCardNumber = card, phone, postalCode = postal,
        preferredLanguage = "en", isDeceased = false,
    };

    [Fact]
    public async Task Registering_gives_the_next_mrn_and_checks_the_card()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var created = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Zelda", "Quillfeather", "1991-07-04", "1286 844 020", postal: "m5v0a1"), confirmNew = false }));
        Assert.StartsWith("HV", (string)created["mrn"]!);
        Assert.True(int.Parse(((string)created["mrn"]!)[2..]) > 400);
        Assert.Equal("1286844020", (string)created["fields"]!["healthCardNumber"]!);
        Assert.Equal("M5V 0A1", (string)created["fields"]!["postalCode"]!);
        Assert.Equal("ON", (string)created["fields"]!["coverageProvince"]!);

        // The same card again: refused, naming who holds it.
        var again = await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Zed", "Other", "1970-01-01", "1286844020"), confirmNew = true });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(created["id"]!.GetValue<int>(), (await again.Content.ReadFromJsonAsync<JsonNode>())!["existingPatient"]!["id"]!.GetValue<int>());

        var bad = await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Bad", "Card", "1970-01-01", "1234567890"), confirmNew = false });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("healthCardNumber", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_likely_duplicate_stops_registration_until_confirmed_then_waits_for_review()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var existing = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Marigold", "Featherstonehaugh", "1958-02-11", phone: "(416) 555-7788"), confirmNew = false }));

        var second = await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Marigold", "Featherstonhaugh", "1958-02-11", phone: "416 555 7788"), confirmNew = false });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = (await second.Content.ReadFromJsonAsync<JsonNode>())!;
        var match = problem["possibleDuplicates"]!.AsArray().Single()!;
        Assert.Equal(existing["id"]!.GetValue<int>(), match["patient"]!["id"]!.GetValue<int>());
        Assert.True(match["score"]!.GetValue<int>() >= PatientMatching.Likely);

        var confirmed = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Marigold", "Featherstonhaugh", "1958-02-11", phone: "416 555 7788"), confirmNew = true }));
        Assert.Equal(1, confirmed["openDuplicates"]!.GetValue<int>());

        // The front desk can't merge; the privacy officer sees the pair.
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync("/api/patients/duplicates")).StatusCode);
        var privacy = await factory.SignInAsync("privacy");
        var queue = (await privacy.GetFromJsonAsync<JsonArray>("/api/patients/duplicates"))!;
        Assert.Contains(queue, r => r!["a"]!["id"]!.GetValue<int>() == existing["id"]!.GetValue<int>() && r["b"]!["id"]!.GetValue<int>() == confirmed["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task Demo_clinic_starts_with_duplicates_to_review()
    {
        var privacy = await factory.SignInAsync("privacy");
        var queue = (await privacy.GetFromJsonAsync<JsonArray>("/api/patients/duplicates"))!;
        foreach (var mrn in new[] { "HV000401", "HV000402", "HV000403", "HV000404" })
            Assert.Contains(queue, r => (string)r!["b"]!["mrn"]! == mrn);
        // A second scan finds nothing new.
        var scan = await Json(await privacy.PostAsync("/api/patients/duplicates/scan", null));
        Assert.Equal(0, scan["found"]!.GetValue<int>());
    }

    [Fact]
    public async Task Merging_moves_the_history_and_the_old_mrn_still_finds_the_person()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var keep = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Persephone", "Ashbury", "1984-09-30", "9876543217"), confirmNew = false }));
        var copy = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Persephone", "Ashbury", "1984-09-30", phone: "(905) 555-0199"), confirmNew = true }));
        var (keepId, copyId) = (keep["id"]!.GetValue<int>(), copy["id"]!.GetValue<int>());
        // A walk-in under the copy: the visit must follow the person.
        await Json(await desk.PostAsJsonAsync("/api/frontdesk/walk-ins", new { patientId = copyId, reason = "Rash" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync("/api/patients/merge", new { survivorId = keepId, mergedId = copyId })).StatusCode);
        var privacy = await factory.SignInAsync("privacy");
        var merged = await Json(await privacy.PostAsJsonAsync("/api/patients/merge", new { survivorId = keepId, mergedId = copyId }));
        Assert.Equal("(905) 555-0199", (string)merged["fields"]!["phone"]!);           // blank filled from the copy
        Assert.Equal((string)copy["mrn"]!, (string)merged["mergedFrom"]![0]!["mrn"]!);
        Assert.Equal(0, merged["openDuplicates"]!.GetValue<int>());

        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        Assert.Contains(day["appointments"]!.AsArray(), a => a!["patient"]!["id"]!.GetValue<int>() == keepId && (string?)a["reason"] == "Rash");
        Assert.DoesNotContain(day["appointments"]!.AsArray(), a => a!["patient"]!["id"]!.GetValue<int>() == copyId);

        var found = (await desk.GetFromJsonAsync<JsonArray>($"/api/patients?q={copy["mrn"]}"))!;
        Assert.Equal(keepId, Assert.Single(found)!["id"]!.GetValue<int>());
        var old = await Json(await desk.GetAsync($"/api/patients/{copyId}"));
        Assert.Equal(keepId, old["mergedInto"]!["id"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PutAsJsonAsync($"/api/patients/{copyId}", new { patient = Fields("X", "Y", "1984-09-30"), rowVersion = (string)old["rowVersion"]! })).StatusCode);

        // The survivor's access log includes what was opened under the old record.
        var log = (await privacy.GetFromJsonAsync<JsonArray>($"/api/audit/access?patientId={keepId}"))!;
        Assert.Contains(log, l => (string)l!["mrn"]! == (string)copy["mrn"]! && ((string)l["context"]!).StartsWith("Registered"));
        Assert.Contains(log, l => ((string)l!["context"]!).StartsWith("Merged"));
    }

    [Fact]
    public async Task Records_with_different_cards_are_not_merged()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var a = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Ione", "Vantablack", "1999-05-05", "4532015114"), confirmNew = false }));
        var b = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Ione", "Vantablack", "1999-05-05", "1111111116"), confirmNew = true }));
        var admin = await factory.SignInAsync("admin", "harbourview");
        var res = await admin.PostAsJsonAsync("/api/patients/merge", new { survivorId = a["id"]!.GetValue<int>(), mergedId = b["id"]!.GetValue<int>() });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Correcting_a_patient_needs_the_current_version()
    {
        var desk = await factory.SignInAsync("frontdesk");
        var p = await Json(await desk.PostAsJsonAsync("/api/patients", new { patient = Fields("Wilhelmina", "Crumb", "1977-12-01"), confirmNew = false }));
        var id = p["id"]!.GetValue<int>();
        var updated = await Json(await desk.PutAsJsonAsync($"/api/patients/{id}", new { patient = Fields("Wilhelmina", "Crumbe", "1977-12-01"), rowVersion = (string)p["rowVersion"]! }));
        Assert.Equal("Crumbe", (string)updated["fields"]!["lastName"]!);
        var stale = await desk.PutAsJsonAsync($"/api/patients/{id}", new { patient = Fields("Wilhelmina", "Crumb", "1977-12-01"), rowVersion = (string)p["rowVersion"]! });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }
}
