using System.Collections.Concurrent;
using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Catalog;

public class ClinicOptions
{
    /// <summary>Prefix of every clinic database on the shared server: ClinicHub_harbourview, ClinicHub_lakeshore…</summary>
    public string DatabasePrefix { get; set; } = "ClinicHub_";
}

/// <summary>
/// Finds and opens a clinic's own database. Connection strings come from the catalog and are cached; a
/// clinic on its own server stores an encrypted connection string, the rest share ConnectionStrings:ClinicServer.
/// </summary>
public class ClinicDatabases(IConfiguration config, IServiceScopeFactory scopes, IDataProtectionProvider protection)
{
    private const string ConnectionPurpose = "ClinicHub.ClinicConnection";
    private readonly ConcurrentDictionary<int, string> _connections = new();

    private string Server => config.GetConnectionString("ClinicServer")
        ?? throw new InvalidOperationException("ConnectionStrings:ClinicServer is not configured.");

    public string ConnectionStringFor(Clinic clinic) => clinic.ConnectionString is { } protectedValue
        ? protection.CreateProtector(ConnectionPurpose).Unprotect(protectedValue)
        : new SqlConnectionStringBuilder(Server) { InitialCatalog = clinic.DatabaseName }.ConnectionString;

    public string Protect(string connectionString) => protection.CreateProtector(ConnectionPurpose).Protect(connectionString);

    public ClinicHubDbContext Open(Clinic clinic) => Create(ConnectionStringFor(clinic));

    public async Task<ClinicHubDbContext> OpenAsync(int clinicId)
    {
        if (!_connections.TryGetValue(clinicId, out var cs))
        {
            using var scope = scopes.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var clinic = await catalog.Clinics.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clinicId && c.IsActive)
                ?? throw new NoClinicException();
            cs = _connections[clinicId] = ConnectionStringFor(clinic);
        }
        return Create(cs);
    }

    /// <summary>For dependency injection, which cannot await. Cookie validation has already cached the clinic by then.</summary>
    public ClinicHubDbContext Open(int clinicId) =>
        _connections.TryGetValue(clinicId, out var cs) ? Create(cs) : OpenAsync(clinicId).GetAwaiter().GetResult();

    public void Forget(int clinicId) => _connections.TryRemove(clinicId, out _);

    public static ClinicHubDbContext Create(string connectionString) =>
        new(new DbContextOptionsBuilder<ClinicHubDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options);

    /// <summary>Creates or upgrades a clinic's database and the lists every clinic needs.</summary>
    public async Task MigrateAsync(Clinic clinic)
    {
        await using var db = Open(clinic);
        await db.Database.MigrateAsync();
        await ReferenceSeeder.SeedAsync(db);
    }
}

/// <summary>The request was for clinic data but no clinic has been chosen (or it was closed).</summary>
public class NoClinicException() : Exception("Choose a clinic first.");

public static class ClinicDatabaseSetup
{
    /// <summary>
    /// The clinic database of the current request is the one chosen at sign-in (the "clinic" claim). Endpoints
    /// that touch clinic data also require a permission, which is only granted once a clinic is chosen.
    /// </summary>
    public static void AddClinicDatabases(this IServiceCollection services, IConfiguration config)
    {
        var catalog = config.GetConnectionString("Catalog") ?? throw new InvalidOperationException("ConnectionStrings:Catalog is not configured.");
        services.AddDbContext<CatalogDbContext>(o => o.UseSqlServer(catalog, sql => sql.EnableRetryOnFailure()));
        services.Configure<ClinicOptions>(config.GetSection("Clinics"));
        services.AddSingleton<ClinicDatabases>();
        services.AddHttpContextAccessor();
        services.AddScoped(sp =>
        {
            var user = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
            var clinicId = user?.ClinicId() ?? throw new NoClinicException();
            return sp.GetRequiredService<ClinicDatabases>().Open(clinicId);
        });
    }

    public static int? ClinicId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.Clinic), out var id) ? id : null;
}
