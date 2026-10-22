---
name: codeatout-csharp-api-integration-testing
description: Designs .NET API integration tests that exercise the HTTP boundary and real application configuration. Use when creating or improving ASP.NET Core API integration tests.
---

# .NET API Integration Testing

Apply `codeatout-csharp-testcontainers` when the API's behaviour depends on a
real database, cache, broker, or other Docker-supported infrastructure. An API
integration test exercises the HTTP host; Testcontainers supplies the real
infrastructure behind that host when in-memory substitutes cannot prove the
contract.

## Establish the test boundary

Inspect the host entry point, existing test framework, authentication setup,
data stores, external dependencies, and test fixtures. Test through HTTP and
the application's public API contract rather than implementation details.

## Principles

- Use the repository's supported in-memory, containerized, or ephemeral
  dependencies. Isolate data so tests can run in any order and in parallel.
- Configure test authentication only in the test host, and cover authorization
  success and denial where behaviour is protected.
- Exercise request validation, serialization, status codes, error responses,
  routing, and important middleware behaviour.
- Replace only truly external boundaries; do not mock application code merely
  to make a test pass.
- Use deterministic time, identifiers, and cleanup where the test environment
  requires it. Never use production data or credentials.

## Test host and infrastructure

Use the repository's `WebApplicationFactory` or equivalent test-host pattern
to start the real application through HTTP. Configure test-only settings before
the host is built, and replace only dependencies outside the scenario.

```csharp
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("FeatureFlags:Enabled", "true")
            ]);
        });
        builder.ConfigureTestServices(services =>
        {
            // Replace an external dependency, not application behaviour.
        });
    }
}
```

When testing persistence, cache, broker, serialization, migration, or
transport behaviour, start the supported Testcontainers fixture before the
factory and pass its mapped endpoint through the same configuration path used
in deployment. Use a separate fixture or unique namespace for mutable state,
then reset state between tests when a shared container is necessary for
performance.

```csharp
public sealed class ItemsApiTests(ApiFactory factory)
    : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Get_missing_item_returns_not_found_problem_details()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/items/missing");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ProblemDetails problem = await response.Content.ReadFromJsonAsync<ProblemDetails>()
            ?? throw new InvalidOperationException("Expected a problem response.");
        problem.Status.ShouldBe(StatusCodes.Status404NotFound);
    }
}
```

Configure test authentication only in the test host. Exercise both permitted
and denied callers when the endpoint is protected; never make a production
authentication bypass available through ordinary configuration.

## Validate

Run the focused integration tests from a clean test environment and ensure
failures expose useful request/response diagnostics without leaking sensitive
data.
