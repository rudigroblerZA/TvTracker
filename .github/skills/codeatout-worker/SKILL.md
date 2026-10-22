---
name: codeatout-worker
description: Implements reliable background workers and message handlers without assuming a particular worker or messaging framework. Use when changing asynchronous processing, scheduled work, or event handling.
---

# Background Processing

## Discover first

Inspect the host lifecycle, queue or scheduler, delivery guarantees,
retry/dead-letter policy, idempotency strategy, dependency scopes, and
observability conventions.

Apply `codeatout-nservicebus` when the repository already uses NServiceBus;
that skill adds endpoint, Outbox, transport, pipeline, and `SendLocal`
guidance.

## Principles

- Honor cancellation and graceful shutdown.
- Treat messages and scheduled input as untrusted; validate before processing.
- Design handlers to be idempotent when duplicate delivery is possible.
- Use bounded retries with clear classification of transient versus permanent
  failure. Preserve enough safe context for diagnosis.
- Authorize sensitive work at the appropriate boundary and keep credentials in
  approved configuration or secret stores.
- Emit operational telemetry without exposing message payloads or personal data.

## Validate

Test success, cancellation, retryable failure, permanent failure, duplicate
delivery, and shutdown behaviour appropriate to the host.

Create or update focused automated tests in the same change as every worker,
handler, scheduling, retry, or telemetry behaviour change. Include a
regression test for the scenario that motivated a defect fix.

## Reference handler pattern

For a message-driven host, map the message into the application's write model
and let the host's configured error policy decide whether to retry:

```csharp
public sealed class RebuildItemProjectionHandler(IItemProjectionWriter writer)
{
    public async Task HandleAsync(
        RebuildItemProjection message,
        CancellationToken cancellationToken)
    {
        var projection = new ItemProjection(
            message.ItemId,
            message.ChangedAt,
            message.Status);

        await writer.UpsertAsync(projection, cancellationToken);
    }
}
```

Use the handler context supplied by the host for follow-up messages so
correlation, transactions, and delivery semantics are preserved. Do not add a
second transport client when the host already provides one.
