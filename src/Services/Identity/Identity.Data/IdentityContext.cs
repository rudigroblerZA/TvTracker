using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Identity.Data;

/// <summary>
/// What admins have set in Settings for the whole site: the app's name and accent colour, and the company behind it.
/// One row; a field left null falls back to configuration (<c>BrandingApi</c>).
/// </summary>
public class SiteSettings
{
    /// <summary>
    /// The one row's key.
    /// </summary>
    public const int OnlyId = 1;

    public int Id { get; set; }

    public string? AppName { get; set; }

    public string? AccentColor { get; set; }

    public string? Operator { get; set; }

    public string? ContactEmail { get; set; }

    public string? OperatorAddress { get; set; }

    public string? CompanyNumber { get; set; }

    public string? GoverningLaw { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public class UserProfile
{
    /// <summary>
    /// Keycloak user id ("sub").
    /// </summary>
    public required string UserId { get; set; }

    public required string DisplayName { get; set; }

    public string? Email { get; set; }

    /// <summary>
    /// A picture elsewhere on the web, by its https address. Used when no picture has been uploaded.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Set while the user has an uploaded picture (<see cref="ProfileAvatar"/>), and different after each upload: it
    /// goes into the picture's address, so browsers keep a picture for good and still see a new one at once.
    /// </summary>
    public long? AvatarVersion { get; set; }

    /// <summary>
    /// The picture to show for this user, as the profile events carry it: the uploaded one's path under
    /// <c>api/v1/</c>, else <see cref="AvatarUrl"/>, else null.
    /// </summary>
    public string? Avatar => AvatarOf(UserId, AvatarVersion, AvatarUrl);

    /// <summary>
    /// <see cref="Avatar"/> from its parts, for a query that reads only those.
    /// </summary>
    public static string? AvatarOf(string userId, long? version, string? url) => version is not null ? $"avatars/{userId}?v={version}" : url;

    /// <summary>
    /// IANA timezone id, e.g. "Africa/Johannesburg". Clients convert UTC air times with it.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// ISO 3166-1 country code; drives where-to-watch results.
    /// </summary>
    public string Region { get; set; } = "US";

    public string Language { get; set; } = "en";

    /// <summary>
    /// The plan this user pays for (<see cref="TvTracker.IntegrationEvents.Plans"/>); everyone starts free.
    /// </summary>
    public string Plan { get; set; } = TvTracker.IntegrationEvents.Plans.Free;

    /// <summary>
    /// When <see cref="Plan"/> was last set; null while it never has.
    /// </summary>
    public DateTime? PlanChangedAtUtc { get; set; }

    /// <summary>
    /// The subscription that set <see cref="Plan"/>, so only its own ending takes the plan away; null when an admin set
    /// it, or a subscription from before ids were sent.
    /// </summary>
    public Guid? PlanSubscriptionId { get; set; }

    /// <summary>
    /// The subscription last heard to have ended, and when: its own start, arriving late, is then known to be over.
    /// </summary>
    public Guid? EndedSubscriptionId { get; set; }

    public DateTime? EndedSubscriptionAtUtc { get; set; }

    /// <summary>
    /// The plan they get: their own, or "family" while they're in an active family; what other services are sent
    /// (<c>PlanState</c>).
    /// </summary>
    public string EffectivePlan { get; set; } = TvTracker.IntegrationEvents.Plans.Free;

    /// <summary>
    /// When <see cref="EffectivePlan"/> last changed; null while it never has.
    /// </summary>
    public DateTime? EffectivePlanChangedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A family plan's group, one per owner. Members get the family plan while the owner pays for it.
/// </summary>
public class Family
{
    public required string OwnerUserId { get; set; }

    /// <summary>
    /// The secret in the invite link (<c>/family/join/{token}</c>); null while invites are off.
    /// </summary>
    public string? InviteToken { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Someone in a family; a person is in at most one.
/// </summary>
public class FamilyMember
{
    public required string UserId { get; set; }
    public required string OwnerUserId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}

public class IdentityContext(DbContextOptions<IdentityContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<UserProfile> Profiles => Set<UserProfile>();
    public DbSet<ProfileAvatar> Avatars => Set<ProfileAvatar>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();

    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SiteSettings>(entity =>
        {
            entity.ToTable("site_settings");
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.AppName).HasMaxLength(40);
            entity.Property(s => s.AccentColor).HasMaxLength(7);
            entity.Property(s => s.Operator).HasMaxLength(120);
            entity.Property(s => s.ContactEmail).HasMaxLength(256);
            entity.Property(s => s.OperatorAddress).HasMaxLength(300);
            entity.Property(s => s.CompanyNumber).HasMaxLength(160);
            entity.Property(s => s.GoverningLaw).HasMaxLength(80);
        });

        modelBuilder.Entity<ProfileAvatar>(entity =>
        {
            entity.ToTable("profile_avatars");
            entity.HasKey(a => a.UserId);
            entity.Property(a => a.UserId).HasMaxLength(64);
            entity.Property(a => a.ContentType).HasMaxLength(32);
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("profiles");
            entity.Ignore(p => p.Avatar);
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
