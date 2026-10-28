using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;

namespace Research.API.IntegrationEvents;

/// <summary>
/// A deleted account's name comes off the research versions they saved. The research itself stays: it's the
/// catalog's record, not the moderator's. Idempotent, so it needs no inbox entry.
/// </summary>
public sealed class UserDeletedHandler(ResearchContext db) : IIntegrationEventHandler<UserDeletedIntegrationEvent>
{
    public Task HandleAsync(UserDeletedIntegrationEvent e, CancellationToken ct) =>
        db.Saved.Where(s => s.SavedById == e.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SavedById, (string?)null).SetProperty(x => x.SavedByName, "a former moderator"), ct);
}
