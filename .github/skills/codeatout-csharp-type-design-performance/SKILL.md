---
name: codeatout-csharp-type-design-performance
description: Designs and reviews C# types, collections, and allocation-sensitive code for measured performance. Use when choosing class, struct, collection, enumeration, async, or buffer patterns in .NET.
---

# C# Type Design and Performance

## Start from the repository

Confirm the target framework, language version, existing type-design
conventions, public API compatibility requirements, and whether the code is on
a measured hot path. Profile or benchmark representative workloads before
trading clarity, allocation, or API simplicity for a micro-optimization.

Preserve the required value, identity, mutability, ownership, and lifetime
semantics. Performance improvements must not change observable behaviour.

## Design types deliberately

- Seal a class when it is not an intended inheritance or proxy extension point.
  This makes the API intent explicit and can enable JIT optimizations.
- Use a class where reference identity, shared mutable state, polymorphism, or
  a larger object is appropriate.
- Use a small immutable `readonly struct` or `readonly record struct` for
  value semantics. Avoid mutable structs and large structs that are frequently
  copied.
- Prefer static pure functions when an operation needs no instance state or
  polymorphism. Keep genuine dependencies explicit; do not turn cohesive
  services into static utilities merely to avoid a virtual call.
- Treat changes to public type kind, mutability, inheritance, and collection
  contracts as compatibility decisions, not just performance changes.

## Enumerate and collect intentionally

- Avoid materializing an `IEnumerable<T>` more than once unless the repeated
  enumeration is required and safe. Combine filtering, projection, and
  ordering before the final materialization where practical.
- Return a lazy sequence only when deferred execution, source lifetime, repeat
  enumeration, and exception timing are part of the documented contract.
- Return `IReadOnlyList<T>` or `IReadOnlyCollection<T>` when callers need a
  read-only view. These interfaces do not make the underlying collection
  immutable; defensively copy or use an immutable collection when callers must
  not observe future mutation.
- Use `FrozenDictionary<TKey, TValue>` or `FrozenSet<T>` only for static or
  infrequently rebuilt lookup data on supported target frameworks, after
  measuring the construction and lookup trade-off.

## Use async and buffers safely

- Return `Task` for ordinary asynchronous operations. Consider `ValueTask`
  only for a measured hot path that commonly completes synchronously and can
  follow its single-consumption rules.
- Do not block on asynchronous work. Bound concurrency when processing large
  sequences; `Task.WhenAll` creates one task per input item.
- Use `ReadOnlySpan<T>` or `Span<T>` for synchronous, allocation-sensitive
  parsing and buffer operations. Use `ReadOnlyMemory<T>` or `Memory<T>` across
  asynchronous boundaries because spans cannot outlive a synchronous stack
  frame.
- Use `stackalloc` only for small, bounded buffers. When renting from
  `ArrayPool<T>`, return the buffer in a `finally` block and do not expose it
  after return.

## Validate

Run the repository's focused tests and performance validation where it exists.
For a material hot-path change, compare representative benchmarks or profiling
data for throughput, allocations, and tail latency. Check that disposal,
cancellation, enumeration timing, nullability, and public API compatibility
remain correct.
