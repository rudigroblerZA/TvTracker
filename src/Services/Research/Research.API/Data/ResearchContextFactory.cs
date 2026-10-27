using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Research.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class ResearchContextFactory : IDesignTimeDbContextFactory<ResearchContext>
{
    public ResearchContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ResearchContext>().UseNpgsql("Host=localhost;Database=researchdb").Options);
}
