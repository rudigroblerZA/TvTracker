---
name: codeatout-build-pipeline
description: Creates or improves a secure CI build pipeline. Use when adding or changing validation, testing, packaging, versioning, artifact publishing, or build quality gates.
---

# Build Pipeline

## Discover first

Inspect the repository's build files, package managers, existing pipelines,
release process, artifact consumers, and required quality/security checks.
Confirm the CI platform and supported runner image before generating files.

## Required pipeline shape

1. Restore dependencies with locked or reproducible inputs where supported.
2. Build and run the smallest relevant test suite, publishing useful test and
   coverage results when the platform supports them.
3. Run existing formatting, static-analysis, dependency, and security checks.
   Treat compiler warnings, analyzer diagnostics, dependency vulnerabilities,
   and quality-gate failures according to the repository's policy. Do not
   suppress them in CI merely to make a build green; document approved,
   time-bounded exceptions with their mitigation.
4. Create versioned artifacts only after validation succeeds.
5. Publish only the artifacts required by downstream delivery, with clear
   retention and provenance.

## Guardrails

- Use least-privilege service connections and short-lived credentials.
- Read secrets from the CI platform's secret store; never echo or embed them.
- Pin external actions, tasks, and container images according to the
  organisation's supply-chain policy.
- Keep pull-request validation free of deployment side effects.
- For infrastructure changes, run a non-mutating validation or plan during
  pull-request builds and reserve apply or deployment steps for approved
  environment promotion.
- Do not impose a language, package format, registry, or pipeline vendor where
  the consuming repository already has an established alternative.

## Validate

Verify triggers, path filters, cache keys, artifact names, failure behaviour,
and that a clean checkout can execute the pipeline.

## Azure DevOps reference pattern

When the consuming repository uses Azure DevOps, a thin entry pipeline can
delegate reusable work to a build-stage template. Preserve the repository's
branch policy and task versions:

```yaml
trigger:
  branches:
    include: [main]
    exclude: [hotfix/*]
pr:
  branches:
    include: [main, hotfix/*]

stages:
- template: pipelines/build-stage.yml
```

The shared stage should check out sufficient history only when versioning needs
it, select the SDK from repository configuration, then build, test, and publish
diagnostics:

```yaml
steps:
- checkout: self
  fetchDepth: 0
- task: UseDotNet@2
  inputs:
    useGlobalJson: true
- task: NuGetAuthenticate@1
- task: DotNetCoreCLI@2
  inputs:
    command: build
    arguments: --configuration Release
- task: DotNetCoreCLI@2
  inputs:
    command: test
    arguments: '--no-build --collect:"XPlat Code Coverage"'
- task: PublishTestResults@2
- task: PublishCodeCoverageResults@1
```

Put image, package, and chart publishing behind a non-pull-request condition.
Use artifact names consistently between build and release templates.
