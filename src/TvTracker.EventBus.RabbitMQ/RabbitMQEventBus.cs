using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TvTracker.EventBus.RabbitMQ;

public sealed class RabbitMQEventBusOptions
{
    /// <summary>Name of the connection string Aspire injects (the RabbitMQ resource name).</summary>
    public string ConnectionName { get; set; } = "eventbus";

    /// <summary>Queue for this service. Each service has its own queue bound to the events it subscribes to.</summary>
    public string SubscriptionClientName { get; set; } = "";

    public ushort PrefetchCount { get; set; } = 10;
}

/// <summary>
/// Publishes events to a direct exchange (routing key = event type name) and consumes this service's queue.
/// Failed messages are requeued once; a message that fails again is dropped and logged.
/// </summary>
public sealed class RabbitMQEventBus(
    ILogger<RabbitMQEventBus> logger,
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IOptions<RabbitMQEventBusOptions> options,
    IOptions<EventBusSubscriptionInfo> subscriptionInfo) : IEventBus, IHostedService, IAsyncDisposable
{
    public const string ExchangeName = "tvtracker_event_bus";

    internal static readonly ActivitySource ActivitySource = new("TvTracker.EventBus");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private IConnection? _connection;
    private IChannel? _consumerChannel;
    private Task? _consumerTask;

    /// <summary>True once this service's queue is declared, bound and being consumed (or it has no subscriptions).</summary>
    public bool IsReady => subscriptionInfo.Value.EventTypes.Count == 0 || _consumerChannel is { IsOpen: true };

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        var routingKey = EventName.For(@event.GetType());
        using var activity = ActivitySource.StartActivity($"publish {routingKey}", ActivityKind.Producer);

        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);

        var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), JsonOptions);
        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = @event.Id.ToString(),
            Type = routingKey,
            ContentType = "application/json"
        };

        await channel.BasicPublishAsync(ExchangeName, routingKey, mandatory: false, basicProperties: properties, body: body, cancellationToken: cancellationToken);

        logger.LogDebug("Published {EventName} {EventId}", routingKey, @event.Id);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (subscriptionInfo.Value.EventTypes.Count == 0)
        {
            logger.LogInformation("No event subscriptions registered; not consuming");
            return Task.CompletedTask;
        }

        // RabbitMQ may still be starting; keep retrying in the background so the service can start.
        _consumerTask = Task.Run(() => StartConsumingWithRetryAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_consumerTask is not null)
        {
            try { await _consumerTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task StartConsumingWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StartConsumingAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not start consuming from RabbitMQ; retrying in {Delay}", delay);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        var queueName = QueueName();
        var connection = await GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);

        foreach (var eventName in subscriptionInfo.Value.EventTypes.Keys)
        {
            await channel.QueueBindAsync(queueName, ExchangeName, eventName, cancellationToken: cancellationToken);
        }

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: options.Value.PrefetchCount, global: false, cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);

        _consumerChannel = channel;
        logger.LogInformation("Consuming queue {Queue} for {Count} event types", queueName, subscriptionInfo.Value.EventTypes.Count);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        var channel = _consumerChannel ?? ((AsyncEventingBasicConsumer)sender).Channel;
        var eventName = args.RoutingKey;
        using var activity = ActivitySource.StartActivity($"process {eventName}", ActivityKind.Consumer);

        try
        {
            if (subscriptionInfo.Value.EventTypes.TryGetValue(eventName, out var eventType))
            {
                var @event = (IntegrationEvent?)JsonSerializer.Deserialize(args.Body.Span, eventType, JsonOptions)
                    ?? throw new InvalidOperationException($"Could not deserialize {eventName}");

                await using var scope = serviceProvider.CreateAsyncScope();
                foreach (var handler in scope.ServiceProvider.GetKeyedServices<IIntegrationEventHandler>(eventType))
                {
                    await handler.HandleAsync(@event, _stopping.Token);
                }
            }
            else
            {
                logger.LogWarning("No subscription for {EventName}", eventName);
            }

            await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var requeue = !args.Redelivered;
            logger.LogError(ex, "Error handling {EventName}; requeue: {Requeue}", eventName, requeue);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: requeue);
        }
    }

    private string QueueName()
    {
        var name = options.Value.SubscriptionClientName;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("RabbitMQEventBusOptions.SubscriptionClientName must be set.");
        }
        return name;
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var connectionString = configuration.GetConnectionString(options.Value.ConnectionName)
                ?? throw new InvalidOperationException($"Missing connection string '{options.Value.ConnectionName}'.");

            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Dispose();
        if (_consumerChannel is not null)
        {
            await _consumerChannel.DisposeAsync();
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        _connectionLock.Dispose();
    }
}
