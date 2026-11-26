using Financials.Data;
using Microsoft.EntityFrameworkCore;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Financials.API.IntegrationEvents;

/// <summary>
/// Keeps each where-to-watch click Catalog.API reports, under the click's own id, which the link carried to the
/// network: a sale reported later is matched to it. The id is the key, so a click delivered twice is kept once.
/// </summary>
public sealed class OutboundClickedHandler(FinancialsContext db) : IIntegrationEventHandler<OutboundClickedIntegrationEvent>
{
    public async Task HandleAsync(OutboundClickedIntegrationEvent e, CancellationToken ct)
    {
        await db.HandleOnceAsync(e, nameof(OutboundClickedHandler), async () =>
        {
            // The same click under a new event id (Catalog sent it again): the row is already here.
            if (await db.OutboundClicks.AnyAsync(c => c.Id == e.ClickId, ct))
            {
                return;
            }
            db.OutboundClicks.Add(new OutboundClick
            {
                Id = e.ClickId,
                ShowId = e.ShowId,
                ShowTitle = e.ShowTitle.Length > 256 ? e.ShowTitle[..256] : e.ShowTitle,
                Service = e.Service,
                Type = e.Type,
                Link = e.Link,
                Region = e.Region,
                Surface = e.Surface,
                Plan = e.Plan,
                Program = e.Program,
                ClickedAtUtc = e.ClickedAtUtc.UtcDateTime
            });
        }, ct);
    }
}
