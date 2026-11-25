using Catalog.API.Services;
using Catalog.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Catalog.API.IntegrationEvents;

/// <summary>
/// Keeps Catalog's copy of the affiliate programs as Financials.API publishes them. The newest list wins, so an hourly
/// repeat that arrives after an admin's change doesn't undo it.
/// </summary>
public sealed class AffiliateProgramsChangedHandler(CatalogContext db) : IIntegrationEventHandler<AffiliateProgramsChangedIntegrationEvent>
{
    public async Task HandleAsync(AffiliateProgramsChangedIntegrationEvent e, CancellationToken ct)
    {
        if (await db.HasProcessedAsync(e, nameof(AffiliateProgramsChangedHandler), ct))
        {
            return;
        }
        var copy = await db.AffiliatePrograms.FindAsync([AffiliateProgramsCopy.OnlyId], ct);
        if (copy is null)
        {
            db.AffiliatePrograms.Add(new AffiliateProgramsCopy
            {
                Id = AffiliateProgramsCopy.OnlyId, Programs = CopiedAffiliatePrograms.Write(e.Programs), AsOfUtc = e.AsOfUtc
            });
        }
        else if (e.AsOfUtc >= copy.AsOfUtc)
        {
            (copy.Programs, copy.AsOfUtc) = (CopiedAffiliatePrograms.Write(e.Programs), e.AsOfUtc);
        }
        db.MarkProcessed(e, nameof(AffiliateProgramsChangedHandler));
        await db.SaveChangesAsync(ct);
    }
}
