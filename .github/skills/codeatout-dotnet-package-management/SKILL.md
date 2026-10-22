---
name: codeatout-dotnet-package-management
description: Manages NuGet dependencies, Central Package Management, package sources, and restore issues in .NET solutions. Use when adding, removing, updating, auditing, or troubleshooting packages.
---

# .NET Package Management

## Discover first

Identify the target SDK, package source configuration, lock-file policy, and
whether the solution uses Central Package Management (CPM) through
`Directory.Packages.props`. Follow the repository's established package and
version-management convention; do not introduce CPM or migrate package
management as an incidental part of an unrelated change.

Check the dependency's compatibility with every targeted framework, its
transitive dependencies, licensing policy, maintenance status, and whether an
existing approved package already satisfies the need.

## Add, remove, and update deliberately

- Use the `dotnet` CLI or the repository's documented package-management
  workflow for routine package reference changes. Edit project or central
  package files directly only when a required setting, condition, or tooling
  limitation cannot be expressed through that workflow.
- Add a package only to the project that owns the dependency. Do not add it to
  a shared project merely to make it available transitively.
- Prefer stable, compatible versions. Upgrade related packages together only
  when their compatibility guidance requires it, and avoid broad upgrades
  unrelated to the requested change.
- Remove direct package references that are no longer used, but do not remove
  a transitive package reference solely because it appears unused.
- Use `PrivateAssets="all"` for build, analyzer, test, or development-only
  dependencies that must not flow to consuming projects.

## Central Package Management

When CPM is already enabled, place shared package versions in
`Directory.Packages.props` and omit versions from ordinary `PackageReference`
items. Keep related package families on compatible versions and use a named
shared MSBuild property only when it makes that relationship clearer.

Use `VersionOverride` only for a documented, temporary compatibility exception.
Do not mix inline and central versions without understanding the repository's
CPM configuration and the override's effect on restore resolution.

## Sources, restore, and reproducibility

- Use only approved package sources. Treat a `NuGet.config` change as a
  security-sensitive supply-chain change.
- Never commit feed credentials, access tokens, clear-text passwords, or
  credential-bearing source URLs. Use the repository's credential provider,
  environment-based local configuration, or deployment secret mechanism.
- Respect the repository's lock-file policy. When lock files are committed,
  update them intentionally and use locked restore in CI where configured.
- Investigate restore failures with the dependency graph and restore logs
  before clearing caches. Clear local NuGet caches only as targeted
  troubleshooting, not as a routine fix.

## Security and validation

Run the repository's restore, build, and focused tests after a dependency
change. Use `dotnet list package --vulnerable` and the repository's existing
dependency scanning or policy checks. Address critical and high vulnerabilities
before merging, and document any approved exception with its mitigation and
expiry.

Review the final dependency graph for unintended transitive additions,
framework compatibility, deprecated packages, source changes, and lock-file
updates.
