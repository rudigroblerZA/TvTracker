using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TvTracker;

/// <param name="Firing">Whether the condition the alert watches for holds now.</param>
/// <param name="Detail">What was found, in a sentence ("3 events unpublished, the oldest 12 minutes"), firing or not.</param>
public sealed record AlertState(bool Firing, string Detail);

/// <summary>
/// One condition someone should hear about: an outbox that stopped publishing, dead letters, a failed
/// insights run, TVmaze errors, Ask's queue. A check only looks; <see cref="AlertMonitor"/> runs it and says so.
/// </summary>
public interface IAlertCheck
{
    /// <summary>
    /// A stable slug ("outbox-backlog"): the metric's <c>alert</c> tag and the log's <c>Alert</c> property, which alert
    /// rules match on, so it never changes once deployed.
    /// </summary>
    string Name { get; }

    Task<AlertState> CheckAsync(CancellationToken ct);
}

/// <summary>
/// Runs a service's <see cref="IAlertCheck"/>s every <c>Alerts:Interval</c> (a minute; the first after
/// <c>Alerts:FirstCheck</c>, two minutes, so a start's own catching up doesn't fire anything) and makes each one visible
/// three ways, so whatever watches the deployment can alert on it:
/// <list type="bullet">
/// <item><b>A gauge,</b> <c>tvtracker.alerts.firing</c> (1 or 0, tagged <c>alert</c>) on the <c>TvTracker.Alerts</c> meter,
/// which ServiceDefaults exports with every <c>TvTracker.*</c> meter: a metric alert rule is one threshold.</item>
/// <item><b>A log line on each change:</b> an error "Alert {Alert} firing: {Detail}" when it starts, information "Alert
/// {Alert} resolved" when it stops, so a log alert or a person reading the logs sees it once, not every minute.</item>
/// <item><b>Health, Degraded:</b> the service's health check "alerts" names the firing ones, so the Aspire dashboard
/// shows them. Degraded still answers 200, so a firing alert never takes a service out of rotation: an outbox backlog
/// needs a person, not a restart.</item>
/// </list>
/// A check that throws is logged and leaves its last state as it was: not being able to look isn't the same as finding
/// the condition, and a flapping database would otherwise fire and resolve every alert in turn.
/// </summary>
public sealed class AlertMonitor : BackgroundService
{
    public const string MeterName = "TvTracker.Alerts";

    private readonly IReadOnlyList<IAlertCheck> checks;
    private readonly TimeProvider time;
    private readonly ILogger<AlertMonitor> logger;
    private readonly TimeSpan interval;
    private readonly TimeSpan firstCheck;
    private readonly ConcurrentDictionary<string, AlertState> states = new();

    public AlertMonitor(IEnumerable<IAlertCheck> checks, IMeterFactory meters, IConfiguration config, TimeProvider time,
        ILogger<AlertMonitor> logger)
    {
        this.checks = checks.ToList();
        this.time = time;
        this.logger = logger;
        interval = config.GetValue("Alerts:Interval", TimeSpan.FromMinutes(1));
        firstCheck = config.GetValue("Alerts:FirstCheck", TimeSpan.FromMinutes(2));
        meters.Create(MeterName).CreateObservableGauge("tvtracker.alerts.firing",
            () => this.checks.Select(c => new Measurement<int>(states.TryGetValue(c.Name, out var s) && s.Firing ? 1 : 0,
                new KeyValuePair<string, object?>("alert", c.Name))),
            description: "1 while the alert's condition holds, else 0");
    }

    /// <summary>
    /// The alerts firing now, with what each found.
    /// </summary>
    public IReadOnlyDictionary<string, string> Firing =>
        states.Where(s => s.Value.Firing).ToDictionary(s => s.Key, s => s.Value.Detail);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(firstCheck, time, stoppingToken);
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            await CheckAllAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var check in checks)
        {
            AlertState now;
            try
            {
                now = await check.CheckAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Alert {Alert} couldn't be checked; it stays as it was", check.Name);
                continue;
            }

            var was = states.TryGetValue(check.Name, out var before) && before.Firing;
            states[check.Name] = now;
            if (now.Firing && !was)
            {
                logger.LogError("Alert {Alert} firing: {Detail}", check.Name, now.Detail);
            }
            else if (!now.Firing && was)
            {
                logger.LogInformation("Alert {Alert} resolved: {Detail}", check.Name, now.Detail);
            }
        }
    }
}

/// <summary>
/// Degraded, naming them, while any alert fires; healthy otherwise.
/// </summary>
public sealed class AlertsHealthCheck(AlertMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var firing = monitor.Firing;
        return Task.FromResult(firing.Count == 0
            ? HealthCheckResult.Healthy("No alerts firing")
            : HealthCheckResult.Degraded($"Alerts firing: {string.Join("; ", firing.Select(f => $"{f.Key} ({f.Value})"))}",
                data: firing.ToDictionary(f => f.Key, f => (object)f.Value)));
    }
}
