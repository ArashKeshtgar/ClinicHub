using ClinicHub.Api.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Catalog;

/// <summary>Opening a clinic, and keeping every clinic's database and built-in roles up to date.</summary>
public class ClinicProvisioning(CatalogDbContext catalog, ClinicDatabases databases, IOptions<ClinicOptions> options, SecurityLog log, TimeProvider clock)
{
    /// <summary>
    /// A clinic: its catalog entry, the built-in roles, its own database (created and migrated), and the
    /// creator as its first administrator so someone can sign in and manage it.
    /// </summary>
    public async Task<Clinic> CreateAsync(string slug, string name, int? creatorAccountId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var clinic = new Clinic { Slug = slug, Name = name, DatabaseName = options.Value.DatabasePrefix + slug, CreatedUtc = now };
        catalog.Clinics.Add(clinic);
        await catalog.SaveChangesAsync();
        await EnsureRolesAsync(clinic);
        await databases.MigrateAsync(clinic);
        // A first location under the clinic's name, so the day board works from the start; address and rooms are filled in later.
        await using (var clinicDb = databases.Open(clinic))
        {
            clinicDb.Locations.Add(new Data.Location { Name = name });
            await clinicDb.SaveChangesAsync();
        }

        if (creatorAccountId is { } creator)
        {
            var admin = await catalog.Roles.SingleAsync(r => r.ClinicId == clinic.Id && r.Name == SystemRoles.Administrator);
            catalog.AccountRoles.Add(new AccountRole { AccountId = creator, RoleId = admin.Id });
            await catalog.SaveChangesAsync();
            var account = await catalog.Accounts.SingleAsync(a => a.Id == creator);
            await using var db = databases.Open(clinic);
            await StaffSync.EnsureAsync(db, account, member: true, now);
        }
        await log.WriteAsync(SecurityEventTypes.ClinicCreated, creatorAccountId, detail: $"{clinic.Name} ({clinic.Slug}, database {clinic.DatabaseName})", clinicId: clinic.Id);
        return clinic;
    }

    /// <summary>
    /// Adds missing built-in roles and gives Administrator every permission, including ones a new version
    /// added. Never overwrites a clinic's edits to the other roles.
    /// </summary>
    public async Task EnsureRolesAsync(Clinic clinic)
    {
        var existing = await catalog.Roles.Where(r => r.ClinicId == clinic.Id).ToDictionaryAsync(r => r.Name);
        foreach (var (name, description, permissions) in SystemRoles.Defaults.Where(d => !existing.ContainsKey(d.Name)))
            catalog.Roles.Add(new Role { ClinicId = clinic.Id, Name = name, Description = description, Permissions = Permissions.Format(permissions), IsSystem = true });
        if (existing.TryGetValue(SystemRoles.Administrator, out var admin))
            admin.Permissions = Permissions.Format(Permissions.All);
        await catalog.SaveChangesAsync();
    }

    /// <summary>On start-up: the catalog, then every active clinic's database and roles.</summary>
    public async Task UpgradeAllAsync()
    {
        await catalog.Database.MigrateAsync();
        foreach (var clinic in await catalog.Clinics.Where(c => c.IsActive).ToListAsync())
        {
            await EnsureRolesAsync(clinic);
            await databases.MigrateAsync(clinic);
        }
    }
}
