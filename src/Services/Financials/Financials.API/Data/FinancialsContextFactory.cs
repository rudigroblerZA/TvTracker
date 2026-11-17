using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Financials.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class FinancialsContextFactory : IDesignTimeDbContextFactory<FinancialsContext>
{
    public FinancialsContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FinancialsContext>().UseNpgsql("Host=localhost;Database=financialsdb").Options);
}
