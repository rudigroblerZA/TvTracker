using Microsoft.EntityFrameworkCore;
using TvTracker.Persistence;

namespace Financials.Data;

public enum CostRecurrence
{
    /// <summary>
    /// Paid every month while it runs.
    /// </summary>
    Monthly,

    /// <summary>
    /// Paid once a year; the statement spreads it evenly, a twelfth to each month it runs.
    /// </summary>
    Yearly,

    /// <summary>
    /// Paid once, in the month it starts.
    /// </summary>
    Once
}

/// <summary>
/// Something the business pays for, entered by an admin on the financials page: the server, a data licence, a domain,
/// an accountant's bill. Not about any user.
/// </summary>
public class CostItem
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// One of <c>FinancialStatement.Categories</c>.
    /// </summary>
    public required string Category { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// ISO 4217, upper case. Costs are never converted: each currency has its own book.
    /// </summary>
    public required string Currency { get; set; }

    public CostRecurrence Recurrence { get; set; }

    /// <summary>
    /// The first day it's paid for (the day it's paid, for a one-off).
    /// </summary>
    public DateOnly StartsOn { get; set; }

    /// <summary>
    /// The last day it's paid for; null while it runs.
    /// </summary>
    public DateOnly? EndsOn { get; set; }

    public string? Notes { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// The payment provider's rate, as an admin set it on the financials page from the provider's own statement. One row;
/// without it the configured rate (<c>Financials:PaymentFee*</c>) is used.
/// </summary>
public class FeeSettings
{
    public const int OnlyId = 1;

    public int Id { get; set; }

    /// <summary>
    /// Percent of each payment.
    /// </summary>
    public decimal Percent { get; set; }

    /// <summary>
    /// Added per payment, in the payment's own currency.
    /// </summary>
    public decimal Fixed { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public class FinancialsContext(DbContextOptions<FinancialsContext> options) : DbContext(options), IIntegrationEventLogDbContext
{
    /// <summary>
    /// The unique index that holds a user to one running subscription, whatever races the code's own check.
    /// </summary>
    public const string OneRunningSubscription = "IX_subscriptions_UserId_Running";

    public DbSet<CostItem> CostItems => Set<CostItem>();
    public DbSet<FeeSettings> FeeSettings => Set<FeeSettings>();
    public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<PaymentReceipt> Receipts => Set<PaymentReceipt>();
    public DbSet<ProcessedPaymentEvent> ProcessedPaymentEvents => Set<ProcessedPaymentEvent>();
    public DbSet<PaymentSettings> PaymentSettings => Set<PaymentSettings>();
    public DbSet<OutboundClick> OutboundClicks => Set<OutboundClick>();
    public DbSet<AffiliateConversion> AffiliateConversions => Set<AffiliateConversion>();
    public DbSet<AffiliateProgramRecord> AffiliatePrograms => Set<AffiliateProgramRecord>();

    /// <summary>
    /// Integration events waiting to be published (the outbox).
    /// </summary>
    public DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();

    /// <summary>
    /// Integration events already applied here (the inbox).
    /// </summary>
    public DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CostItem>(entity =>
        {
            entity.ToTable("cost_items");
            entity.Property(c => c.Name).HasMaxLength(120);
            entity.Property(c => c.Category).HasMaxLength(40);
            entity.Property(c => c.Amount).HasPrecision(12, 2);
            entity.Property(c => c.Currency).HasMaxLength(3);
            entity.Property(c => c.Notes).HasMaxLength(500);
        });

        modelBuilder.Entity<FeeSettings>(entity =>
        {
            entity.ToTable("fee_settings");
            entity.Property(f => f.Id).ValueGeneratedNever();
            entity.Property(f => f.Percent).HasPrecision(5, 2);
            entity.Property(f => f.Fixed).HasPrecision(8, 2);
        });

        modelBuilder.Entity<PaymentSettings>(entity =>
        {
            entity.ToTable("payment_settings");
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Provider).HasMaxLength(32);
            entity.Property(s => s.StripeSecretKey).HasMaxLength(1024);
            entity.Property(s => s.StripeWebhookSecret).HasMaxLength(1024);
            entity.Property(s => s.StripePortalUrl).HasMaxLength(Data.PaymentSettings.PortalUrlLength);
            entity.Property(s => s.UpdatedBy).HasMaxLength(Data.PaymentSettings.UpdatedByLength);
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
            entity.HasIndex(s => s.UserId, OneRunningSubscription).IsUnique().HasFilter($"\"Status\" = {(int)SubscriptionStatus.Active}");
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

        // One row per where-to-watch click, as Catalog.API reported it (OutboundClickedHandler).
        modelBuilder.Entity<OutboundClick>(entity =>
        {
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.ToTable("outbound_clicks");
            entity.HasIndex(c => c.ClickedAtUtc);
            entity.Property(c => c.Service).HasMaxLength(128);
            entity.Property(c => c.ShowTitle).HasMaxLength(256);
            entity.Property(c => c.Type).HasMaxLength(16);
            entity.Property(c => c.Link).HasMaxLength(16);
            entity.Property(c => c.Region).HasMaxLength(2);
            entity.Property(c => c.Surface).HasMaxLength(32);
            entity.Property(c => c.Plan).HasMaxLength(16);
            entity.Property(c => c.Program).HasMaxLength(64);
        });

        modelBuilder.Entity<AffiliateProgramRecord>(entity =>
        {
            entity.ToTable("affiliate_programs");
            entity.HasKey(p => p.Name);
            entity.Property(p => p.Name).HasMaxLength(64);
            entity.Property(p => p.Template).HasMaxLength(1024);
            entity.Property(p => p.PostbackKey).HasMaxLength(256);
            entity.Property(p => p.Currency).HasMaxLength(3);
            entity.Property(p => p.Services).HasDefaultValueSql("'{}'");
            entity.Property(p => p.Regions).HasDefaultValueSql("'{}'");
        });

        modelBuilder.Entity<AffiliateConversion>(entity =>
        {
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.ToTable("affiliate_conversions");
            entity.HasOne<OutboundClick>().WithMany().HasForeignKey(c => c.ClickId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => new { c.Program, c.TransactionId }).IsUnique();
            entity.HasIndex(c => c.ReceivedAtUtc);
            entity.Property(c => c.Program).HasMaxLength(64);
            entity.Property(c => c.TransactionId).HasMaxLength(128);
            entity.Property(c => c.Amount).HasPrecision(12, 2);
            entity.Property(c => c.Currency).HasMaxLength(3);
        });

        modelBuilder.UseIntegrationEventLogs();
    }
}
