using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ratings.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class RatingsContextFactory : IDesignTimeDbContextFactory<RatingsContext>
{
    public RatingsContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RatingsContext>().UseNpgsql("Host=localhost;Database=ratingsdb").Options);
}
