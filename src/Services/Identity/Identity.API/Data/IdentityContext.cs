using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Identity.API.Data;

public class UserProfile
{
    /// <summary>Keycloak user id ("sub").</summary>
    public required string UserId { get; set; }

    public required string DisplayName { get; set; }

    public string? Email { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>IANA timezone id, e.g. "Africa/Johannesburg". Clients convert UTC air times with it.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>ISO 3166-1 country code; drives where-to-watch results.</summary>
    public string Region { get; set; } = "US";

    public string Language { get; set; } = "en";

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public class IdentityContext(DbContextOptions<IdentityContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<UserProfile> Profiles => Set<UserProfile>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();

    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("profiles");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.DisplayName).HasMaxLength(100);
            entity.Property(p => p.Email).HasMaxLength(256);
            entity.Property(p => p.AvatarUrl).HasMaxLength(1024);
            entity.Property(p => p.TimeZone).HasMaxLength(64);
            entity.Property(p => p.Region).HasMaxLength(2);
            entity.Property(p => p.Language).HasMaxLength(10);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}
