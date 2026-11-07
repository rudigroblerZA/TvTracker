using Identity.API.Data;
using Identity.API.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker.Persistence;

namespace Identity.API.Services;

/// <summary>
/// Ends subscriptions whose paid period is over, hourly, so a provider's lost or late "ended" webhook can't leave someone
/// on a plan nobody pays for.
/// <list type="bullet">
/// <item><b>Renewing:</b> ended once the period is over by more than <see cref="PaymentOptions.GraceDays"/>, so a renewal
/// that arrives a day late doesn't take the plan away meanwhile.</item>
/// <item><b>Cancelled:</b> ended as soon as the period is over, the date the plan page gave; no renewal is coming.</item>
/// <item><b>One at a time:</b> a subscription that fails is logged and left for the next run, and doesn't hold up the rest.</item>
/// </list>
/// </summary>
public sealed class SubscriptionLapse(IServiceScopeFactory scopes, TimeProvider time, IOptions<PaymentOptions> options, ILogger<SubscriptionLapse> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether an active subscription has lapsed: its period ended more than the grace period ago.
    /// </summary>
    internal static bool Lapsed(Subscription subscription, DateTime nowUtc, int graceDays) =>
        subscription.Status == SubscriptionStatus.Active
        && subscription.CurrentPeriodEndUtc.AddDays(subscription.CancelAtPeriodEnd ? 0 : graceDays) < nowUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), time, stoppingToken);
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await EndLapsedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ending lapsed subscriptions failed; trying again in an hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task EndLapsedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<SubscriptionLedger>();
        var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
        var now = time.GetUtcNow();
        var renewingBefore = now.UtcDateTime.AddDays(-options.Value.GraceDays);
        // The same rule as Lapsed, as a query.
        var lapsed = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active
                && (s.CancelAtPeriodEnd ? s.CurrentPeriodEndUtc < now.UtcDateTime : s.CurrentPeriodEndUtc < renewingBefore))
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in lapsed)
        {
            try
            {
                var subscription = await db.Subscriptions.SingleAsync(s => s.Id == id, ct);
                await ledger.EndAsync(subscription, now, ct);
                await db.SaveChangesAsync(ct);
                await outbox.PublishAddedAsync(ct);
                logger.LogInformation("Subscription {Subscription} of {UserId} lapsed on {PeriodEnd:d}", subscription.Id, subscription.UserId, subscription.CurrentPeriodEndUtc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ending lapsed subscription {Subscription} failed; trying again next run", id);
            }
            finally
            {
                // Nothing a failed one left tracked rides along with the next one's save.
                db.ChangeTracker.Clear();
            }
        }
    }
}
