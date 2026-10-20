using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Catalog.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running a service. Migrations are generated from the model alone,
/// so the connection string is never used. See the README for the command.
/// </summary>
public sealed class CatalogContextFactory : IDesignTimeDbContextFactory<CatalogContext>
{
    public CatalogContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogContext>().UseNpgsql("Host=localhost;Database=catalogdb").Options);
}
