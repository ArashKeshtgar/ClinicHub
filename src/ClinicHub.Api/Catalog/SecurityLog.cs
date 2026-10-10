using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Catalog;

/// <summary>Writes the catalog's security log. Rows are only ever added.</summary>
public class SecurityLog(CatalogDbContext catalog, IHttpContextAccessor http, TimeProvider clock)
{
    /// <summary>Adds an event; it is saved with the caller's next SaveChanges on the catalog.</summary>
    public void Add(string type, int? actor, int? target = null, string? detail = null, int? clinicId = null) =>
        catalog.SecurityEvents.Add(new SecurityEvent
        {
            AtUtc = clock.GetUtcNow().UtcDateTime, Type = type, ClinicId = clinicId, ActorAccountId = actor, TargetAccountId = target,
            Detail = detail is { Length: > 500 } ? detail[..500] : detail,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });

    public async Task WriteAsync(string type, int? actor, int? target = null, string? detail = null, int? clinicId = null)
    {
        Add(type, actor, target, detail, clinicId);
        await catalog.SaveChangesAsync();
    }
}

/// <summary>Keeps a clinic's copy of its staff (<see cref="User"/>) in step with the catalog accounts.</summary>
public static class StaffSync
{
    public static async Task EnsureAsync(ClinicHubDbContext clinicDb, Account account, bool member, DateTime now)
    {
        var copy = await clinicDb.Users.SingleOrDefaultAsync(u => u.Id == account.Id);
        if (copy is null)
        {
            if (!member)
                return;
            clinicDb.Users.Add(copy = new User { Id = account.Id });
        }
        copy.UserName = account.UserName;
        copy.DisplayName = account.DisplayName;
        copy.Credentials = account.Credentials;
        copy.IsActive = member && account.IsActive;
        copy.SyncedUtc = now;
        await clinicDb.SaveChangesAsync();
    }
}
