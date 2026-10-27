using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tracking.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class TrackingContextFactory : IDesignTimeDbContextFactory<TrackingContext>
{
    public TrackingContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TrackingContext>().UseNpgsql("Host=localhost;Database=trackingdb").Options);
}
