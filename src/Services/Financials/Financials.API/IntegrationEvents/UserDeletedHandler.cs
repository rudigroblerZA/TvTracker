using Financials.API.Payments;
using Financials.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Financials.API.IntegrationEvents;

/// <summary>
/// An account was deleted: its running subscriptions are stopped at their providers, so nobody is charged for an account
/// that's gone, its payment records are removed, and their name comes off the payment settings they saved. A provider
/// that doesn't answer is logged to be stopped by hand; the records go all the same, as the person asked. The provider's
/// confirmation then finds no subscription here (<see cref="LedgerResult.NotHere"/>).
/// </summary>
public sealed class UserDeletedHandler(FinancialsContext db, PaymentProviders providers, ILogger<UserDeletedHandler> logger)
    : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public async Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(UserDeletedHandler), ct))
        {
            return;
        }
        await StopSubscriptionsAsync(e.UserId, ct);
        await RemoveRecordsAsync(e.UserId, ct);
        db.MarkProcessed(e, nameof(UserDeletedHandler));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stops every running subscription at its provider at once, so nobody is charged for an account that's gone. A
    /// provider that fails (down, timing out) is logged to be stopped by hand and doesn't keep the records.
    /// </summary>
    private async Task StopSubscriptionsAsync(string userId, CancellationToken ct)
    {
        foreach (var subscription in await db.Subscriptions.Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active).ToListAsync(ct))
        {
            try
            {
                await (providers.Named(subscription.Provider)?.CancelAsync(subscription, now: true, ct) ?? Task.CompletedTask);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Subscription {Subscription} couldn't be stopped at {Provider}; stop it there by hand", subscription.ProviderSubscriptionId, subscription.Provider);
            }
        }
    }

    /// <summary>
    /// Marks the member's payment records for deletion; the caller saves (the provider keeps its own for its books).
    /// </summary>
    private async Task RemoveRecordsAsync(string userId, CancellationToken ct)
    {
        db.Receipts.RemoveRange(await db.Receipts.Where(r => r.UserId == userId).ToListAsync(ct));
        db.Subscriptions.RemoveRange(await db.Subscriptions.Where(s => s.UserId == userId).ToListAsync(ct));
        db.CheckoutSessions.RemoveRange(await db.CheckoutSessions.Where(c => c.UserId == userId).ToListAsync(ct));
        // The settings an admin saved stay in force; who saved them is no longer said.
        foreach (var settings in await db.PaymentSettings.Where(s => s.UpdatedById == userId).ToListAsync(ct))
        {
            (settings.UpdatedBy, settings.UpdatedById) = (null, null);
        }
    }
}
