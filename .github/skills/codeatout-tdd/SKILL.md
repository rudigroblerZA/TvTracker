---
name: codeatout-tdd
description: Applies test-driven development to a bug fix or small behaviour change using the repository's established test tooling. Use when a developer explicitly requests TDD, a failing regression test, or a bug has a clear, low-cost automated test path.
---

# Test-Driven Development

Use `codeatout-testing` to choose the appropriate test level and general test
strategy. For .NET, also apply `codeatout-csharp-unit-testing` for isolated
behaviour, `codeatout-csharp-xunit` when the repository uses xUnit, and
`codeatout-csharp-api-integration-testing` for ASP.NET Core HTTP boundaries.

## Discover the test path

First determine the intended and actual behaviour, the affected path, and the
smallest observable reproduction. Inspect the repository's existing tests,
framework, test data, fixtures, and supported validation commands. Prefer the
closest existing unit, component, integration, or regression-test pattern.

Use TDD when a focused, deterministic test can be added with proportionate
cost. Do not force it when the available path requires broad harness setup,
brittle mocks, slow or production-like infrastructure, vague reproduction
steps, or large unrelated fixture changes.

## Work red, green, refactor

1. Write the smallest test that captures the intended behaviour or regression.
   Test observable outcomes rather than implementation details.
2. Run the test before changing production code. Confirm that it fails for the
   expected reason; correct the test or reproduction if it does not.
3. Make the smallest production change that satisfies the test and preserves
   surrounding contracts.
4. Run the focused test again and confirm it passes.
5. Refactor only after the test passes, preserving the behaviour it protects.
6. Run proportionate nearby validation using existing repository commands.

## When a failing test is not practical

State why a failing automated test would provide insufficient value before
changing the implementation. Use the closest reliable executable check instead,
such as a targeted script, a manual reproduction command, browser automation,
a snapshot comparison, a log assertion, or focused integration validation.

Prefer no new test over a test that mostly verifies mocks, relies on timing or
shared global state, duplicates implementation details, or needs disproportionate
infrastructure for a small change.

## Guardrails

- Do not change tests to preserve incorrect behaviour.
- Do not weaken an existing assertion unless the intended contract changed and
  the reason is explicit.
- Keep regression coverage narrow; avoid unrelated fixture churn or speculative
  coverage expansion.
- Keep flaky-test signals deterministic where possible.
- For a broader defect class, establish the focused regression first, then add
  sibling coverage only when it is justified.

## Report evidence

State the focused test or executable check used, what it demonstrated before
the implementation change, and the result after the change. If a
failing-before test was impractical, state the reason and the closest
regression check used.
