---
name: codeatout-architecture
description: Guides feature design and placement within an existing application architecture. Use when planning or implementing a cross-layer feature, integration, command, query, or persistence change.
---

# Application Architecture

## Start from the repository

Identify the application's existing boundaries, dependency direction, feature
layout, domain model, integration style, persistence pattern, and test
strategy. Follow established conventions unless the request explicitly changes
them.

## Principles

- Keep transport, application, domain, persistence, and external-integration
  concerns separated where the architecture distinguishes them.
- Make write, read, asynchronous messaging, and integration boundaries
  explicit. Select patterns based on the problem rather than mandating CQRS,
  a mediator, or a message bus.
- Depend on stable abstractions at external boundaries and avoid unnecessary
  wrappers inside a cohesive feature.
- Make ownership, transactional boundaries, consistency expectations, and
  failure handling clear.
- Place a feature's code and tests together according to the repository's
  established layout.
- Use `codeatout-roslyn-architecture-analyzers` when a stable, verifiable rule
  should become a build-time diagnostic across the solution.

## Validate

Confirm the design preserves dependency direction, authorization, validation,
observability, data migration needs, and backwards compatibility.

## Reference boundary pattern

For a layered feature, make the request flow visible and keep external concerns
at the edge:

```text
HTTP endpoint
  -> application query or command
  -> handler or use-case service
  -> repository or external-client abstraction
  -> persistence or remote system
```

For example, a handler can coordinate a read without exposing a persistence
entity through the API:

```csharp
public sealed class GetItemHandler(IItemReader reader)
{
    public async Task<ItemResult> HandleAsync(
        GetItem query,
        CancellationToken cancellationToken)
    {
        var item = await reader.FindAsync(query.Id, cancellationToken);
        return item is null ? ItemResult.NotFound() : ItemResult.Found(item);
    }
}
```

Use this shape only where the consuming repository already has comparable
boundaries. Folder names, handler suffixes, mediators, buses, and repositories
are implementation choices, not universal requirements.
