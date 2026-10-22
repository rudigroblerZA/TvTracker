---
name: codeatout-api
description: Creates or changes HTTP API endpoints, contracts, validation, mappings, and error handling. Use when adding or modifying a web API surface.
---

# API Development

## Discover first

Inspect the repository's routing, versioning, authentication, validation,
problem-response, serialization, mapping, observability, and testing
conventions. Preserve compatible public contracts unless a versioned breaking
change is explicitly intended.

Apply `codeatout-queries` for read-operation design and
`codeatout-commands` for state-changing operation design when the API maps to
those application boundaries.

## Principles

- Define clear request and response contracts at the API boundary.
- Validate untrusted input server-side and return consistent, useful errors
  without exposing implementation details.
- Authorize access to the requested resource, not just the endpoint.
- Pass cancellation and correlation context through downstream operations.
- Use the repository's `ProblemDetails` convention for client-safe errors.
  Include a safe correlation or trace identifier when one exists, but never
  expose exception details, tokens, or request bodies.
- Keep transport mapping separate from domain behaviour where the existing
  architecture does so; do not impose a particular mediator or messaging stack.
- Document security requirements, status codes, pagination/filtering semantics,
  and idempotency when applicable.
- For a breaking public-contract change, use the repository's versioning and
  deprecation policy; do not silently change an existing endpoint's semantics.

## Validate

Cover successful, invalid, unauthorized, forbidden, not-found, conflict, and
unexpected-failure behaviour appropriate to the endpoint.

Create or update focused automated tests in the same change for every endpoint
or API-contract change. Cover the HTTP contract where the repository has API
integration tests; otherwise cover controller mapping and validation at the
established unit-test boundary. Never leave new API behaviour untested.

## Reference endpoint pattern

For a layered API that already separates HTTP contracts from application
queries, keep request binding, dispatch, and response mapping explicit:

```csharp
[HttpGet("{id}")]
public async Task<ActionResult<ItemResponse>> GetByIdAsync(
    string id,
    CancellationToken cancellationToken)
{
    var result = await itemQueries.GetByIdAsync(id, cancellationToken);

    return result switch
    {
        { Status: QueryStatus.Found } => Ok(new ItemResponse(result.Value)),
        { Status: QueryStatus.NotFound } => NotFound(),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
}
```

Validate the route and query model before dispatching. If the application uses
a mediator or message bus, map to its existing command/query contract rather
than coupling the controller to persistence or transport implementation.
