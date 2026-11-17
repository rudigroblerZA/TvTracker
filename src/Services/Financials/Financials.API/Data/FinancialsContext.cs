using Microsoft.EntityFrameworkCore;

namespace Financials.API.Data;

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
    /// One of <see cref="Services.FinancialStatement.Categories"/>.
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

public class FinancialsContext(DbContextOptions<FinancialsContext> options) : DbContext(options)
{
    public DbSet<CostItem> CostItems => Set<CostItem>();

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
    }
}
