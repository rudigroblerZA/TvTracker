using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Recommendations.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class RecommendationsContextFactory : IDesignTimeDbContextFactory<RecommendationsContext>
{
    public RecommendationsContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RecommendationsContext>().UseNpgsql("Host=localhost;Database=recommendationsdb").Options);
}
