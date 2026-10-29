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

    /// <summary>The plan this user pays for (<see cref="TvTracker.IntegrationEvents.Plans"/>); everyone starts free.</summary>
    public string Plan { get; set; } = TvTracker.IntegrationEvents.Plans.Free;

    /// <summary>When <see cref="Plan"/> was last set; null while it never has.</summary>
    public DateTime? PlanChangedAtUtc { get; set; }

    /// <summary>
    /// The plan they get: their own, or "family" while they're in an active family; what other services are sent
    /// (<see cref="Services.PlanState"/>).
    /// </summary>
    public string EffectivePlan { get; set; } = TvTracker.IntegrationEvents.Plans.Free;

    /// <summary>When <see cref="EffectivePlan"/> last changed; null while it never has.</summary>
    public DateTime? EffectivePlanChangedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A family plan's group, one per owner. Members get the family plan while the owner pays for it.</summary>
public class Family
{
    public required string OwnerUserId { get; set; }

    /// <summary>The secret in the invite link (<c>/family/join/{token}</c>); null while invites are off.</summary>
    public string? InviteToken { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Someone in a family; a person is in at most one.</summary>
public class FamilyMember
{
    public required string UserId { get; set; }
    public required string OwnerUserId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}

public class IdentityContext(DbContextOptions<IdentityContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<UserProfile> Profiles => Set<UserProfile>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

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
            entity.Property(p => p.Plan).HasMaxLength(16).HasDefaultValue(TvTracker.IntegrationEvents.Plans.Free);
            entity.Property(p => p.EffectivePlan).HasMaxLength(16).HasDefaultValue(TvTracker.IntegrationEvents.Plans.Free);
        });

        modelBuilder.Entity<Family>(entity =>
        {
            entity.ToTable("families");
            entity.HasKey(f => f.OwnerUserId);
            entity.Property(f => f.OwnerUserId).HasMaxLength(64);
            entity.Property(f => f.InviteToken).HasMaxLength(32);
            entity.HasIndex(f => f.InviteToken).IsUnique().HasFilter("\"InviteToken\" IS NOT NULL");
        });

        modelBuilder.Entity<FamilyMember>(entity =>
        {
            entity.ToTable("family_members");
            entity.HasKey(m => m.UserId);
            entity.Property(m => m.UserId).HasMaxLength(64);
            entity.Property(m => m.OwnerUserId).HasMaxLength(64);
            entity.HasIndex(m => m.OwnerUserId);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}
