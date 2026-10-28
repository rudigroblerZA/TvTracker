using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Notifications.API.Data;

public class NotificationPreferences
{
    public required string UserId { get; set; }

    // What to be told about
    public bool NewEpisodes { get; set; } = true;
    public bool NewSeasons { get; set; } = true;
    public bool StatusChanges { get; set; } = true;

    // How
    public bool PushEnabled { get; set; } = true;
    public bool WebPushEnabled { get; set; } = true;
    public bool EmailEnabled { get; set; }

    /// <summary>From the user's profile (UserProfileUpdated) or token.</summary>
    public string? Email { get; set; }

    /// <summary>Alerts are held during quiet hours (in the user's timezone) and sent when they end.</summary>
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>IANA timezone id, from the user's profile.</summary>
    public string TimeZone { get; set; } = "UTC";

    public DateTime UpdatedAtUtc { get; set; }
}

public class ShowPreference
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public bool Muted { get; set; }
}

public enum DevicePlatform
{
    Android,
    iOS,
    Web
}

public class Device
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public DevicePlatform Platform { get; set; }

    /// <summary>FCM or APNs token (Android / iOS), or the push service endpoint (web).</summary>
    public required string Token { get; set; }

    /// <summary>Web push subscription keys.</summary>
    public string? P256dh { get; set; }
    public string? Auth { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}

/// <summary>Local copy of who follows which show (from ShowFollowed / ShowUnfollowed).</summary>
public class Follower
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
}

public enum NotificationKind
{
    NewEpisode,
    NewSeason,
    StatusChange,
    SeasonVerdict,
    Crossover
}

public class InboxItem
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public NotificationKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>In-app link, e.g. "/shows/{id}".</summary>
    public string? Link { get; set; }

    public Guid ShowId { get; set; }
    public Guid? EpisodeId { get; set; }

    /// <summary>The event that caused this notification; unique per user, so a redelivered event never notifies twice.</summary>
    public Guid SourceEventId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}

public enum DeliveryChannel
{
    Push,
    WebPush,
    Email
}

public enum DeliveryStatus
{
    Pending,
    Sent,
    Failed,
    Skipped
}

/// <summary>One notification to send on one channel; the delivery worker processes these as a queue.</summary>
public class Delivery
{
    public Guid Id { get; set; }
    public Guid InboxItemId { get; set; }
    public InboxItem InboxItem { get; set; } = null!;
    public required string UserId { get; set; }
    public DeliveryChannel Channel { get; set; }
    public DeliveryStatus Status { get; set; }
    public DateTime ScheduledForUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public class NotificationsContext(DbContextOptions<NotificationsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<NotificationPreferences> Preferences => Set<NotificationPreferences>();
    public DbSet<ShowPreference> ShowPreferences => Set<ShowPreference>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Follower> Followers => Set<Follower>();
    public DbSet<InboxItem> Inbox => Set<InboxItem>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationPreferences>(entity =>
        {
            entity.ToTable("preferences");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.Email).HasMaxLength(256);
            entity.Property(p => p.TimeZone).HasMaxLength(64);
        });

        modelBuilder.Entity<ShowPreference>(entity =>
        {
            entity.ToTable("show_preferences");
            entity.HasKey(p => new { p.UserId, p.ShowId });
            entity.Property(p => p.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");
            entity.HasIndex(d => d.UserId);
            entity.HasIndex(d => d.Token).IsUnique();
            entity.Property(d => d.UserId).HasMaxLength(64);
            entity.Property(d => d.Token).HasMaxLength(1024);
            entity.Property(d => d.P256dh).HasMaxLength(256);
            entity.Property(d => d.Auth).HasMaxLength(256);
        });

        modelBuilder.Entity<Follower>(entity =>
        {
            entity.ToTable("followers");
            entity.HasKey(f => new { f.ShowId, f.UserId });
            entity.Property(f => f.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<InboxItem>(entity =>
        {
            entity.ToTable("inbox");
            entity.HasIndex(i => new { i.UserId, i.CreatedAtUtc });
            entity.HasIndex(i => new { i.UserId, i.SourceEventId }).IsUnique();
            entity.Property(i => i.UserId).HasMaxLength(64);
            entity.Property(i => i.Title).HasMaxLength(256);
            entity.Property(i => i.Body).HasMaxLength(1024);
            entity.Property(i => i.Link).HasMaxLength(256);
        });

        modelBuilder.Entity<Delivery>(entity =>
        {
            entity.ToTable("deliveries");
            entity.HasIndex(d => new { d.Status, d.ScheduledForUtc });
            entity.HasOne(d => d.InboxItem).WithMany().HasForeignKey(d => d.InboxItemId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(d => d.UserId).HasMaxLength(64);
            entity.Property(d => d.LastError).HasMaxLength(1024);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}
