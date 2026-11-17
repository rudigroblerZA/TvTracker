using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TvTracker.Tests;

/// <summary>
/// Runs something once, just before a context's first save: how a test puts another writer between a module's own
/// check and its write, the moment a race happens, without relying on timing.
/// </summary>
public sealed class BeforeSave(Func<Task> interleave) : SaveChangesInterceptor
{
    private bool done;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!done)
        {
            done = true;
            await interleave();
        }
        return result;
    }
}
