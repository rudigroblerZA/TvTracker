---
name: codeatout-telemetry-buffering
description: Investigates and implements bounded Serilog and OpenTelemetry buffering in .NET applications. Use when telemetry increases memory usage, containers are OOM killed, exporters cannot keep up, or log and trace queues need explicit limits.
---

# Serilog and OpenTelemetry Buffering

## Discover first

Inspect the consuming repository's runtime, resolved package versions, host
startup, Serilog sinks, OpenTelemetry SDK or auto-instrumentation, configuration
providers, deployment manifests, and existing tests. Apply this skill only to
the technologies the application actually uses.

Map each independent pipeline: Serilog logs, Serilog-generated spans where
supported, SDK logs, SDK traces, and metric aggregation. Record queue and batch
limits, export intervals, request timeouts, retry budgets, signal ownership,
and whether the settings reach the running process.

Verify defaults and supported configuration against documentation or source
for the installed versions. Do not infer JSON or environment-variable support
from an options property's existence. See the
[configuration reference](references/configuration.md) for the settings and
version-specific pitfalls to check.

## Establish the memory and loss budget

- Distinguish a larger baseline, managed-heap growth, native memory, retained
  queues, metric cardinality, and temporary serialization allocations. Memory
  graphs and restarts alone do not prove an exporter leak.
- Compare managed heap, container memory, GC activity, event rate, exporter
  latency, failures, and dropped telemetry through representative traffic.
  Verify OOM termination reasons rather than treating every restart as an OOM.
- Select limits from measured throughput, typical and large event sizes,
  acceptable outage retention, and available memory. Document assumptions when
  measurements are unavailable; examples are not organisation-wide defaults.
- Queue limits usually count events or spans, not bytes. Account for queued
  objects, active batches, serialization buffers, retries, and independent
  exporters. A bounded queue is not a bound on total process memory.
- Agree the full-queue policy: drop, block, or durable buffering. Smaller
  in-memory queues intentionally lose telemetry sooner during an outage.
  Required audit records need an approved durable delivery path, not a
  best-effort diagnostic sink.

## Implement the smallest supported change

Use existing application configuration and deployment conventions. Apply
`codeatout-dotnet-configuration` for binding and validation. Keep exporters at
the application composition boundary and preserve endpoint, authentication,
resource identity, log filters, minimum levels, correlation, and redaction.

For Serilog:

- Configure the sink's own bounded queue, batch size, buffering interval, and
  retry budget using the installed version's supported API.
- Prefer native configuration. If JSON cannot express the options callback,
  use programmatic registration or a small shared configuration adapter where
  the repository's existing sharing model supports it. Prove the limitation
  before adding an adapter; do not impose a new shared project or framework.
- Fail startup for invalid owned settings. Reject an unbounded queue when a
  bounded queue is required, non-positive capacities, batches larger than the
  queue, and invalid timing values.
- Do not add an async wrapper around an already-batched sink just to reduce
  memory. It can introduce another queue, shutdown path, and loss policy.
- Treat retry duration and individual network-request timeout separately.
  Avoid blocking the application path on a remote diagnostic exporter.

For OpenTelemetry:

- Configure the SDK trace and log processors independently. `OTEL_BSP_*`
  controls SDK span batching; `OTEL_BLRP_*` controls SDK log batching. Neither
  automatically controls a Serilog OTLP sink.
- Sampling applies to traces, not ordinary logs or metric aggregation.
  Parent-based sampling honours upstream decisions and is not a global cap.
  Completed-span queue limits do not bound active spans.
- Check for duplicate log collection before disabling a pipeline. With
  auto-instrumentation, disable automatic SDK logs only after confirming the
  retained Serilog path covers the required logs and the change is approved.
  Preserve tracing and metrics unless a separate change is requested.
- Treat metrics as aggregation state, not an event queue. Reduce unbounded
  dimensions and unnecessary instruments, and use supported cardinality limits
  or views. Shorter export intervals do not clear cumulative state.
- Do not silently change sampling, metric temporality, histogram aggregation,
  container resources, exporter protocol, or retention requirements as part of
  buffering work.

## Validate and roll out

Apply `codeatout-testing` and the repository's established test tooling.

1. Exercise the real configuration-loading path. Confirm the intended sink
   is registered, settings bind to the intended processor, and invalid owned
   settings fail explicitly instead of being ignored.
2. Use a controlled in-memory exporter or HTTP test boundary. Block export,
   fill the configured queue, release export, and assert the exact retained
   count, batch-size ceiling, full-queue behaviour, and recovery. Avoid sleeps
   and private-field inspection as proof of queue limits.
3. Cover export failure, retry behaviour, shutdown flushing, and telemetry
   disabled where affected. Verify log levels, resource attributes, trace
   correlation, and secret-safe diagnostics remain intact.
4. Render the consuming deployment manifests and check exact variable names,
   string values, configuration precedence, enabled and disabled branches, and
   pod restart behaviour. Do not validate only the application settings file.
5. Make exporter failures and drops observable through supported counters,
   failure listeners, or internal diagnostics. Route these outside the failing
   exporter where possible, and do not emit raw events or sensitive payloads.
   State when the installed sink cannot report queue-full drops.
6. Canary the change and compare memory, CPU, export latency, and telemetry
   loss through peak traffic. Exercise slow or unavailable export in a safe
   non-production environment. Document rollback and any unverified behaviour.
