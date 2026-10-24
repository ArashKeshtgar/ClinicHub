using System.Security.Cryptography;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Catalog;

/// <summary>
/// One-off commands for a server where nobody can sign in yet (no demo users outside a demo). They run after the
/// catalog and clinic databases are upgraded, print what they did and exit; on the VPS:
/// <code>docker compose run --rm clinichub admin arash "Arash Keshtgar" harbourview lakeshore</code>
/// </summary>
public static class AdminCommands
{
    public const string Usage = """
        Commands:
          seed-demo                                  fictional clinics with synthetic patients; their accounts get random
                                                     passwords nobody knows (no one-click sign-in without Demo:Enabled)
          admin <user name> [display name] [clinic]...
                                                     a platform administrator with a new temporary password (changed at
                                                     the first sign-in), Administrator at each clinic named
        """;

    /// <summary>Runs the command <paramref name="args"/> names; false when they name none (start the web server).</summary>
    public static async Task<bool> RunAsync(string[] args, IServiceProvider services)
    {
        // Settings such as --Demo:Enabled=true are the host's; a command is the first plain word.
        var words = args.Where(a => !a.StartsWith('-') && !a.Contains('=')).ToArray();
        switch (words)
        {
            case []:
                return false;
            case ["seed-demo"]:
                await DemoSeeder.SeedAsync(services.GetRequiredService<CatalogDbContext>(), services.GetRequiredService<ClinicProvisioning>(),
                    services.GetRequiredService<ClinicDatabases>(), services.GetRequiredService<IPasswordHasher<Account>>(),
                    services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
                Console.WriteLine($"Demo clinics seeded: {string.Join(", ", DemoSeeder.Clinics.Select(c => c.Slug))}.");
                return true;
            case ["admin", var userName, .. var rest]:
                var (summary, password) = await AdminAsync(services, userName, rest);
                Console.WriteLine(summary);
                Console.WriteLine($"Temporary password (changed at the first sign-in): {password}");
                return true;
            default:
                Console.Error.WriteLine(Usage);
                Environment.ExitCode = 2;
                return true;
        }
    }

    /// <summary>Creates or resets the platform administrator; returns what was done and the temporary password.</summary>
    public static async Task<(string Summary, string Password)> AdminAsync(IServiceProvider services, string userName, string[] rest)
    {
        var catalog = services.GetRequiredService<CatalogDbContext>();
        var hasher = services.GetRequiredService<IPasswordHasher<Account>>();
        var log = services.GetRequiredService<SecurityLog>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        userName = userName.Trim().ToLowerInvariant();
        // A display name is any word that is not a clinic's slug.
        var slugs = await catalog.Clinics.Where(c => c.IsActive).Select(c => c.Slug).ToListAsync();
        var displayName = rest.FirstOrDefault(w => !slugs.Contains(w));
        var clinics = rest.Where(slugs.Contains).Distinct().ToArray();

        var account = await catalog.Accounts.Include(a => a.Roles).SingleOrDefaultAsync(a => a.UserName == userName);
        var created = account is null;
        account ??= catalog.Accounts.Add(new Account { UserName = userName, DisplayName = displayName ?? userName, CreatedUtc = now }).Entity;
        if (displayName is { Length: > 0 })
            account.DisplayName = displayName.Trim();
        var password = NewPassword(userName);
        account.PasswordHash = hasher.HashPassword(account, password);
        account.MustChangePassword = true;
        account.IsPlatformAdmin = true;
        account.FailedLoginCount = 0;
        account.LockoutEndUtc = null;
        account.SecurityStamp = AuthEndpoints.NewStamp();
        await catalog.SaveChangesAsync();
        log.Add(created ? SecurityEventTypes.UserCreated : SecurityEventTypes.PasswordReset, actor: null, account.Id, "Platform administrator, from the server's command line");

        var databases = services.GetRequiredService<ClinicDatabases>();
        foreach (var slug in clinics)
        {
            var clinic = await catalog.Clinics.SingleAsync(c => c.Slug == slug);
            var role = await catalog.Roles.SingleAsync(r => r.ClinicId == clinic.Id && r.Name == SystemRoles.Administrator);
            if (account.Roles.All(r => r.RoleId != role.Id))
            {
                account.Roles.Add(new AccountRole { AccountId = account.Id, RoleId = role.Id });
                log.Add(SecurityEventTypes.MemberAdded, actor: null, account.Id, $"Roles: {SystemRoles.Administrator}", clinic.Id);
            }
            await catalog.SaveChangesAsync();
            await using var db = databases.Open(clinic);
            await StaffSync.EnsureAsync(db, account, member: true, now);
        }
        await catalog.SaveChangesAsync();

        return ($"{(created ? "Created" : "Reset")} {account.UserName} ({account.DisplayName}), platform administrator"
            + (clinics.Length > 0 ? $", Administrator at {string.Join(", ", clinics)}" : "") + ".", password);
    }

    /// <summary>20 letters and digits, which every password rule here accepts and nobody has to escape.</summary>
    private static string NewPassword(string userName)
    {
        while (true)
        {
            var p = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 20);
            if (UserEndpoints.PasswordProblem(p, userName) is null)
                return p;
        }
    }
}
