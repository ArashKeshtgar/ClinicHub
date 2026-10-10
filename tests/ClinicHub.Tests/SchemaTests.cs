using ClinicHub.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Tests;

/// <summary>
/// A throw-away database migrated from scratch. Uses CLINICHUB_TEST_SQL when set (CI uses a SQL Server
/// container), otherwise the local default instance with Windows authentication.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public string ConnectionString { get; }

    public TestDatabase()
    {
        var server = Environment.GetEnvironmentVariable("CLINICHUB_TEST_SQL")
            ?? "Server=.;Integrated Security=True;TrustServerCertificate=True";
        ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = $"ClinicHub_Test_{Guid.NewGuid():N}" }.ConnectionString;
    }

    public ClinicHubDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ClinicHubDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        SqlConnection.ClearAllPools();
        await db.Database.EnsureDeletedAsync();
    }
}

public class SchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static readonly DateTime Now = new(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc);

    private static Patient NewPatient(string mrn, string? hcn = null) => new()
    {
        Mrn = mrn, FirstName = "Test", LastName = "Patient", DateOfBirth = new DateOnly(1980, 1, 1), Sex = "F",
        HealthCardNumber = hcn, HealthCardVersion = hcn is null ? null : "AB", CreatedUtc = Now, UpdatedUtc = Now,
    };

    /// <summary>Saves and expects SQL Server to refuse the row with the named constraint or index.</summary>
    private async Task AssertRejected(Action<ClinicHubDbContext> add, string constraint)
    {
        await using var db = database.NewContext();
        add(db);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(constraint, ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task Migrations_create_every_schema()
    {
        await using var db = database.NewContext();
        var schemas = await db.Database.SqlQueryRaw<string>(
            "SELECT DISTINCT s.name AS Value FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name <> 'dbo'").ToListAsync();
        Assert.Equal(["billing", "care", "clinic", "orders", "people", "ref", "sched", "sec"], schemas.Order());
    }

    [Fact]
    public Task Health_card_must_be_ten_digits() =>
        AssertRejected(db => db.Patients.Add(NewPatient("T-HC1", "12345")), "CK_Patient_HealthCard");

    [Fact]
    public Task Sex_must_be_a_known_code() =>
        AssertRejected(db =>
        {
            var p = NewPatient("T-SEX");
            p.Sex = "Q";
            db.Patients.Add(p);
        }, "CK_Patient_Sex");

    [Fact]
    public async Task One_live_record_per_health_card_but_merged_records_keep_theirs()
    {
        await using (var db = database.NewContext())
        {
            db.Patients.Add(NewPatient("T-DUP1", "1286844022"));
            await db.SaveChangesAsync();
        }
        await AssertRejected(db => db.Patients.Add(NewPatient("T-DUP2", "1286844022")), "IX_Patient_HealthCardNumber");

        await using (var db = database.NewContext())
        {
            var survivor = NewPatient("T-DUP3");
            db.Patients.Add(survivor);
            await db.SaveChangesAsync();
            var merged = NewPatient("T-DUP4", "1286844022");
            merged.MergedIntoPatientId = survivor.Id;
            db.Patients.Add(merged);
            await db.SaveChangesAsync(); // allowed: the filtered unique index ignores merged records
        }
    }

    [Fact]
    public async Task Appointment_must_end_after_it_starts()
    {
        var ids = await SeedBasics();
        await AssertRejected(db => db.Appointments.Add(new Appointment
        {
            PatientId = ids.Patient, ProviderId = ids.Provider, LocationId = ids.Location, AppointmentTypeId = ids.Type,
            StartUtc = Now, EndUtc = Now.AddMinutes(-15), CreatedByUserId = ids.User, CreatedUtc = Now,
        }), "CK_Appointment_Time");
    }

    [Fact]
    public async Task Signed_encounter_needs_a_signer()
    {
        var ids = await SeedBasics();
        await AssertRejected(db => db.Encounters.Add(new Encounter
        {
            PatientId = ids.Patient, ProviderId = ids.Provider, LocationId = ids.Location,
            Status = EncounterStatus.Signed, CheckInUtc = Now,
        }), "CK_Encounter_Signed");
    }

    [Fact]
    public async Task Billed_service_is_either_ohip_or_uninsured_not_both()
    {
        var ids = await SeedBasics();
        int encounterId;
        await using (var db = database.NewContext())
        {
            var e = new Encounter { PatientId = ids.Patient, ProviderId = ids.Provider, LocationId = ids.Location, CheckInUtc = Now };
            db.Encounters.Add(e);
            await db.SaveChangesAsync();
            encounterId = e.Id;
        }
        await AssertRejected(db => db.EncounterServices.Add(new EncounterService
        {
            EncounterId = encounterId, FeeCodeServiceCode = "A007A", UninsuredServiceId = ids.Uninsured, Fee = 10, ServiceDate = new DateOnly(2026, 10, 10),
        }), "CK_EncounterService_OneKind");
    }

    [Fact]
    public async Task Invoice_totals_must_add_up()
    {
        var ids = await SeedBasics();
        await AssertRejected(db => db.Invoices.Add(new Invoice
        {
            Number = $"T-{Guid.NewGuid():N}"[..20], PatientId = ids.Patient, Subtotal = 25, Tax = 0, Total = 30, Balance = 30, CreatedUtc = Now,
        }), "CK_Invoice_Totals");
    }

    [Fact]
    public async Task Clinical_history_is_not_deleted_with_the_patient()
    {
        var ids = await SeedBasics();
        await using var db = database.NewContext();
        db.Encounters.Add(new Encounter { PatientId = ids.Patient, ProviderId = ids.Provider, LocationId = ids.Location, CheckInUtc = Now });
        await db.SaveChangesAsync();

        await using var db2 = database.NewContext();
        var ex = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            db2.Patients.Remove(await db2.Patients.SingleAsync(p => p.Id == ids.Patient));
            await db2.SaveChangesAsync();
        });
        Assert.Contains("FK_Encounter_Patient_PatientId", ex.InnerException!.Message);
    }

    private record Ids(int Patient, int Provider, int Location, int Type, int User, int Uninsured);

    private async Task<Ids> SeedBasics()
    {
        await using var db = database.NewContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserName = $"u{suffix}", DisplayName = "Test", CreatedUtc = Now };
        var location = new Location { Name = "Test clinic", AddressLine1 = "1 Test St", City = "Toronto", PostalCode = "M5V 2T6" };
        var provider = new Provider { FirstName = "Avery", LastName = "Test" };
        var type = new AppointmentType { Name = $"Visit {suffix}", DurationMinutes = 15 };
        var patient = NewPatient($"T-{suffix}");
        var uninsured = new UninsuredService { Code = $"U{suffix}", Name = "Sick note", Price = 25 };
        if (!await db.FeeCodes.AnyAsync(f => f.ServiceCode == "A007A"))
            db.FeeCodes.Add(new FeeCode { ServiceCode = "A007A", Description = "Intermediate assessment", Fee = 40, EffectiveFrom = new DateOnly(2026, 1, 1) });
        db.AddRange(user, location, provider, type, patient, uninsured);
        await db.SaveChangesAsync();
        return new Ids(patient.Id, provider.Id, location.Id, type.Id, user.Id, uninsured.Id);
    }
}
