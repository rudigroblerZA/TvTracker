---
name: codeatout-csharp-testcontainers
description: Creates .NET integration tests with Testcontainers for real databases, queues, caches, and other Dockerized dependencies. Use when a test needs production-like infrastructure behaviour without shared external environments.
---

# .NET Testcontainers

Apply `codeatout-csharp-api-integration-testing` when a containerized
dependency sits behind an ASP.NET Core HTTP boundary. Use the test host to
exercise the public contract and Testcontainers to provide the real
infrastructure it depends on.

## Discover first

Confirm that the test requires a real infrastructure boundary rather than an
isolated unit test. Inspect the repository's test framework, fixture lifecycle,
container modules, image policy, Docker availability, CI runner capabilities,
and existing data-reset approach before adding containers.

Use the repository's supported Testcontainers and test-framework versions. Do
not introduce a parallel fixture or container lifecycle pattern.

## Design the test boundary

- Test the real behaviour that mocks cannot establish: queries, migrations,
  transactions, serialization, cache semantics, message delivery, or
  integration configuration.
- Keep application code real through the boundary under test. Replace only
  dependencies that are external to the test scenario.
- Use the same database engine, broker, or cache product as production when
  its semantics affect the behaviour being tested.
- Test migrations and schema constraints against a newly initialized database
  when they are part of the change.

## Configure containers safely

- Pin images to a reviewed, compatible version; do not use floating tags such
  as `latest`.
- Let Docker assign host ports and obtain the mapped endpoint from the
  container. Do not assume a fixed localhost port.
- Use a readiness check that proves the dependency can accept the operation
  under test, not merely that its port is open.
- Keep test configuration and generated credentials scoped to the test
  environment. Never use or log production credentials, endpoints, or data.
- For multi-container tests, use a dedicated network and aliases for
  container-to-container communication rather than host port mappings.

## Lifecycle, isolation, and performance

- Start and dispose containers through the repository's asynchronous fixture
  lifecycle. Ensure cleanup runs after failures and cancellation.
- Isolate each test's state. Use a fresh container, unique database or
  namespace, transaction rollback, or a proven reset mechanism as appropriate.
- Share a container only within a clearly scoped fixture when startup cost
  warrants it, and reset all mutable state between tests so parallel execution
  remains safe.
- Apply bounded startup and operation timeouts. Fail with useful container
  diagnostics while masking connection strings, credentials, and sensitive
  payloads.
- Avoid cross-run container reuse unless the repository explicitly supports
  it; it weakens test reproducibility and can leak state between developers or
  CI jobs.

## Validate

Run the focused integration tests with Docker available, including a clean
environment where practical. Confirm the test fails when the expected
infrastructure behaviour, migration, or integration contract is broken and
passes independently and in parallel with the repository's test suite.
