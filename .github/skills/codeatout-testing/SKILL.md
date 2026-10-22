---
name: codeatout-testing
description: Selects and implements proportionate automated tests using the consuming repository's established tooling. Use when planning or adding tests for changed behaviour.
---

# Testing Strategy

Use `codeatout-csharp-unit-testing` for isolated .NET behaviour,
`codeatout-csharp-xunit` for xUnit conventions,
`codeatout-csharp-api-integration-testing` for ASP.NET Core HTTP boundaries,
and `codeatout-csharp-testcontainers` when a real containerized dependency is
needed.

## Choose the smallest meaningful level

Use unit tests for isolated rules, integration tests for boundaries such as
persistence or HTTP, and end-to-end tests for critical user workflows. Follow
the repository's existing framework, assertion library, and naming conventions.

## Principles

- Test observable behaviour and important failure modes, not private
  implementation details.
- Make tests deterministic, isolated, readable, and suitable for parallel
  execution.
 - Use explicit `// Arrange`, `// Act`, `// Assert` comments in each test to
   make the phases of the test clear and consistent.
- Use realistic non-production data with sensitive values removed or generated.
- Mock external systems at the boundary only when a real test dependency is not
  appropriate.
- Add regression coverage for defects and contract coverage for public
  interfaces.

## Validate

Run the focused tests and ensure they fail for the defect or behaviour they are
intended to protect.

## Reference test patterns

When the repository uses xUnit and an assertion library with fluent assertions,
keep tests focused and structure the relevant phases clearly:

```csharp
[Fact]
public async Task WhenItemDoesNotExist_ItReturnsNotFound()
{
    // Arrange
    var service = new ItemService(new EmptyItemReader());

    // Act
    var result = await service.GetByIdAsync("missing", CancellationToken.None);

    // Assert
    result.Status.Should().Be(QueryStatus.NotFound);
}
```

For HTTP integration coverage, assert the boundary behaviour rather than
internal calls:

```csharp
using var response = await client.GetAsync("/items/missing");

response.StatusCode.Should().Be(HttpStatusCode.NotFound);
```

Use the test framework, assertion syntax, cancellation helper, and naming
convention already established by the consuming repository.
