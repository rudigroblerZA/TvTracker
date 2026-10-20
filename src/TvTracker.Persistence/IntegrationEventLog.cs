using Microsoft.EntityFrameworkCore;

namespace TvTracker.Persistence;

public enum EventState
{
    NotPublished = 0,
    Published = 1,
    PublishFailed = 2
}

/// <summary>An outgoing event stored in the same transaction as the data change that raised it.</summary>
public class IntegrationEventLogEntry
{
    public Guid EventId { get; set; }

    /// <summary>Assembly-qualified type name, used to deserialize the event for publishing.</summary>
    public required string EventTypeName { get; set; }

    public required string Content { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public EventState State { get; set; }

    public int TimesSent { get; set; }
}

/// <summary>Records that a handler has processed an incoming event, so redeliveries are ignored.</summary>
public class ProcessedIntegrationEvent
{
    public Guid EventId { get; set; }

    public required string Handler { get; set; }

    public DateTime ProcessedAtUtc { get; set; }
}

/// <summary>Implemented by each service's DbContext to get the outbox and inbox tables.</summary>
public interface IIntegrationEventLogDbContext
{
    DbSet<IntegrationEventLogEntry> IntegrationEventLogs { get; }

    DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents { get; }
}

public static class IntegrationEventLogModelBuilderExtensions
{
    /// <summary>Call from OnModelCreating to map the outbox and inbox tables.</summary>
    public static ModelBuilder UseIntegrationEventLogs(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntegrationEventLogEntry>(entity =>
        {
            entity.ToTable("integration_event_log");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventTypeName).HasMaxLength(512);
            entity.HasIndex(e => new { e.State, e.CreatedAtUtc });
        });

        modelBuilder.Entity<ProcessedIntegrationEvent>(entity =>
        {
            entity.ToTable("processed_integration_events");
            entity.HasKey(e => new { e.EventId, e.Handler });
            entity.Property(e => e.Handler).HasMaxLength(256);
        });

        return modelBuilder;
    }
}
