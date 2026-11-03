using System.Collections.Concurrent;
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
    /// <summary>
    /// Name of the connection string Aspire injects (the RabbitMQ resource name).
    /// </summary>
    public string ConnectionName { get; set; } = "eventbus";

    /// <summary>
    /// Queue for this service. Each service has its own queue bound to the events it subscribes to.
    /// </summary>
    public string SubscriptionClientName { get; set; } = "";

    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// How long a failed message waits before each retry; after the last it goes to the dead-letter queue. Each delay is
    /// a queue of its own (<c>{queue}.retry.{delay}</c>), so changing one declares a new queue rather than altering one.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)];
}

/// <summary>
/// Publishes events to a direct exchange (routing key = event type name) and consumes this service's queue.
/// <list type="bullet">
/// <item><b>Delayed retries:</b> a message whose handler throws is acked and republished to a retry queue
/// (<see cref="RabbitMQEventBusOptions.RetryDelays"/>: 1 s, 10 s, 60 s) whose TTL dead-letters it back to this
/// service's queue alone, counting attempts in <see cref="AttemptHeader"/>. Before, it was requeued once at once and then
/// dropped, so a database hiccup of a few hundred milliseconds could lose an event for good.</item>
/// <item><b>Dead letters:</b> after the last retry, or at once when it can't be read, a message goes to
/// <see cref="DeadLetterQueue"/>, one queue shared by every service, with the queue it came from
/// (<see cref="QueueHeader"/>) and the error. <see cref="ReplayDeadLettersAsync"/> sends them back to that queue only,
/// so other subscribers don't see the event twice.</item>
/// <item><b>Order:</b> a retried event arrives after later ones. Handlers already cope (the inbox, and newest-wins
/// copies such as Social's shared lists), since redeliveries were never ordered either.</item>
/// </list>
/// </summary>
public sealed class RabbitMQEventBus(
    ILogger<RabbitMQEventBus> logger,
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IOptions<RabbitMQEventBusOptions> options,
    IOptions<EventBusSubscriptionInfo> subscriptionInfo) : IEventBus, IHostedService, IAsyncDisposable
{
    public const string ExchangeName = "tvtracker_event_bus";

    /// <summary>
    /// The queue every service's undeliverable messages end up in.
    /// </summary>
    public const string DeadLetterQueue = "tvtracker_dead_letters";

    /// <summary>
    /// Retries so far, on a message in or back from a retry queue.
    /// </summary>
    public const string AttemptHeader = "x-tvtracker-attempt";

    /// <summary>
    /// On a dead letter: the queue it was taken from, which a replay sends it back to.
    /// </summary>
    public const string QueueHeader = "x-tvtracker-queue";

    /// <summary>
    /// On a dead letter: the last error, cut to <see cref="MaxErrorLength"/> characters.
    /// </summary>
    public const string ErrorHeader = "x-tvtracker-error";

    private const int MaxErrorLength = 1000;

    internal static readonly ActivitySource ActivitySource = new("TvTracker.EventBus");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Publishing channels kept open between publishes, at most this many.
    /// </summary>
    private const int MaxIdlePublishChannels = 8;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ConcurrentBag<IChannel> _publishChannels = [];
    private IConnection? _exchangeDeclaredOn;
    private readonly CancellationTokenSource _stopping = new();
    private IConnection? _connection;
    private IChannel? _consumerChannel;
    private Task? _consumerTask;

    /// <summary>
    /// True once this service's queue is declared, bound and being consumed (or it has no subscriptions).
    /// </summary>
    public bool IsReady => subscriptionInfo.Value.EventTypes.Count == 0 || _consumerChannel is { IsOpen: true };

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        var routingKey = EventName.For(@event.GetType());
        using var activity = ActivitySource.StartActivity($"publish {routingKey}", ActivityKind.Producer);

        // Opening a channel per publish and declaring the exchange each time cost three round trips per event inside
        // every write request, so channels come from a small pool (one publish at a time each) and the exchange is
        // declared once per connection.
        var connection = await GetConnectionAsync(cancellationToken);
        var channel = await RentPublishChannelAsync(connection, cancellationToken);

        var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), JsonOptions);
        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = @event.Id.ToString(),
            Type = routingKey,
            ContentType = "application/json"
        };

        try
        {
            await channel.BasicPublishAsync(ExchangeName, routingKey, mandatory: false, basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
        await ReturnPublishChannelAsync(channel);

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
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var delay in options.Value.RetryDelays)
        {
            // A message waits out the queue's TTL, then the default exchange routes it back to this service's queue only.
            await channel.QueueDeclareAsync(RetryQueueName(queueName, delay), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = (int)delay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = queueName
                }, cancellationToken: cancellationToken);
        }

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
        // A message back from a retry queue carries the queue's name as its routing key, so the event's name is read from
        // its type (set on every publish), and from the routing key for anything published before that.
        var eventName = string.IsNullOrEmpty(args.BasicProperties.Type) ? args.RoutingKey : args.BasicProperties.Type;
        var attempt = ReadInt(args.BasicProperties.Headers, AttemptHeader);
        using var activity = ActivitySource.StartActivity($"process {eventName}", ActivityKind.Consumer);

        IntegrationEvent? @event = null;
        try
        {
            if (subscriptionInfo.Value.EventTypes.TryGetValue(eventName, out var eventType))
            {
                try
                {
                    @event = (IntegrationEvent?)JsonSerializer.Deserialize(args.Body.Span, eventType, JsonOptions);
                }
                catch (JsonException) { }

                if (@event is null)
                {
                    // Unreadable now means unreadable on every retry.
                    await ForwardAsync(args, eventName, DeadLetterQueue, attempt, $"Could not deserialize {eventName}");
                    logger.LogError("Could not deserialize {EventName} {MessageId}; sent to {DeadLetters}", eventName,
                        args.BasicProperties.MessageId, DeadLetterQueue);
                    await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
                    return;
                }

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
        catch (Exception ex) when (!_stopping.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var delays = options.Value.RetryDelays;
            try
            {
                if (attempt < delays.Length)
                {
                    var next = RetryQueueName(QueueName(), delays[attempt]);
                    await ForwardAsync(args, eventName, next, attempt + 1, error: null);
                    logger.LogWarning(ex, "Error handling {EventName} {EventId} (attempt {Attempt}); retrying in {Delay}",
                        eventName, @event?.Id, attempt + 1, delays[attempt]);
                }
                else
                {
                    await ForwardAsync(args, eventName, DeadLetterQueue, attempt, ex.ToString());
                    logger.LogError(ex, "Error handling {EventName} {EventId} after {Attempts} attempts; sent to {DeadLetters}",
                        eventName, @event?.Id, attempt + 1, DeadLetterQueue);
                }
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
            }
            catch (Exception forwardError)
            {
                // Couldn't park it: fall back to RabbitMQ's own redelivery rather than lose it at once.
                var requeue = !args.Redelivered;
                logger.LogError(forwardError, "Couldn't forward failed {EventName}; requeue: {Requeue}", eventName, requeue);
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: requeue);
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested)
        {
            // Shutting down mid-handler: put it back for the next start, without counting an attempt.
            await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true);
        }
    }

    /// <summary>
    /// Republishes a received message to <paramref name="queue"/> through the default exchange (so only that queue gets it),
    /// with its body, id and type, and the attempt count; a dead letter also gets the queue it came from and the error.
    /// </summary>
    private async Task ForwardAsync(BasicDeliverEventArgs args, string eventName, string queue, int attempt, string? error)
    {
        var headers = args.BasicProperties.Headers is { } existing ? new Dictionary<string, object?>(existing) : [];
        headers[AttemptHeader] = attempt;
        if (queue == DeadLetterQueue)
        {
            headers[QueueHeader] = QueueName();
            headers[ErrorHeader] = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
        }
        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = args.BasicProperties.MessageId,
            Type = eventName,
            ContentType = args.BasicProperties.ContentType ?? "application/json",
            Headers = headers
        };
        await PublishRawAsync(queue, properties, args.Body, CancellationToken.None);
    }

    private async Task PublishRawAsync(string queue, BasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var channel = await RentPublishChannelAsync(connection, cancellationToken);
        try
        {
            await channel.BasicPublishAsync("", queue, mandatory: false, basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
        await ReturnPublishChannelAsync(channel);
    }

    /// <summary>
    /// Messages waiting in the shared dead-letter queue (0 before any service has declared it).
    /// </summary>
    public async Task<uint> DeadLetterCountAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            return (await channel.QueueDeclarePassiveAsync(DeadLetterQueue, cancellationToken)).MessageCount;
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0; // not declared yet: nothing has failed
        }
    }

    /// <summary>
    /// Sends up to <paramref name="max"/> dead letters back to the queue each came from, with a fresh attempt count, and
    /// says how many went to each. A letter without its queue stays where it is.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> ReplayDeadLettersAsync(int max, CancellationToken cancellationToken = default)
    {
        var replayed = new Dictionary<string, int>();
        var kept = new List<ulong>();
        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            await channel.QueueDeclarePassiveAsync(DeadLetterQueue, cancellationToken);
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return replayed;
        }

        for (var i = 0; i < max; i++)
        {
            var letter = await channel.BasicGetAsync(DeadLetterQueue, autoAck: false, cancellationToken);
            if (letter is null)
            {
                break;
            }
            if (ReadString(letter.BasicProperties.Headers, QueueHeader) is not { Length: > 0 } queue)
            {
                kept.Add(letter.DeliveryTag);
                continue;
            }

            var headers = letter.BasicProperties.Headers is { } existing ? new Dictionary<string, object?>(existing) : [];
            headers.Remove(AttemptHeader);
            headers.Remove(QueueHeader);
            headers.Remove(ErrorHeader);
            var properties = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = letter.BasicProperties.MessageId,
                Type = letter.BasicProperties.Type,
                ContentType = letter.BasicProperties.ContentType,
                Headers = headers
            };
            await PublishRawAsync(queue, properties, letter.Body, cancellationToken);
            await channel.BasicAckAsync(letter.DeliveryTag, multiple: false, cancellationToken);
            replayed[queue] = replayed.GetValueOrDefault(queue) + 1;
        }

        // Released only now, so the loop doesn't take the same unroutable letter again.
        foreach (var tag in kept)
        {
            await channel.BasicNackAsync(tag, multiple: false, requeue: true, cancellationToken);
        }
        logger.LogInformation("Replayed dead letters: {Replayed}", replayed);
        return replayed;
    }

    internal static string RetryQueueName(string queue, TimeSpan delay) =>
        $"{queue}.retry.{(delay.TotalSeconds >= 1 && delay.TotalSeconds % 1 == 0 ? $"{delay.TotalSeconds:0}s" : $"{delay.TotalMilliseconds:0}ms")}";

    private static int ReadInt(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                byte b => b,
                byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => 0
            }
            : 0;

    private static string? ReadString(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null
            }
            : null;

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

    private async Task<IChannel> RentPublishChannelAsync(IConnection connection, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_exchangeDeclaredOn, connection))
        {
            await using var declare = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await declare.ExchangeDeclareAsync(ExchangeName, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
            _exchangeDeclaredOn = connection;
        }

        while (_publishChannels.TryTake(out var pooled))
        {
            if (pooled.IsOpen)
            {
                return pooled;
            }
            await pooled.DisposeAsync();
        }
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken);
    }

    private async ValueTask ReturnPublishChannelAsync(IChannel channel)
    {
        if (channel.IsOpen && _publishChannels.Count < MaxIdlePublishChannels)
        {
            _publishChannels.Add(channel);
            return;
        }
        await channel.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Dispose();
        while (_publishChannels.TryTake(out var channel))
        {
            await channel.DisposeAsync();
        }
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
