using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Notifications.API.Data;

/// <summary>
/// Lets `dotnet ef` build the model without running the service. Migrations are generated from the model alone,
/// so the connection string is never used.
/// </summary>
public sealed class NotificationsContextFactory : IDesignTimeDbContextFactory<NotificationsContext>
{
    public NotificationsContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsContext>().UseNpgsql("Host=localhost;Database=notificationsdb").Options);
}
