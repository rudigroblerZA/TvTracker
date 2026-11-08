using TvTracker;
using TvTracker.EventBus.RabbitMQ;

namespace Catalog.API.Alerts;

/// <summary>
/// Fires while <c>tvtracker_dead_letters</c> holds anything: each letter is an event some service's handler failed four
/// times over a minute (or couldn't read at all), so a change that service never applied. Nothing heals one but a fix
/// and a replay (<c>/ops</c>), so a single letter is enough. Checked here because Catalog.API already counts the queue
/// for <c>/ops</c>; one service watching one shared queue fires once, not once per service.
/// </summary>
public sealed class DeadLettersAlert(RabbitMQEventBus bus) : IAlertCheck
{
    public string Name => "dead-letters";

    public async Task<AlertState> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return Evaluate(await bus.DeadLetterCountAsync(timeout.Token));
    }

    internal static AlertState Evaluate(uint letters) => letters == 0
        ? new AlertState(false, "No dead letters")
        : new AlertState(true, $"{letters} dead letter{(letters == 1 ? "" : "s")} waiting for a fix and a replay");
}
