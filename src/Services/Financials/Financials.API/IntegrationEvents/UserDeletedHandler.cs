using Financials.API.Apis;
using Financials.API.Payments;
using Financials.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Financials.API.IntegrationEvents;

/// <summary>
/// An account was deleted: its running subscriptions are stopped at their providers, so nobody is charged for an account
/// that's gone, and its payment records are removed. A provider that doesn't answer is logged to be stopped by hand; the
/// records go all the same, as the person asked. The provider's confirmation then finds no subscription here
/// (<see cref="LedgerResult.NotHere"/>).
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
        await PaymentsApi.StopSubscriptionsAsync(db, providers, e.UserId, logger, ct);
        await PaymentsApi.RemoveRecordsAsync(db, e.UserId, ct);
        db.MarkProcessed(e, nameof(UserDeletedHandler));
        await db.SaveChangesAsync(ct);
    }
}
