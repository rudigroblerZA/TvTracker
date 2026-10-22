---
description: 'Organisation-wide guidance for maintainable and secure legacy .NET Framework projects'
applyTo: '**/*.csproj, **/*.props, **/*.targets'
---

# .NET Framework Development

When a project targets .NET Framework, inspect its target framework, project
format, language version, NuGet management, build tooling, and nearby code
before making changes. Follow the repository's established tooling and
conventions; do not assume that every .NET Framework project is a legacy
non-SDK project.

## Project and build conventions

- Identify whether the project is SDK-style or legacy non-SDK style. Legacy
  projects commonly require explicit `Compile` entries and use
  `TargetFrameworkVersion`; SDK-style projects retain SDK conventions even when
  targeting .NET Framework.
- Use the build command already established by the repository. Legacy projects
  may require MSBuild or Visual Studio build tools, while SDK-style projects
  may support `dotnet build`.
- Use only the C# language features enabled by the project's configured
  compiler and `LangVersion`. Do not change the target framework, SDK, or
  language version as an incidental change.
- Confirm library and NuGet package compatibility with the targeted .NET
  Framework version and the repository's package-management approach before
  changing dependencies.

## Runtime safety

- Avoid synchronous waits on asynchronous work. In reusable library code,
  follow the repository's established context-capture convention and ensure
  cancellation and disposal remain correct.
- Use `DateTimeOffset` for absolute timestamps. When `DateTime` is required,
  preserve and handle its `Kind` intentionally. Use an explicit culture for
  persisted or protocol data.
- Specify `StringComparison` for non-linguistic string comparisons and avoid
  relying on the current culture for identifiers, keys, or protocol values.
- Dispose resources deterministically using the language form supported by the
  configured compiler. Do not use finalizers or static mutable state as a
  substitute for clear ownership.
- Keep secrets out of source, application configuration transforms, logs,
  exceptions, and test data. Use the repository's approved configuration and
  secret-management approach.

## Validation

Run the focused build and tests using the repository's supported .NET Framework
toolchain. Check that project-file changes include all required legacy entries
and that changes remain compatible with every configured target framework.
