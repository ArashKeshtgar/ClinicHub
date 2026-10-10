using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Tests;

public class EncounterTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    internal static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    /// <summary>
    /// A walk-in checked in and roomed through the front desk, then given to Dr. Patel (walk-ins go to
    /// whoever is on today, which on a weekend is not her, and only the treating provider signs).
    /// </summary>
    private Task<int> RoomedVisitForDrPatel(Func<ClinicHubDbContext, Task<Patient>> pickPatient) => RoomedVisitForDrPatel(factory, pickPatient);

    internal static async Task<int> RoomedVisitForDrPatel(ApiFactory factory, Func<ClinicHubDbContext, Task<Patient>> pickPatient)
    {
        var desk = await factory.SignInAsync("frontdesk");
        await using var db = new TestDatabaseReader(factory).Context();
        var patient = await pickPatient(db);
        var appt = (await Json(await desk.PostAsJsonAsync("/api/frontdesk/walk-ins", new { patientId = patient.Id, reason = "Sore throat" })))["id"]!.GetValue<int>();
        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{appt}/check-in", new { })).EnsureSuccessStatusCode();

        var day = await Json(await desk.GetAsync("/api/frontdesk/day"));
        var busy = day["appointments"]!.AsArray().Where(a => a!["roomId"] is not null && (string)a["status"]! is "Roomed" or "WithProvider").Select(a => (int)a!["roomId"]!).ToHashSet();
        var room = day["rooms"]!.AsArray().Select(r => (int)r!["id"]!).First(r => !busy.Contains(r));
        (await desk.PostAsJsonAsync($"/api/frontdesk/appointments/{appt}/status", new { status = "Roomed", roomId = room })).EnsureSuccessStatusCode();

        var patel = await db.Providers.Where(p => p.LastName == "Patel").Select(p => p.Id).SingleAsync();
        var encounter = await db.Encounters.Include(e => e.Appointment).SingleAsync(e => e.AppointmentId == appt);
        encounter.ProviderId = patel;
        encounter.Appointment!.ProviderId = patel;
        await db.SaveChangesAsync();
        return encounter.Id;
    }

    internal static Task<Patient> OhipPatient(ClinicHubDbContext db) =>
        db.Patients.OrderBy(p => p.Id).FirstAsync(p => p.HealthCardNumber != null && p.HealthCardExpiry > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30));

    private static Task<Patient> UninsuredPatient(ClinicHubDbContext db) =>
        db.Patients.OrderBy(p => p.Id).FirstAsync(p => p.HealthCardNumber == null && p.CoverageProvince == null);

    [Fact]
    public async Task Visit_is_documented_coded_signed_and_billed()
    {
        var id = await RoomedVisitForDrPatel(OhipPatient);
        var nurse = await factory.SignInAsync("nurse");
        var dr = await factory.SignInAsync("dr.patel");

        var bad = await nurse.PostAsJsonAsync($"/api/encounters/{id}/vitals", new { systolicMmHg = 80, diastolicMmHg = 120 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var withVitals = await Json(await nurse.PostAsJsonAsync($"/api/encounters/{id}/vitals",
            new { systolicMmHg = 128, diastolicMmHg = 82, heartRate = 76, temperatureC = 38.1, spO2 = 98, weightKg = 70, heightCm = 175 }));
        Assert.Equal(22.9m, (decimal)withVitals["vitals"]![0]!["bmi"]!);

        var started = await Json(await dr.PostAsync($"/api/encounters/{id}/start", null));
        Assert.Equal("WithProvider", (string)started["appointmentStatus"]!);

        var unready = await dr.PostAsync($"/api/encounters/{id}/sign", null);
        Assert.Equal(HttpStatusCode.BadRequest, unready.StatusCode);
        Assert.Contains("primary diagnosis", await unready.Content.ReadAsStringAsync());

        (await dr.PutAsJsonAsync($"/api/encounters/{id}/note", new
        {
            subjective = "Sore throat for 3 days, fever.", objective = "Tonsillar exudate, tender anterior nodes.",
            assessment = "Streptococcal pharyngitis likely.", plan = "Rapid strep positive. Penicillin V 10 days.",
        })).EnsureSuccessStatusCode();
        var coded = await Json(await dr.PutAsJsonAsync($"/api/encounters/{id}/coding", new
        {
            diagnoses = new[] { new { code = "034", isPrimary = true }, new { code = "460", isPrimary = false } },
            services = new object[] { new { feeCode = "A007A", units = 1 }, new { uninsuredServiceId = await NoteServiceId(), units = 1 } },
        }));
        Assert.Equal(37.95m, (decimal)coded["insuredTotal"]!);
        Assert.Empty(coded["readyToSign"]!.AsArray());

        var signed = await Json(await dr.PostAsync($"/api/encounters/{id}/sign", null));
        Assert.Equal("Signed", (string)signed["status"]!);
        Assert.Equal("Ready", (string)signed["billing"]!["claimStatus"]!);
        Assert.Equal(37.95m, (decimal)signed["billing"]!["claimAmount"]!);
        Assert.Equal(25m, (decimal)signed["billing"]!["invoiceTotal"]!);

        await using (var db = new TestDatabaseReader(factory).Context())
        {
            var claim = await db.Claims.Include(c => c.Items).SingleAsync(c => c.EncounterId == id);
            Assert.Equal("HCP", claim.PaymentProgram);
            Assert.Equal("034", Assert.Single(claim.Items).DiagnosticCode);
            Assert.Equal(id.ToString("D8"), claim.AccountingNumber);
        }

        // Locked once signed: corrections are addenda.
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PutAsJsonAsync($"/api/encounters/{id}/note", new { assessment = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await dr.PostAsync($"/api/encounters/{id}/sign", null)).StatusCode);
        var amended = await Json(await dr.PostAsJsonAsync($"/api/encounters/{id}/addendum", new { text = "Penicillin allergy denied on re-check." }));
        Assert.Contains("Dr. Nadia Patel] Penicillin allergy", (string)amended["note"]!["addendum"]!);
        Assert.Equal("Streptococcal pharyngitis likely.", (string)amended["note"]!["assessment"]!);
    }

    [Fact]
    public async Task Uninsured_visit_cannot_bill_ohip_and_is_invoiced()
    {
        var id = await RoomedVisitForDrPatel(UninsuredPatient);
        var dr = await factory.SignInAsync("dr.patel");
        var visit = await Json(await dr.GetAsync($"/api/encounters/{id}"));
        Assert.Equal("Uninsured", (string)visit["paymentProgram"]!);

        var ohip = await dr.PutAsJsonAsync($"/api/encounters/{id}/coding", new
        {
            diagnoses = new[] { new { code = "465", isPrimary = true } },
            services = new[] { new { feeCode = "A007A", units = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, ohip.StatusCode);

        int visitService;
        await using (var db = new TestDatabaseReader(factory).Context())
            visitService = await db.UninsuredServices.Where(u => u.Code == "VISIT").Select(u => u.Id).SingleAsync();
        (await dr.PutAsJsonAsync($"/api/encounters/{id}/note", new { assessment = "URI", plan = "Fluids, rest." })).EnsureSuccessStatusCode();
        (await dr.PutAsJsonAsync($"/api/encounters/{id}/coding", new
        {
            diagnoses = new[] { new { code = "465", isPrimary = true } },
            services = new[] { new { uninsuredServiceId = visitService, units = 1 } },
        })).EnsureSuccessStatusCode();
        var signed = await Json(await dr.PostAsync($"/api/encounters/{id}/sign", null));
        Assert.Null(signed["billing"]!["claimNumber"]);
        Assert.Equal(85m, (decimal)signed["billing"]!["invoiceTotal"]!);
    }

    [Fact]
    public async Task Only_the_treating_physician_codes_and_signs()
    {
        var id = await RoomedVisitForDrPatel(OhipPatient);
        var coding = new { diagnoses = new[] { new { code = "401", isPrimary = true } }, services = new[] { new { feeCode = "A001A", units = 1 } } };

        var nurse = await factory.SignInAsync("nurse");
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PutAsJsonAsync($"/api/encounters/{id}/coding", coding)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PostAsync($"/api/encounters/{id}/sign", null)).StatusCode);

        // The administrator holds every permission but is not this patient's provider.
        var admin = await factory.SignInAsync("admin");
        (await admin.PutAsJsonAsync($"/api/encounters/{id}/note", new { assessment = "HTN", plan = "Recheck in 4 weeks." })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/encounters/{id}/coding", coding)).EnsureSuccessStatusCode();
        var sign = await admin.PostAsync($"/api/encounters/{id}/sign", null);
        Assert.Equal(HttpStatusCode.Forbidden, sign.StatusCode);
        Assert.Contains("treating provider", await sign.Content.ReadAsStringAsync());

        var front = await factory.SignInAsync("frontdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await front.GetAsync($"/api/encounters/{id}")).StatusCode);
    }

    [Fact]
    public async Task Worklist_shows_my_visits_and_opening_one_is_logged()
    {
        var id = await RoomedVisitForDrPatel(OhipPatient);
        var dr = await factory.SignInAsync("dr.patel");
        var mine = await Json(await dr.GetAsync("/api/encounters"));
        var me = await Json(await dr.GetAsync("/api/auth/me"));
        Assert.Contains(mine["visits"]!.AsArray(), v => (int)v!["id"]! == id);
        Assert.All(mine["visits"]!.AsArray(), v => Assert.Equal((int)me["providerId"]!, (int)v!["providerId"]!));

        (await dr.GetAsync($"/api/encounters/{id}")).EnsureSuccessStatusCode();
        await using var db = new TestDatabaseReader(factory).Context();
        Assert.Contains(db.PatientAccessLog, l => l.Context == $"Encounter {id}" && l.Action == AccessActions.View);
    }

    [Fact]
    public async Task Two_note_saves_at_once_both_succeed()
    {
        var id = await RoomedVisitForDrPatel(OhipPatient);
        var dr = await factory.SignInAsync("dr.patel");
        var saves = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => dr.PutAsJsonAsync($"/api/encounters/{id}/note", new { subjective = $"draft {i}" })));
        Assert.All(saves, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var db = new TestDatabaseReader(factory).Context();
        Assert.Equal(1, await db.ClinicalNotes.CountAsync(n => n.EncounterId == id));
    }

    [Fact]
    public async Task Demo_day_opens_encounters_for_patients_already_in()
    {
        await using var db = new TestDatabaseReader(factory).Context();
        var underway = await db.Appointments.Where(a => a.Status == AppointmentStatus.Roomed || a.Status == AppointmentStatus.WithProvider || a.Status == AppointmentStatus.Completed)
            .Select(a => a.Id).ToListAsync();
        var withEncounter = await db.Encounters.Where(e => e.AppointmentId != null).Select(e => e.AppointmentId!.Value).ToListAsync();
        Assert.Empty(underway.Except(withEncounter));
    }

    private async Task<int> NoteServiceId()
    {
        await using var db = new TestDatabaseReader(factory).Context();
        return await db.UninsuredServices.Where(u => u.Code == "NOTE").Select(u => u.Id).SingleAsync();
    }
}
