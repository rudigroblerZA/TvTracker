using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Analytics.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running a service. Migrations are generated from the model alone,
/// so the connection string is never used. See the README for the command.
/// </summary>
public sealed class AnalyticsContextFactory : IDesignTimeDbContextFactory<AnalyticsContext>
{
    public AnalyticsContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AnalyticsContext>().UseNpgsql("Host=localhost;Database=analyticsdb").Options);
}
