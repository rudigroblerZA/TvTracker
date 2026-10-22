---
name: codeatout-csharp-xunit
description: Creates focused xUnit tests using the repository's established xUnit version, fixtures, data sources, and assertion conventions. Use when adding or improving C# tests in a repository that uses xUnit.
---

# xUnit Testing

Apply `codeatout-csharp-unit-testing` first for framework-agnostic unit-test
principles. This skill adds xUnit-specific guidance and does not replace the
repository's established test conventions.

## Discover first

Inspect the xUnit version, test SDK and runner packages, existing test
attributes, fixtures, data sources, assertion library, traits, parallelism
settings, and test project structure. Follow the version-specific APIs already
in use; do not introduce a new xUnit major version, assertion library, mocking
library, or DI container as part of a test change.

Keep xUnit projects under the repository's top-level `test` or `tests`
directory and mirror the production project or feature layout. Use the
repository's existing test SDK, runner, and coverage-collector configuration;
test-only runner and coverage packages should not flow to production consumers.

## Test structure

- Use `[Fact]` for one fixed scenario and `[Theory]` for the same observable
  behaviour over a concise, readable data set.
- Use inline data for small literal cases and member or class data for complex,
  reusable, or generated cases. Keep data deterministic and name cases so
  failures remain diagnosable.
- Return `Task` from asynchronous tests and await the operation under test; do
  not use `async void`.
 - Use explicit `// Arrange`, `// Act`, `// Assert` comments in each test to
   make the phases of the test clear and consistent.
- Use constructors and the fixture lifecycle supported by the installed xUnit
  version for setup and cleanup. Use class or collection fixtures only for
  expensive, genuinely shared context.
- Treat shared fixture state as concurrent by default. Reset mutable state,
  isolate test data, or use a narrowly scoped collection when serialization is
  unavoidable; do not disable parallelism broadly to hide test coupling.

## Assertions and diagnostics

- Use the repository's existing assertion library. Assert the behaviour named
  by the test, including exception type and relevant message or state only
  when it is part of the contract.
- Use `ITestOutputHelper` or the repository's test diagnostics approach for
  failure context. Do not emit credentials, personal data, raw requests, or
  sensitive test data.
- Use traits only when the repository already uses them for filtering or
  reporting. Avoid skipped tests; when unavoidable, record a specific reason,
  owner or issue reference, and removal condition.
- Put broadly reusable test builders and helpers in a focused test-fixtures
  project. Do not make a shared fixture project a catch-all dependency or use
  it to conceal domain-specific setup.

## Validate

Run the focused xUnit project with the repository's supported test command.
Confirm tests remain independent under the configured parallelism and that
fixture setup, teardown, cancellation, and failure paths do not leak resources
or state.
