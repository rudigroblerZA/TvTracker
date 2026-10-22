---
name: codeatout-resilience
description: Designs and changes resilient .NET handling for rate limits, remote HTTP dependencies, caching, and multi-provider integrations. Use when protecting capacity, handling transient dependency failures, or defining degradation behaviour.
---

# .NET Resilience

## Discover first

Inspect the target framework, package-management policy, request and background
processing paths, dependency contracts, existing timeout/retry/circuit-breaker
policies, cache ownership, error responses, and operational objectives. Identify
which operations are safe to repeat, what data may be stale, and the business
impact of allowing versus rejecting work during an outage.

Apply `codeatout-api` for consistent HTTP contracts, `codeatout-worker` for
asynchronous retry and idempotency, and `codeatout-open-telemetry` for existing
tracing, metrics, redaction, and cardinality conventions.

## Choose explicit failure semantics

- Define the protected resource, limit scope, capacity objective, and the
  client-visible outcome before selecting a mechanism.
- Decide and document fail-open or fail-closed for each dependency failure.
  Fail closed where bypass could enable abuse, overspend a scarce resource, or
  violate a hard downstream limit. Fail open only when the resulting extra work
  is explicitly acceptable and bounded.
- Keep authorization, validation, and durable correctness rules independent of
  resilience mechanisms; a cache or limiter is not an authorization boundary.
- Use timeouts and cancellation on every remote path. Bound queues, concurrent
  work, response sizes, and waiting time to prevent an outage becoming local
  resource exhaustion.

## Distributed rate limiting

Use a local limiter for local capacity. Use a shared, atomic store only when
the limit must span processes or instances. Redis through StackExchange.Redis
is conditional: select it only when the repository already uses it or the
shared-limit requirement justifies operating it. Add StackExchange.Redis and
`System.Threading.RateLimiting` packages only when the target framework does
not already supply the required APIs and the repository's dependency policy
approves them.

- Construct a stable, namespaced key from the resource and a privacy-safe,
  normalized subject or partition. Do not use raw personal identifiers,
  unbounded URLs, tokens, or arbitrary headers. Hash a sensitive identifier if
  it must partition a limit.
- Include a policy revision in the key when changing semantics; expire old
  state deliberately. Set a TTL aligned to the algorithm window and ensure
  every created counter or bucket expires.
- Perform increment/check/expiry in one server-side atomic operation. Never
  issue separate read, increment, and expiry calls that can race or leave
  permanent keys.
- Define partition cardinality and eviction behaviour. Reject malformed,
  anonymous, or missing partitions consistently rather than silently sharing an
  unbounded global bucket.
- On rejection, return `429`, a bounded, truthful `Retry-After` when known,
  and the repository's standard `ProblemDetails` shape. Do not disclose
  internal keys, thresholds, or dependency state.

```csharp
public async Task<RateLimitDecision> CheckAsync(
    string subject,
    CancellationToken cancellationToken)
{
    var key = keyFactory.Create("operation", subject);
    var result = await counter.TryConsumeAtomicallyAsync(
        key, limit: 20, window: TimeSpan.FromMinutes(1), cancellationToken);

    return result.Allowed
        ? RateLimitDecision.Allow()
        : RateLimitDecision.Reject(result.RetryAfter);
}
```

Map a rejection at the API boundary using the existing problem-response
convention:

```csharp
if (!decision.Allowed)
{
    Response.Headers["Retry-After"] = decision.RetryAfter.TotalSeconds.ToString("0");
    return Problem(statusCode: StatusCodes.Status429TooManyRequests);
}
```

## HTTP dependencies and circuit breakers

- Classify failures from the provider contract: retry only transient transport
  failures and explicitly retryable responses. Do not retry cancellation,
  validation, authorization, or most client errors. Respect server
  `Retry-After` where it fits the caller deadline.
- Use a small, bounded retry count with exponential backoff, jitter, a total
  time budget, and cancellation. Do not combine independent retry layers; the
  resulting multiplication can overload a failing dependency.
- A circuit breaker must have closed, open, and half-open/probe behaviour.
  Count only relevant failures, open for a bounded recovery interval, permit
  limited probes, and close only after defined recovery evidence. Scope breaker
  state per meaningful dependency and operation, not globally by accident.
- Provide a fallback only when its correctness, freshness, ownership, and
  authorization are equivalent enough for the caller. Prefer a clear,
  documented unavailable response to fabricated or unsafe data.
- Normalize provider-specific errors into a small internal outcome model.
  Do not let one provider's status codes, quotas, pagination, idempotency, or
  retry semantics leak into another integration or application contract.

```csharp
var outcome = await breaker.ExecuteAsync(
    token => client.SendAsync(request, token),
    cancellationToken);

return outcome.IsSuccess
    ? outcome.Value
    : fallback.TryGet(out var value)
        ? value
        : throw new DependencyUnavailableException();
```

## Cache deliberately

- State what is cached, who may read it, its freshness limit, invalidation
  trigger, and behaviour on cache failure. Cache only data whose classification
  and authorization scope allow it.
- Include tenant, authorization scope, representation, and relevant query
  dimensions in cache keys; never key a shared response solely by a route.
- Use bounded TTLs, size limits, and stampede control. A stale-on-error policy
  needs an explicit maximum stale age and must not hide changes that require
  immediate consistency.
- Do not treat a cache hit as proof that an object still exists or that the
  caller remains authorized. Avoid caching exceptions, transient failures, or
  partial responses unless that outcome is intentionally part of the contract.

## Observe and validate

Record low-cardinality counters for allowed/rejected limits, breaker
transitions, retries, fallbacks, cache hits/misses, stale responses, and
dependency failures. Record duration distributions and correlate traces using
the existing OpenTelemetry patterns. Exclude keys, subjects, request content,
credentials, and provider payloads from metrics, spans, and logs.

Test deterministic clock and dependency seams for:

- concurrent limit consumption, expiry, atomicity, and shared-store outage
  decisions;
- `429`, `Retry-After`, and `ProblemDetails` contracts;
- retry classification, attempt and total-time bounds, cancellation, and
  retry-after handling;
- breaker open, recovery interval, probe, and close transitions;
- fallback correctness, cache authorization isolation, invalidation, stale-age
  bound, and cache outage behaviour;
- each provider's quota, timeout, idempotency, and partial-failure semantics.
