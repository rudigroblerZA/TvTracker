---
name: codeatout-queries
description: Designs read operations, query handlers, projections, and response models without assuming a specific query framework. Use when adding or changing data retrieval behaviour.
---

# Queries

## Discover first

Identify the caller's result shape, read model, query boundary, data access,
authorization scope, filter and sort policy, pagination convention, cache
ownership, consistency requirement, and error contract. Reuse the repository's
existing dispatcher or handler abstraction when it has one; a dedicated query
framework is optional.

## Principles

- Return a contract designed for the caller rather than exposing persistence
  entities by default.
- Validate page bounds and allow-list filter and sort fields. Use stable,
  documented ordering for paged results.
- Authorize data at the resource and record level where necessary.
- Avoid unbounded result sets, N+1 remote access patterns, and loading data
  that the response does not use.
- Treat continuation tokens as opaque contracts. Do not expose database query
  text, partition data, authorization scope, or implementation details in a
  client-visible token.
- Make total counts optional and deliberate: they can require separate or
  expensive work and are not implied by a paged result.
- Cache only when its key includes the caller's authorization scope, tenant,
  representation, and query dimensions. A cached result never substitutes for
  current authorization.
- Keep read operations free of intentional state changes unless explicitly
  documented by the application's architecture.
- Return known absence or expected business outcomes through the repository's
  established contract. Do not catch arbitrary exceptions only to convert
  programming or dependency failures into an ordinary empty result.

## Pagination and projection

Use a caller-oriented projection and a bounded page contract. The repository's
data-access layer owns its continuation mechanism; the application contract
passes it through as an opaque value only when that is the established API
design.

```csharp
public sealed record SearchItemsQuery(
    string? Category,
    int PageSize,
    string? ContinuationToken);

public sealed record ItemPage(
    IReadOnlyList<ItemDto> Items,
    string? ContinuationToken);

public sealed class SearchItemsHandler(IItemReadService items)
{
    public Task<ItemPage> HandleAsync(
        SearchItemsQuery query,
        CancellationToken cancellationToken)
    {
        int pageSize = Math.Clamp(query.PageSize, 1, 100);

        return items.SearchAsync(
            category: query.Category,
            pageSize: pageSize,
            continuationToken: query.ContinuationToken,
            cancellationToken: cancellationToken);
    }
}
```

Validate `Category` against the allowed domain values before the data-access
call, apply caller authorization before projection, and ensure the
implementation uses parameterized queries or the repository's equivalent
safe query API.

## Validate

Cover empty results, bounds, invalid filters, authorization, deterministic
pagination, continuation-token handling, data mapping, cache isolation, and
dependency failure behaviour.

## Reference query pattern

For a C# application with a distinct query boundary, return a caller-oriented
DTO and preserve cancellation through the data-access call:

```csharp
public sealed record GetItemQuery(string ItemId);

public sealed class GetItemHandler(IItemReadService items)
{
    public async Task<ItemDto?> HandleAsync(
        GetItemQuery query,
        CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(query.ItemId, cancellationToken);

        return item is null
            ? null
            : new ItemDto(item.Id, item.Name, item.Status);
    }
}
```

Apply authorization before returning the DTO, and translate a missing result
into the consuming transport's established not-found contract. A dedicated
query handler is optional; use the repository's existing read-service or
application-service boundary.
