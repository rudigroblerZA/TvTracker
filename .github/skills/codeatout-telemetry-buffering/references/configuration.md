# Buffer Configuration Reference

Use this reference for .NET applications that already use Serilog or
OpenTelemetry. Resolve the installed sink, configuration library, SDK, and
auto-instrumentation versions before choosing settings. Verify the deployed
environment as well as source-controlled defaults.

## Independent buffers

| Pipeline | Buffer owner | Configuration to verify |
| --- | --- | --- |
| Serilog OTLP logs | Serilog sink and batching implementation | Sink batching options |
| Serilog-generated spans, where supported | Serilog sink and batching implementation | May have a separate queue from logs |
| SDK traces | SDK batch span processor | `OTEL_BSP_*` or processor options |
| SDK logs | SDK batch log processor | `OTEL_BLRP_*` or processor options |
| Metrics | SDK aggregation and metric reader | Dimensions, cardinality, views, temporality, collection settings |

Do not assume that using the OTLP protocol means sharing an SDK, queue, retry
policy, or environment-variable implementation. In particular,
`Serilog.Sinks.OpenTelemetry` exports independently of the OpenTelemetry SDK.

## Serilog sink options

For `Serilog.Sinks.OpenTelemetry` 4.2.0, the batching defaults are 100,000
events per queue, batches of 1,000 events, and a two-second buffering interval.
Serilog 4.4.0's batching retry default is ten minutes. These are version-scoped
defaults, not evidence that a queue is full.

The sink's native options callback exposes:

- `BatchingOptions.QueueLimit`
- `BatchingOptions.BatchSizeLimit`
- `BatchingOptions.BufferingTimeLimit`
- `BatchingOptions.RetryTimeLimit`, where supported by the resolved Serilog version

A small illustrative profile is a 2,048-event queue, 256-event batches, a
two-second buffering interval, and a one-minute retry budget. Choose values
from the application's measurements and loss requirements, not from this
example alone. Lower batch sizes reduce serialization peaks but can increase
export request volume.

### Verify appsettings support before adding code

For sink 4.2.0 with `Serilog.Settings.Configuration` 10.0.1:

- The native JSON-friendly `OpenTelemetry` overload accepts endpoint, protocol,
  headers, resource attributes, included data, minimum level, and level switch.
  It does not accept `batchingOptions`, `queueLimit`, or `batchSizeLimit`.
- Batching is exposed through `Action<BatchedOpenTelemetrySinkOptions>`.
  The configuration library cannot turn a nested JSON `configure` object into
  that callback. It supports only particular logger configuration callbacks.
- A static callback reference can still require application code. It is not a
  JSON-only mechanism for binding arbitrary batching values.
- The sink does not consume `OTEL_BSP_*` or `OTEL_BLRP_*`. Its supported
  environment overrides must be checked separately; SDK timeout variables
  must not be assumed to configure the sink's HTTP client.

Recheck these constraints for other versions. If native JSON support exists,
use it instead of introducing an adapter.

When a bridge is needed, keep values in the existing non-secret configuration
and validate them at startup. Either bind and register the sink programmatically
or expose a small configuration adapter that delegates to the native sink.
Do not retain the old JSON sink registration as well as the new programmatic
registration: that creates duplicate export and queues. Preserve the existing
configuration-provider precedence and environment overrides.

## OpenTelemetry SDK processor settings

The following names are supported by OpenTelemetry .NET SDK 1.19.1. Verify
support and defaults for the SDK actually resolved by the application or
bundled with auto-instrumentation; the auto-instrumentation version number is
not the SDK version number.

| Environment variable | SDK default | Meaning |
| --- | --- | --- |
| `OTEL_BSP_MAX_QUEUE_SIZE` | 2048 | Completed spans awaiting export |
| `OTEL_BSP_MAX_EXPORT_BATCH_SIZE` | 512 | Maximum spans in an export batch |
| `OTEL_BSP_SCHEDULE_DELAY` | 5000 ms | Trace export scheduling delay |
| `OTEL_BSP_EXPORT_TIMEOUT` | 30000 ms | Trace processor export-timeout option |
| `OTEL_BLRP_MAX_QUEUE_SIZE` | 2048 | SDK log records awaiting export |
| `OTEL_BLRP_MAX_EXPORT_BATCH_SIZE` | 512 | Maximum SDK log records in a batch |
| `OTEL_BLRP_SCHEDULE_DELAY` | 5000 ms | SDK log export scheduling delay |
| `OTEL_BLRP_EXPORT_TIMEOUT` | 30000 ms | SDK log processor export-timeout option |

For either processor, keep batch size no larger than queue capacity. A
1,024-item queue, 256-item batch, and 2,000 ms scheduling delay is an
illustrative constrained-memory profile, not a prescribed default.

Verify the transport's `OTEL_EXPORTER_OTLP_TIMEOUT` and signal-specific options
separately. A processor timeout option is not proof that an in-flight network
request is cancelled by a particular exporter. Reducing timeouts can increase
export failures and drops; increasing them can lengthen queue retention.

For .NET auto-instrumentation that supports it:

```yaml
OTEL_DOTNET_AUTO_LOGS_ENABLED: "false"
```

This disables automatic SDK log collection, not a separately configured
Serilog OTLP sink. Confirm coverage before using it; direct Serilog events
and `ILogger` events may reach different providers. If automatic SDK logs
remain required, bound their processor separately.

In Kubernetes ConfigMaps, represent environment values as strings. Confirm
that the pod consumes the ConfigMap and that a rollout refreshes its
environment. Other deployment platforms should follow their own conventions.

## Metrics and other memory

OpenTelemetry .NET SDK 1.19.1 defaults to a cardinality limit of 2,000 attribute
combinations per metric. Aggregation state and histogram storage can remain
significant even with a small trace-sampling ratio.

- `OTEL_METRIC_EXPORT_INTERVAL` controls export frequency, not a universal
  memory limit. Cumulative state is not cleared just because export succeeds.
- Prefer bounded dimensions and supported views/cardinality limits. Do not
  invent a cardinality environment variable from another language's SDK.
- `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=Delta` changes metric
  semantics. Check backend and dashboard compatibility before adopting it.
  The `LowMemory` value is not supported by the .NET implementation covered
  here; do not assume support from the general OTLP specification.
- Attribute-size limits may be enforced during serialization rather than
  collection. They are not proof that queued objects retain less memory.
- Check serialization buffers, active operations, native memory, and GC
  reservations if memory stays high after queue limits are verified.
- Do not enable additional in-memory retries or disk buffering without a
  deliberate retention, capacity, security, and shutdown policy.

## Sources

- [Sink 4.2.0 batching defaults](https://github.com/serilog/serilog-sinks-opentelemetry/blob/v4.2.0/src/Serilog.Sinks.OpenTelemetry/Sinks/OpenTelemetry/BatchedOpenTelemetrySinkOptions.cs)
- [Sink 4.2.0 configuration overloads](https://github.com/serilog/serilog-sinks-opentelemetry/blob/v4.2.0/src/Serilog.Sinks.OpenTelemetry/OpenTelemetryLoggerConfigurationExtensions.cs)
- [Sink 4.2.0 environment overrides](https://github.com/serilog/serilog-sinks-opentelemetry/blob/v4.2.0/src/Serilog.Sinks.OpenTelemetry/Sinks/OpenTelemetry/Configuration/OpenTelemetryEnvironment.cs)
- [Serilog 4.4.0 batching options](https://github.com/serilog/serilog/blob/v4.4.0/src/Serilog/Configuration/BatchingOptions.cs)
- [Settings 10.0.1 callback binding](https://github.com/serilog/serilog-settings-configuration/blob/v10.0.1/src/Serilog.Settings.Configuration/Settings/Configuration/ObjectArgumentValue.cs)
- [SDK 1.19.1 OTLP exporter configuration](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
- [SDK 1.19.1 processor defaults](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry/BatchExportProcessorOptions.cs)
- [SDK 1.19.1 metrics memory management](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/docs/metrics/README.md#memory-management)
- [.NET auto-instrumentation configuration](https://opentelemetry.io/docs/zero-code/dotnet/configuration/)
