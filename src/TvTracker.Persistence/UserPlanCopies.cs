using Microsoft.EntityFrameworkCore;

namespace TvTracker.Persistence;

/// <summary>
/// A member's plan as a service other than Identity keeps it (<c>user_plans</c>): Identity owns plans and can't be asked
/// on every request, so the services that need a plan keep this from its events. No row means free.
/// </summary>
public class UserPlanCopy
{
    public required string UserId { get; set; }
    public required string Plan { get; set; }

    /// <summary>
    /// When Identity changed it; the newest wins, so a late or repeated event can't undo a newer plan.
    /// </summary>
    public DateTimeOffset ChangedAtUtc { get; set; }
}

/// <summary>
/// A service's context that keeps <see cref="UserPlanCopy"/>. It maps the table with <see cref="UserPlanCopies.AddUserPlanCopies"/>.
/// </summary>
public interface IUserPlanCopies
{
    DbSet<UserPlanCopy> Plans { get; }
}

public static class UserPlanCopies
{
    /// <summary>
    /// Maps <see cref="UserPlanCopy"/> to <c>user_plans</c>, the same in every service that keeps it.
    /// </summary>
    public static ModelBuilder AddUserPlanCopies(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserPlanCopy>(entity =>
        {
            entity.ToTable("user_plans");
            entity.HasKey(p => p.UserId);
            entity.Property(p => p.UserId).HasMaxLength(64);
            entity.Property(p => p.Plan).HasMaxLength(16);
        });
        return modelBuilder;
    }
}
