using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Social.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class SocialContextFactory : IDesignTimeDbContextFactory<SocialContext>
{
    public SocialContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SocialContext>().UseNpgsql("Host=localhost;Database=socialdb").Options);
}
