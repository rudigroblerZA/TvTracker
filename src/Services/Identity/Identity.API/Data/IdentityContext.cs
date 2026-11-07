using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Identity.API.Data;

public class UserProfile
{
    /// <summary>
    /// Keycloak user id ("sub").
    /// </summary>
    public required string UserId { get; set; }

    public required string DisplayName { get; set; }

    public string? Email { get; set; }

    public string? AvatarUrl { get; set; }

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
    /// The plan they get: their own, or "family" while they're in an active family; what other services are sent
    /// (<see cref="Services.PlanState"/>).
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

/// <summary>
/// A checkout someone started: which plan and price, for whom. The provider's checkout refers back to it, so a payment
/// is credited to the user who started it rather than to anyone a webhook names.
/// </summary>
public class CheckoutSession
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Provider { get; set; }
    public required string Plan { get; set; }
    public required string Interval { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>
/// A paid plan at a provider. Active until the period it's paid for ends: renewed by a payment, ended by the provider
/// or, when no word comes, by <see cref="Services.SubscriptionLapse"/> after a grace period.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Provider { get; set; }

    /// <summary>
    /// The provider's id for it; unique per provider.
    /// </summary>
    public required string ProviderSubscriptionId { get; set; }

    public required string Plan { get; set; }
    public required string Interval { get; set; }

    /// <summary>
    /// The price per period, from its checkout: a renewal's receipt when the provider's event doesn't state one.
    /// </summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "USD";

    public SubscriptionStatus Status { get; set; }
    public DateTime CurrentPeriodEndUtc { get; set; }

    /// <summary>
    /// Cancelled, so it ends at <see cref="CurrentPeriodEndUtc"/> instead of renewing.
    /// </summary>
    public bool CancelAtPeriodEnd { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}

public enum SubscriptionStatus
{
    Active,
    Ended
}

/// <summary>
/// A payment, the user's receipt (in their data export).
/// </summary>
public class PaymentReceipt
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public Guid SubscriptionId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderPaymentId { get; set; }
    public required string Plan { get; set; }
    public required string Interval { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTime PaidAtUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
}

/// <summary>
/// A provider's webhook event already applied: providers resend, and a payment must count once.
/// </summary>
public class ProcessedPaymentEvent
{
    public required string Provider { get; set; }
    public required string EventId { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}

public class IdentityContext(DbContextOptions<IdentityContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    public DbSet<UserProfile> Profiles => Set<UserProfile>();
    public DbSet<Family> Families => Set<Family>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<PaymentReceipt> Receipts => Set<PaymentReceipt>();
    public DbSet<ProcessedPaymentEvent> ProcessedPaymentEvents => Set<ProcessedPaymentEvent>();

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

        modelBuilder.Entity<CheckoutSession>(entity =>
        {
            entity.ToTable("checkout_sessions");
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.Property(c => c.UserId).HasMaxLength(64);
            entity.Property(c => c.Provider).HasMaxLength(32);
            entity.Property(c => c.Plan).HasMaxLength(16);
            entity.Property(c => c.Interval).HasMaxLength(8);
            entity.Property(c => c.Currency).HasMaxLength(3);
            entity.Property(c => c.Amount).HasPrecision(10, 2);
            entity.HasIndex(c => c.UserId);
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions");
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.UserId).HasMaxLength(64);
            entity.Property(s => s.Provider).HasMaxLength(32);
            entity.Property(s => s.ProviderSubscriptionId).HasMaxLength(128);
            entity.Property(s => s.Plan).HasMaxLength(16);
            entity.Property(s => s.Interval).HasMaxLength(8);
            entity.Property(s => s.Amount).HasPrecision(10, 2);
            entity.Property(s => s.Currency).HasMaxLength(3);
            entity.HasIndex(s => new { s.Provider, s.ProviderSubscriptionId }).IsUnique();
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.CurrentPeriodEndUtc).HasFilter($"\"Status\" = {(int)SubscriptionStatus.Active}");
        });

        modelBuilder.Entity<PaymentReceipt>(entity =>
        {
            entity.ToTable("payment_receipts");
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.UserId).HasMaxLength(64);
            entity.Property(r => r.Provider).HasMaxLength(32);
            entity.Property(r => r.ProviderPaymentId).HasMaxLength(128);
            entity.Property(r => r.Plan).HasMaxLength(16);
            entity.Property(r => r.Interval).HasMaxLength(8);
            entity.Property(r => r.Currency).HasMaxLength(3);
            entity.Property(r => r.Amount).HasPrecision(10, 2);
            entity.HasIndex(r => new { r.Provider, r.ProviderPaymentId }).IsUnique();
            entity.HasIndex(r => r.UserId);
        });

        modelBuilder.Entity<ProcessedPaymentEvent>(entity =>
        {
            entity.ToTable("processed_payment_events");
            entity.HasKey(e => new { e.Provider, e.EventId });
            entity.Property(e => e.Provider).HasMaxLength(32);
            entity.Property(e => e.EventId).HasMaxLength(128);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}
