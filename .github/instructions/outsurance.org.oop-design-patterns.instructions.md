---
description: 'Organisation-wide guidance for applying object-oriented design patterns without unnecessary abstraction'
applyTo: '**/*.py, **/*.java, **/*.ts, **/*.js, **/*.cs'
---

# Object-Oriented Design Patterns

Inspect the repository's architecture, language idioms, existing abstractions,
and the behaviour that must vary before introducing a pattern. Prefer the
simplest design that expresses the required behaviour and preserves existing
conventions.

## Principles

- Encapsulate behaviour that genuinely varies; do not introduce an abstraction
  solely because a pattern has a name.
- Prefer composition over inheritance. Use inheritance only for a true
  substitutable relationship with a stable base contract.
- Depend on stable abstractions at external or volatile boundaries. Do not add
  interfaces, factories, repositories, mediators, or wrappers inside a
  cohesive implementation without a concrete need.
- Keep ownership, state transitions, error handling, concurrency, and resource
  lifetime explicit. Avoid global mutable state and service locators.
- Give types one cohesive responsibility, but do not split related behaviour
  into a web of single-method types.
- Preserve least-visible APIs, immutability where practical, validation at
  boundaries, and testable observable behaviour.

## Pattern selection

Use a pattern when it makes a recurring problem clearer:

- Strategy or State for interchangeable behaviour or explicit state-dependent
  behaviour.
- Adapter or Facade at an incompatible or complex external boundary.
- Decorator for composable cross-cutting behaviour that shares a stable
  contract.
- Factory or Builder when construction has meaningful variability or complex
  validation.
- Command for queued, auditable, retryable, or undoable operations.
- Observer or events for explicit one-to-many notifications with clear
  ownership and unsubscribe lifecycle.

Avoid pattern-driven rewrites when a function, data structure, conditional, or
direct dependency is clearer and sufficiently stable.

## Maintainability

Name types for their domain responsibility rather than their pattern whenever
that is clearer. Document the reason for a non-obvious abstraction, its
extension boundary, and its lifecycle; do not add comments that merely restate
the implementation.

Test contracts and behaviour, including relevant error and lifecycle paths,
rather than pattern internals. Refactor incrementally and verify that public
behaviour, performance characteristics, observability, and security boundaries
remain correct.
