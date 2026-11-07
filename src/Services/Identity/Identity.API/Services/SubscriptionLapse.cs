using Identity.API.Data;
using Identity.API.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker.Persistence;

namespace Identity.API.Services;

/// <summary>
/// Ends subscriptions whose paid period is over by more than <see cref="PaymentOptions.GraceDays"/> with no renewal:
/// hourly, so a provider's lost or late "ended" webhook can't leave someone on a plan nobody pays for, and a renewal that
/// arrives a day late doesn't take Plus away meanwhile.
/// </summary>
public sealed class SubscriptionLapse(IServiceScopeFactory scopes, TimeProvider time, IOptions<PaymentOptions> options, ILogger<SubscriptionLapse> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether an active subscription has lapsed: its period ended more than the grace period ago.
    /// </summary>
    internal static bool Lapsed(Subscription subscription, DateTime nowUtc, int graceDays) =>
        subscription.Status == SubscriptionStatus.Active && subscription.CurrentPeriodEndUtc.AddDays(graceDays) < nowUtc;

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
        var before = now.UtcDateTime.AddDays(-options.Value.GraceDays);
        var lapsed = await db.Subscriptions.Where(s => s.Status == SubscriptionStatus.Active && s.CurrentPeriodEndUtc < before).ToListAsync(ct);
        foreach (var subscription in lapsed)
        {
            await ledger.EndAsync(subscription, now, ct);
            await db.SaveChangesAsync(ct);
            await outbox.PublishAddedAsync(ct);
            logger.LogInformation("Subscription {Subscription} of {UserId} lapsed on {PeriodEnd:d}", subscription.Id, subscription.UserId, subscription.CurrentPeriodEndUtc);
        }
    }
}
