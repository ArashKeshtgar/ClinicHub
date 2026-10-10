using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClinicHub.Api.Catalog;

// For `dotnet ef` only: at run time the clinic database depends on the signed-in clinic.

public class ClinicDesignTimeFactory : IDesignTimeDbContextFactory<ClinicHubDbContext>
{
    public ClinicHubDbContext CreateDbContext(string[] args) =>
        ClinicDatabases.Create("Server=.;Database=ClinicHub_Design;Integrated Security=True;TrustServerCertificate=True");
}

public class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer("Server=.;Database=ClinicHub_Catalog_Design;Integrated Security=True;TrustServerCertificate=True").Options);
}
