---
name: codeatout-open-telemetry
description: Adds OpenTelemetry tracing and metrics to .NET applications. Use when instrumenting operations, dependencies, errors, or service-level measurements.
---

# OpenTelemetry for .NET

## Discover first

Inspect existing `ActivitySource`, `Meter`, exporters, sampling, logging,
resource attributes, and telemetry conventions. Reuse them rather than creating
parallel instrumentation.

Distinguish library instrumentation from application configuration. Libraries
should use the .NET `ActivitySource`, `Meter`, and `ILogger` APIs without
owning exporters or SDK configuration. The application composition root owns
resource attributes, collection, sampling, and export.

## Instrumentation principles

- Model meaningful operation boundaries with stable, low-cardinality activity
  names and metric names.
- Reuse a stable, component-level activity source and meter name. Treat source
  names, operation names, metric names, tag keys, and units as observable
  contracts that require deliberate compatibility changes.
- Select `ActivityKind` to describe the boundary accurately. Use `Internal` for
  local work and the appropriate client, server, producer, or consumer kind at
  external boundaries.
- Add attributes that help diagnose behaviour without recording secrets,
  personal data, unbounded identifiers, or raw request content.
- Use standard semantic conventions where they apply.
- Record exceptions and failed operation status consistently with the existing
  telemetry policy.
- Keep disabled instrumentation inexpensive: avoid eager formatting,
  allocations, and expensive tag values when no listener is active.
- An activity can be absent or sampled with limited data. Check the returned
  activity before adding tags, and defer expensive tag computation until data
  is requested.

## Performance and resilience

Instrumentation must not materially affect the operation it observes or alter
its outcome when telemetry is disabled, sampled, unavailable, or misconfigured.

- Create activity sources, meters, and instruments once per component; do not
  allocate or look them up for each operation.
- For a measured, high-throughput path, check `ActivitySource.HasListeners()`
  before starting an activity. After starting one, use
  `Activity.IsAllDataRequested` before formatting or computing expensive tags.
- Use stable constant operation names and tag keys. Do not build names with
  interpolation or include runtime values in them.
- Avoid allocations solely for telemetry on the disabled path: defer string
  formatting, serialization, collection construction, and tag-value conversion
  until a listener needs the data.
- Measure elapsed time with `Stopwatch.GetTimestamp()` and
  `Stopwatch.GetElapsedTime()` where recording a duration. Stop or dispose an
  activity only after the asynchronous work it represents has completed.
- Keep telemetry code non-blocking and avoid synchronous network, disk, or
  configuration reads on an application request path.

## Distributed tracing

Prefer the runtime's automatic context propagation for supported HTTP and
messaging integrations. For a custom transport, inject and extract the W3C
trace context using the repository's established propagator so downstream work
continues the intended trace.

Use `Activity` baggage only for bounded, non-sensitive correlation context that
must cross process boundaries. Prefer an incoming correlation header or a
generated identifier; do not accept correlation values through query strings,
where they can leak through URLs, browser history, proxy logs, and caches.

Use a parent-child relationship for direct causal work. Use activity links for
batch processing, fan-in, or other cases where one operation is related to
multiple independent contexts rather than caused by one parent.

## Metrics

Select counters, histograms, and gauges based on the question operators need to
answer. Define units and bounded dimensions. Do not use high-cardinality values
such as user IDs, request IDs, or arbitrary URLs as metric labels.

- Use a counter for monotonic event totals, an up-down counter for changes in a
  current total, a histogram for distributions such as duration or size, and
  an observable instrument for values collected by polling.
- Use OpenTelemetry/UCUM units such as `s`, `By`, and `{request}`. Select
  histogram bucket boundaries and aggregation views from expected operating
  ranges and dashboard or alert requirements.
- Keep observable callbacks fast, side-effect-free, and independent of
  request-scoped state.

## Host configuration and logs

At the application root, configure stable service identity and deployment
resource attributes using the repository's conventions. Select exporters and
sampling policies based on operational requirements; instrumentation must
remain correct when sampling drops an activity or its data.
Apply `codeatout-dotnet-configuration` when binding exporter, resource, and
sampling settings.
Apply `codeatout-telemetry-buffering` when changing exporter queues, batching,
retry retention, or telemetry-related memory usage.

Use `ILogger` for application logs, preserving existing structured logging,
trace correlation, redaction, and retention policies. Do not duplicate a
business event in logs, spans, and metrics without a distinct diagnostic need.

## Validate

Test instrumentation with an in-memory listener or the repository's existing
telemetry test approach. Cover success, failure, no-listener and sampling
paths, as well as activity kind, parentage or links, propagation, tag safety,
and metric instrument, unit, and dimension choices where changed.
