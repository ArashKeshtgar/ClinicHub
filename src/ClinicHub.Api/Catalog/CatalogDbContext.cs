using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Catalog;

/// <summary>
/// The one database shared by every clinic: clinics, accounts, roles per clinic and the security log.
/// Each clinic's patients, visits and billing are in that clinic's own database (<see cref="Data.ClinicHubDbContext"/>).
/// </summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AccountRole> AccountRoles => Set<AccountRole>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveColumnType("datetime2(3)");
        b.Properties<DateTime?>().HaveColumnType("datetime2(3)");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("catalog");
        b.Entity<Clinic>(e =>
        {
            e.ToTable("Clinic", t => t.HasCheckConstraint("CK_Clinic_Slug", "Slug NOT LIKE '%[^a-z0-9-]%' AND LEN(Slug) >= 2"));
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.DatabaseName).IsUnique();
        });
        b.Entity<Account>(e =>
        {
            e.ToTable("Account");
            e.HasIndex(x => x.UserName).IsUnique();
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("Role");
            e.HasIndex(x => new { x.ClinicId, x.Name }).IsUnique();
            e.HasOne(x => x.Clinic).WithMany(c => c.Roles).HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AccountRole>(e =>
        {
            e.ToTable("AccountRole");
            e.HasKey(x => new { x.AccountId, x.RoleId });
            e.HasOne(x => x.Account).WithMany(a => a.Roles).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany(r => r.Accounts).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SecurityEvent>(e =>
        {
            e.ToTable("SecurityEvent");
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => new { x.ClinicId, x.AtUtc });
            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ActorAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.TargetAccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
