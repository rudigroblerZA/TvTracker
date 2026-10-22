---
description: 'Organisation-wide guidance for maintainable and secure C# applications'
applyTo: '**/*.cs'
---

# C# Development

Inspect the target framework, language version, analyzers, `.editorconfig`, and
nearby code before making changes. Follow repository conventions when they do
not conflict with correctness, security, or explicit requirements.

- Use language and runtime features supported by the project's configured
  target framework. Do not change target framework, SDK, or language version
  unless requested.
- Prefer clear, small APIs and the least-visible accessibility that satisfies
  the contract.
- Validate public and untrusted input. Use precise exceptions and do not
  silently swallow failures.
- Keep asynchronous work cancellable; avoid blocking asynchronous code and
  propagate cancellation where applicable.
- Keep secrets and sensitive data out of source, logs, exception messages, and
  telemetry.
- Use the repository's formatting, naming, documentation, test framework, and
  assertion conventions. Add XML documentation only when required by the
  repository or public API contract.
- Add focused tests for changed observable behaviour, including relevant error
  and authorization paths.
- Every production C# behaviour change must create or update automated tests in
  the same change. Do not defer test coverage to a later task. Use the
  repository's established unit or integration test level, and add regression
  coverage for every defect fix.

## Coding Conventions
- Always use implicit typing (var) when the right-hand side of the assignment 
  makes the type obvious.
- Use explicit typing when the type is not immediately clear from the right-hand 
  side of the assignment.
- Always use curly braces {} for conditional statements, even for single-line blocks.

## Source layout and documentation

- Declare one top-level type per `.cs` file. Nested types are acceptable only
  when they are private implementation details inseparable from the containing
  type.
- Put a blank line between adjacent properties, and between a property and the
  XML documentation for the following member. Preserve more specific
  repository formatting when it is already enforced by `.editorconfig`.
- Write XML documentation with opening and closing tags on their own lines.
  Keep the summary text on one or more intervening lines; do not use
  single-line `<summary>` documentation.

## Exception handling

- Never use an empty `catch` block. Handle the expected failure explicitly:
  return or assign a deliberate fallback only when the contract permits it,
  record sufficient safe diagnostics when repository conventions require it,
  or allow the exception to propagate.
- For intentionally tolerated parse failures, include a short comment that
  explains the fallback and preserve the raw input or response when it is
  required for diagnostics. Do not use exception handling to conceal
  programming errors.

Do not assume a database, architectural pattern, project layout, dependency
injection container, or test method naming convention.
