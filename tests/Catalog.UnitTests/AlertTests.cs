using System.Diagnostics.Metrics;
using Catalog.API.Alerts;
using Catalog.SyncWorker.Alerts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TvTracker;
using TvTracker.Persistence;

namespace Catalog.UnitTests;

/// <summary>
/// The alerts' rules as CONTEXT.md states them (Alerts), and the monitor's: an alert is said once when it starts and once
/// when it stops, and a check that can't look leaves the alert as it was.
/// </summary>
public class AlertTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_outbox_fires_once_its_oldest_unpublished_event_is_five_minutes_old()
    {
        Assert.False(OutboxBacklogAlert<NoContext>.Evaluate(0, null, Now).Firing);
        Assert.False(OutboxBacklogAlert<NoContext>.Evaluate(40, Now.AddMinutes(-4), Now).Firing);
        var stuck = OutboxBacklogAlert<NoContext>.Evaluate(3, Now.AddMinutes(-12), Now);
        Assert.True(stuck.Firing);
        Assert.Equal("3 events unpublished, the oldest 12 min old", stuck.Detail);
    }

    [Fact]
    public void One_dead_letter_is_enough()
    {
        Assert.False(DeadLettersAlert.Evaluate(0).Firing);
        Assert.True(DeadLettersAlert.Evaluate(1).Firing);
    }

    [Fact]
    public void TVmaze_errors_fire_at_a_tenth_of_twenty_or_more_calls()
    {
        Assert.False(TvMazeErrorsAlert.Evaluate(calls: 10, errors: 10).Firing);
        Assert.False(TvMazeErrorsAlert.Evaluate(calls: 200, errors: 19).Firing);
        Assert.True(TvMazeErrorsAlert.Evaluate(calls: 200, errors: 20).Firing);
        Assert.True(TvMazeErrorsAlert.Evaluate(calls: 20, errors: 20).Firing);
    }

    [Fact]
    public async Task The_monitor_keeps_the_last_state_when_a_check_throws_and_resolves_when_it_clears()
    {
        var check = new ScriptedCheck();
        var monitor = Monitor(check);

        check.Next = new AlertState(true, "stuck");
        await monitor.CheckAllAsync(CancellationToken.None);
        Assert.Equal("stuck", monitor.Firing["scripted"]);

        check.Next = null;
        await monitor.CheckAllAsync(CancellationToken.None);
        Assert.True(monitor.Firing.ContainsKey("scripted"));

        check.Next = new AlertState(false, "clear");
        await monitor.CheckAllAsync(CancellationToken.None);
        Assert.Empty(monitor.Firing);
    }

    private static AlertMonitor Monitor(IAlertCheck check)
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new AlertMonitor([check], services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IMeterFactory>(),
            new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<AlertMonitor>.Instance);
    }

    /// <summary>
    /// Returns <see cref="Next"/>, or throws when it's null, as a check whose database is down does.
    /// </summary>
    private sealed class ScriptedCheck : IAlertCheck
    {
        public AlertState? Next { get; set; }
        public string Name => "scripted";

        public Task<AlertState> CheckAsync(IServiceProvider services, CancellationToken ct) =>
            Next is { } state ? Task.FromResult(state) : throw new InvalidOperationException("can't look");
    }

    private sealed class NoContext : Microsoft.EntityFrameworkCore.DbContext, IIntegrationEventLogDbContext
    {
        public Microsoft.EntityFrameworkCore.DbSet<IntegrationEventLogEntry> IntegrationEventLogs => Set<IntegrationEventLogEntry>();
        public Microsoft.EntityFrameworkCore.DbSet<ProcessedIntegrationEvent> ProcessedIntegrationEvents => Set<ProcessedIntegrationEvent>();
    }
}
