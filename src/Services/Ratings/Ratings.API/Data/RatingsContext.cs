using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Ratings.API.Data;

public class ShowRating
{
    public required string UserId { get; set; }
    public Guid ShowId { get; set; }
    public int Stars { get; set; }
    public DateTime RatedAtUtc { get; set; }
}

public class EpisodeRating
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public int Stars { get; set; }
    public DateTime RatedAtUtc { get; set; }
}

/// <summary>A post people can flag and moderators can hide: a comment or a community insight.</summary>
public interface IModeratedPost
{
    string UserId { get; }
    bool IsSpoiler { get; set; }
    string? HiddenReason { get; set; }
}

public class Comment : IModeratedPost
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid ShowId { get; set; }
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public required string Text { get; set; }

    /// <summary>The author says the comment contains spoilers; it stays hidden until the reader opts in.</summary>
    public bool IsSpoiler { get; set; }

    /// <summary>Why only its author sees it; null when everyone does. One of <see cref="HiddenReasons"/>.</summary>
    public string? HiddenReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Why a comment or community insight is hidden from everyone but its author.</summary>
public static class HiddenReasons
{
    /// <summary>Enough readers flagged it; it waits for a moderator.</summary>
    public const string Flags = "flags";

    /// <summary>Held for review when posted (it had a link); it waits for a moderator.</summary>
    public const string Held = "held";

    /// <summary>A moderator removed it.</summary>
    public const string Moderator = "moderator";
}

/// <summary>Someone reporting a comment or community insight. Resolved once a moderator restores or removes it.</summary>
public class ContentFlag
{
    public Guid Id { get; set; }

    /// <summary>"comment" or "insight".</summary>
    public required string TargetKind { get; set; }

    public Guid TargetId { get; set; }
    public required string UserId { get; set; }

    /// <summary>spoiler, abuse, spam, off-topic or wrong.</summary>
    public required string Reason { get; set; }

    public string? Note { get; set; }
    public bool Resolved { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// What a viewer knows about a show or an episode and wants to pass on ("the first two episodes are slow; it gets good at
/// 3", "watch the 2005 film first"). Others mark it helpful; the most helpful lead.
/// </summary>
public class CommunityInsight : IModeratedPost
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }

    /// <summary>Null for the show as a whole.</summary>
    public Guid? EpisodeId { get; set; }

    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public required string Text { get; set; }
    public bool IsSpoiler { get; set; }

    /// <summary>As for comments: one of <see cref="HiddenReasons"/>, or null.</summary>
    public string? HiddenReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public class InsightVote
{
    public Guid InsightId { get; set; }
    public required string UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// A suggested fix to the catalog's data for a show or an episode. A moderator accepts it (the catalog sync worker then
/// overrides the provider's value, refresh after refresh) or rejects it; an accepted one can be reverted.
/// </summary>
public class Correction
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public Guid? EpisodeId { get; set; }

    /// <summary>show-summary, episode-title, episode-summary, episode-airdate, episode-runtime, or other (not applied).</summary>
    public required string Field { get; set; }

    /// <summary>What the page showed when it was suggested, so the moderator sees both.</summary>
    public string? CurrentValue { get; set; }

    public required string SuggestedValue { get; set; }
    public string? Note { get; set; }

    /// <summary>The show and episode as the suggester saw them ("Severance · S01E03"), for the queue.</summary>
    public string? Context { get; set; }

    public required string UserId { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>pending, accepted, rejected or reverted.</summary>
    public required string Status { get; set; }

    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>What a moderator did, for the log on the moderation page.</summary>
public class ModerationAction
{
    public Guid Id { get; set; }
    public required string ModeratorId { get; set; }
    public required string ModeratorName { get; set; }

    /// <summary>restore, remove, accept, reject or revert.</summary>
    public required string Action { get; set; }

    /// <summary>"comment", "insight" or "correction".</summary>
    public required string TargetKind { get; set; }

    public Guid TargetId { get; set; }
    public string? Summary { get; set; }
    public DateTime AtUtc { get; set; }
}

public class Reaction
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
    public required string Emoji { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Local copy of watched episodes (from EpisodeWatched), used to decide whether to show comments.</summary>
public class WatchedEpisodeCopy
{
    public required string UserId { get; set; }
    public Guid EpisodeId { get; set; }
}

public class RatingsContext(DbContextOptions<RatingsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<ShowRating> ShowRatings => Set<ShowRating>();
    public DbSet<EpisodeRating> EpisodeRatings => Set<EpisodeRating>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<WatchedEpisodeCopy> WatchedEpisodes => Set<WatchedEpisodeCopy>();
    public DbSet<ContentFlag> Flags => Set<ContentFlag>();
    public DbSet<CommunityInsight> Insights => Set<CommunityInsight>();
    public DbSet<InsightVote> InsightVotes => Set<InsightVote>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<ModerationAction> ModerationActions => Set<ModerationAction>();

    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShowRating>(entity =>
        {
            entity.ToTable("show_ratings");
            entity.HasKey(r => new { r.UserId, r.ShowId });
            entity.HasIndex(r => r.ShowId);
            entity.Property(r => r.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<EpisodeRating>(entity =>
        {
            entity.ToTable("episode_ratings");
            entity.HasKey(r => new { r.UserId, r.EpisodeId });
            entity.HasIndex(r => r.EpisodeId);
            entity.Property(r => r.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<Comment>(entity =>
        {
            entity.ToTable("comments");
            entity.HasIndex(c => new { c.EpisodeId, c.CreatedAtUtc });
            entity.HasIndex(c => c.UserId);
            entity.Property(c => c.UserId).HasMaxLength(64);
            entity.Property(c => c.DisplayName).HasMaxLength(100);
            entity.Property(c => c.Text).HasMaxLength(2000);
            entity.Property(c => c.HiddenReason).HasMaxLength(16);
        });

        modelBuilder.Entity<ContentFlag>(entity =>
        {
            entity.ToTable("content_flags");
            entity.HasIndex(f => new { f.TargetKind, f.TargetId, f.UserId }).IsUnique();
            entity.HasIndex(f => f.Resolved);
            entity.HasIndex(f => new { f.UserId, f.CreatedAtUtc });
            entity.Property(f => f.TargetKind).HasMaxLength(16);
            entity.Property(f => f.UserId).HasMaxLength(64);
            entity.Property(f => f.Reason).HasMaxLength(16);
            entity.Property(f => f.Note).HasMaxLength(500);
        });

        modelBuilder.Entity<CommunityInsight>(entity =>
        {
            entity.ToTable("community_insights");
            entity.HasIndex(i => new { i.ShowId, i.EpisodeId });
            entity.HasIndex(i => new { i.UserId, i.CreatedAtUtc });
            entity.Property(i => i.UserId).HasMaxLength(64);
            entity.Property(i => i.DisplayName).HasMaxLength(100);
            entity.Property(i => i.Text).HasMaxLength(1000);
            entity.Property(i => i.HiddenReason).HasMaxLength(16);
        });

        modelBuilder.Entity<InsightVote>(entity =>
        {
            entity.ToTable("insight_votes");
            entity.HasKey(v => new { v.InsightId, v.UserId });
            entity.HasIndex(v => v.UserId);
            entity.Property(v => v.UserId).HasMaxLength(64);
        });

        modelBuilder.Entity<Correction>(entity =>
        {
            entity.ToTable("corrections");
            entity.HasIndex(c => new { c.Status, c.CreatedAtUtc });
            entity.HasIndex(c => new { c.UserId, c.CreatedAtUtc });
            entity.Property(c => c.Field).HasMaxLength(32);
            entity.Property(c => c.CurrentValue).HasMaxLength(4000);
            entity.Property(c => c.SuggestedValue).HasMaxLength(4000);
            entity.Property(c => c.Note).HasMaxLength(1000);
            entity.Property(c => c.Context).HasMaxLength(300);
            entity.Property(c => c.UserId).HasMaxLength(64);
            entity.Property(c => c.DisplayName).HasMaxLength(100);
            entity.Property(c => c.Status).HasMaxLength(16);
            entity.Property(c => c.ReviewedBy).HasMaxLength(100);
        });

        modelBuilder.Entity<ModerationAction>(entity =>
        {
            entity.ToTable("moderation_actions");
            entity.HasIndex(a => a.AtUtc);
            entity.Property(a => a.ModeratorId).HasMaxLength(64);
            entity.Property(a => a.ModeratorName).HasMaxLength(100);
            entity.Property(a => a.Action).HasMaxLength(16);
            entity.Property(a => a.TargetKind).HasMaxLength(16);
            entity.Property(a => a.Summary).HasMaxLength(300);
        });

        modelBuilder.Entity<Reaction>(entity =>
        {
            entity.ToTable("reactions");
            entity.HasKey(r => new { r.UserId, r.EpisodeId });
            entity.HasIndex(r => r.EpisodeId);
            entity.Property(r => r.UserId).HasMaxLength(64);
            entity.Property(r => r.Emoji).HasMaxLength(16);
        });

        modelBuilder.Entity<WatchedEpisodeCopy>(entity =>
        {
            entity.ToTable("watched_episodes");
            entity.HasKey(w => new { w.UserId, w.EpisodeId });
            entity.Property(w => w.UserId).HasMaxLength(64);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}
