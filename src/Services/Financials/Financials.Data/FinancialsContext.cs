using Microsoft.EntityFrameworkCore;

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

public class FinancialsContext(DbContextOptions<FinancialsContext> options) : DbContext(options)
{
    public DbSet<CostItem> CostItems => Set<CostItem>();
    public DbSet<FeeSettings> FeeSettings => Set<FeeSettings>();

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
    }
}
