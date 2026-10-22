---
name: codeatout-nservicebus
description: Implements or changes NServiceBus endpoints, message handling, reliability, and tests. Use when a repository already adopts NServiceBus and its messaging configuration or handlers are affected.
---

# NServiceBus

## Applicability and discovery

Use this skill only in repositories that already use NServiceBus. Before
changing it, discover the established endpoint role, transport, persistence,
Outbox, recoverability, conventions, dependency-registration approach, and
operational ownership. Inspect existing message contracts, handlers, pipeline
behaviors, tests, logging, tracing, and error-queue procedures.

Do not introduce, replace, or upgrade messaging infrastructure as an incidental
feature change. Use `NServiceBus` and `NServiceBus.Extensions.Hosting` only
where already adopted. Likewise, use `NServiceBus.Transport.AzureServiceBus`,
`NServiceBus.Persistence.CosmosDB`, `NServiceBus.FluentValidation`,
`NServiceBus.Serilog`, and `NServiceBus.UniformSession` only when the
repository already adopts the corresponding integration and its prerequisites.
Follow its established package and configuration conventions; do not specify
fixed package versions.

State sagas are excluded from this skill. Address them only when independently
required, with explicit lifecycle, correlation, timeout, concurrency, and
recovery design.

## Endpoint and delivery choices

- Preserve the existing endpoint identity and topology conventions. Configuration
  belongs at the host composition boundary, not in message handlers.
- Make an endpoint send-only only when it must not receive messages. A
  send-only endpoint has no input queue and cannot host handlers; configure a
  receiver when it consumes commands, events, replies, or timeouts.
- Reuse the configured transport and persistence. Transport addresses,
  connection settings, and credentials must come from approved configuration
  and secret stores, never source or logs.
- Enable and retain the Outbox where the endpoint's delivery guarantees require
  atomic persistence with outgoing operations. The Outbox does not make
  arbitrary external side effects atomic; make those operations idempotent or
  use the repository's proven integration pattern.
- Preserve recoverability policy. Classify transient failures for bounded retry
  and route exhausted or non-retryable failures to the established error
  handling flow. Do not swallow exceptions merely to avoid retries.
- Change custom pipeline behavior only with a clear ordering, scope, and
  failure-semantics reason. Behaviors must be small, deterministic, safe on
  retries, and must not bypass validation, authorization, Outbox, or
  recoverability.

## Contracts and handlers

Keep contracts stable, explicit, and independently serializable. Prefer
additive, backward-compatible changes; coordinate removal or semantic changes
with all publishers and consumers. Do not put secrets, credentials, or
unnecessary personal or financial data in messages.

- A command expresses one intended action and normally has one logical owner.
  Send it to that owner; do not publish it as a broadcast substitute.
- An event records a completed fact and may have many subscribers. Publish it
  only after the fact is true according to the surrounding consistency model.
- Use `SendLocal` only to enqueue a command for another handler on the same
  receiving endpoint. It remains asynchronous, serialized, and subject to the
  endpoint's pipeline, Outbox, and recoverability; it is not a direct handler
  call. A send-only endpoint cannot use `SendLocal` because it has no input
  queue.
- Use `Send` when routing a command to its owning endpoint, `Publish` for a
  completed fact with potential subscribers, and `Reply` only to respond to a
  request/reply interaction the repository already supports. Do not use
  `SendLocal` to conceal cross-endpoint ownership or as a substitute for a
  simple in-process method call.
- Validate untrusted message data at the handler boundary. Use
  `NServiceBus.FluentValidation` only where it is already the repository's
  validation integration.
- Handlers must tolerate at-least-once delivery. Use stable business keys,
  idempotent writes, and durable deduplication where the operation cannot be
  safely repeated.
- Use `IMessageHandlerContext` for sends, publishes, replies, headers, and
  cancellation. Do not create a second endpoint or transport client inside a
  handler. Honor `context.CancellationToken` for cancellable work and do not
  start detached work that outlives message processing.

```csharp
public sealed record SubmitWork(Guid WorkId) : ICommand;
public sealed record WorkSubmitted(Guid WorkId) : IEvent;

public sealed class SubmitWorkHandler(IWorkStore store)
    : IHandleMessages<SubmitWork>
{
    public async Task Handle(SubmitWork message, IMessageHandlerContext context)
    {
        await store.RecordOnceAsync(message.WorkId, context.CancellationToken);
        await context.Publish(
            new WorkSubmitted(message.WorkId),
            context.CancellationToken);
    }
}
```

### Local queued work

Use local dispatch when a receiving endpoint intentionally splits work across
handlers and needs NServiceBus delivery guarantees between those steps. Keep
the local command independently idempotent because it can be retried or
redelivered.

```csharp
public sealed record ValidateWork(Guid WorkId) : ICommand;
public sealed record ProcessValidatedWork(Guid WorkId) : ICommand;

public sealed class ValidateWorkHandler : IHandleMessages<ValidateWork>
{
    public async Task Handle(ValidateWork message, IMessageHandlerContext context)
    {
        await context.SendLocal(
            new ProcessValidatedWork(message.WorkId),
            context.CancellationToken);
    }
}
```

## Correlation and observability

Use NServiceBus's existing correlation and trace propagation rather than
inventing parallel identifiers. Preserve message and conversation headers when
forwarding work; set custom headers only when they have a documented,
bounded, non-sensitive purpose. Treat header names and message schemas as
observable compatibility contracts.

Use existing structured logging and telemetry conventions. `NServiceBus.Serilog`
is conditional on an existing repository adoption. Log safe operational
context such as message type, endpoint role, message identifier, correlation
identifier, retry attempt, and failure classification when allowed by local
policy. Never log raw message bodies, headers wholesale, credentials, tokens,
connection data, or restricted and confidential values.

### ServiceControl and ServicePulse metrics

When the repository already reports NServiceBus metrics to ServiceControl for
ServicePulse, preserve that operational integration for every intended
receiving endpoint. Before changing it, discover the endpoint roles, the
existing metrics package/version convention, the typed configuration model,
host settings, deployment configuration, and the release-time source for the
metrics address.

- Add `NServiceBus.Metrics.ServiceControl` only when the integration is
  required. Register it through the repository's central package-management
  convention and reference it from the project that owns endpoint
  configuration. Do not select or upgrade a package version incidentally.
- Keep the metrics address in typed endpoint configuration. Provide an empty
  non-secret default for local configuration when that matches the repository
  convention; supply the deployed value through its approved configuration and
  secret/value pipeline. Never hard-code addresses, connection strings, or
  environment names.
- Configure metrics at the host composition boundary, alongside transport,
  tracing, recoverability, and endpoint identity. When an endpoint factory
  creates both receiving and send-only endpoints, configure metrics only for
  the receiving role unless the deployment deliberately provides the address
  and requires send-only metrics too. Do not emit a missing-address warning
  for an endpoint intentionally excluded from monitoring.
- For a receiving endpoint with no configured address, follow the repository's
  operational policy: normally log one safe, actionable warning and leave
  metrics disabled rather than failing startup. Do not hide a required
  deployment configuration failure if the repository normally validates such
  settings at startup.
- Use `EnableMetrics()` with
  `SendMetricDataToServiceControl(...)`. Follow the repository's reporting
  interval; when no convention exists, use the established ten-second
  interval. Set the ServiceControl instance ID to the actual endpoint name
  selected for the running role, not the machine name or a different host's
  endpoint name, so ServicePulse groups metrics correctly.
- Treat custom checks as a separate ServiceControl capability. Configure
  `NServiceBus.CustomChecks` and its queue only when the repository already
  uses it or the requirement explicitly includes it; adding metrics must not
  silently introduce custom-check topology.
- Trace configuration end to end for each monitored host: typed binding,
  application settings, environment-variable mapping where applicable, chart
  or deployment template, values/overrides, and release variable or secret
  source. A setting present only in code or only in a deployment template does
  not enable metrics.

#### Helm and YAML deployment configuration

When the repository deploys endpoints with Helm, update every layer required
for each monitored receiving host. Do not copy a metrics setting into
send-only charts unless they are intentionally monitored.

1. Add a `metricsAddress` value beneath the chart's existing service-bus
   values. Keep the chart default empty when the address is environment
   supplied; do not embed an environment address in `values.yaml`.
2. In the host ConfigMap or equivalent pod environment template, map that
   value to the hierarchical .NET configuration key, for example
   `ServiceBus__MetricsAddress`. Use the repository's established `required`
   helper when the deployed receiving endpoint must have metrics.
3. Add the matching value to every environment override or token-replacement
   YAML file used to deploy that host. Use the pipeline's existing variable or
   secret-token convention; do not invent a second substitution mechanism.
4. Ensure the deployment pipeline defines or imports the corresponding
   environment variable/secret for every target environment. A release
   variable that no override consumes, or an override token with no supplied
   value, leaves metrics disabled or makes the chart render fail.
5. Check chart naming and host boundaries carefully. A shared endpoint factory
   does not imply that every chart hosts the same endpoint role. Wire the
   address into the worker, listener, or other receiving-host chart that owns
   message handling; add it to API or proxy charts only when those send-only
   endpoints are explicitly in scope for monitoring.

Use the chart's established rendering or lint command with representative
non-secret values. Verify the rendered ConfigMap/environment contains the
expected configuration key, its value flows into the receiving container, and
the selected endpoint identity matches the metrics instance ID.
- Validate a production-equivalent build because monitoring may be compiled or
  registered conditionally. Run focused endpoint tests where stable public
  configuration APIs permit meaningful assertions, and render or lint the
  deployment configuration with required values. Confirm that the resulting
  receiving endpoint gets an address and send-only endpoints are intentionally
  included or excluded.

## Testing and validation

Use `NServiceBus.Testing` only where it is already used or approved by the
repository. Test handler behavior without a live transport: assert outgoing
commands or events, absence of unexpected operations, and the resulting
collaborator interactions. Match the installed package's testing API.

```csharp
[Fact]
public async Task Submitting_work_publishes_completion()
{
    var handler = new SubmitWorkHandler(new InMemoryWorkStore());

    await Test.Handler(handler)
        .ExpectPublish<WorkSubmitted>(message => message.WorkId != Guid.Empty)
        .OnMessage(new SubmitWork(Guid.NewGuid()));
}
```

Validate the changed endpoint role and configuration, contract compatibility,
validation failure, duplicate delivery, cancellation, transient retry,
unrecoverable failure routing, Outbox behavior where enabled, custom pipeline
ordering, correlation propagation, and safe logging. Run the repository's
targeted build and test commands and confirm no credentials or sensitive
payloads are introduced.
