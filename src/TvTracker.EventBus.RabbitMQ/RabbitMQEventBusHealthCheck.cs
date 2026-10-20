using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TvTracker.EventBus.RabbitMQ;

/// <summary>
/// Unhealthy until the service's queue is bound and consuming. The AppHost starts the catalog sync worker
/// only after the consuming services are healthy, so their queues exist before the first events are published.
/// </summary>
public sealed class RabbitMQEventBusHealthCheck(RabbitMQEventBus eventBus) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(eventBus.IsReady
            ? HealthCheckResult.Healthy("Consuming")
            : HealthCheckResult.Unhealthy("Not consuming from RabbitMQ yet"));
}
