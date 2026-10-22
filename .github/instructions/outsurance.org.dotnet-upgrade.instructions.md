---
description: 'Organisation-wide guidance for safe, staged .NET framework and SDK upgrades'
applyTo: '**/*.csproj, **/*.props, **/*.targets, **/global.json'
---

# .NET Upgrade

Treat a target-framework, SDK, or major dependency upgrade as a planned
compatibility change, not a mechanical version replacement. Confirm the
business objective, supported target, deployment environment, and rollback
expectations before editing project or pipeline configuration.

## Assess and plan

- Inventory target frameworks, project formats, project references, package
  management, `global.json`, build scripts, container images, CI definitions,
  runtime hosting, and test projects.
- Choose a target supported by the organisation, repository dependencies, and
  deployment platform. Do not automatically select the newest runtime or
  migrate .NET Framework or .NET Standard projects without a compatibility
  assessment.
- Read the relevant Microsoft release notes, support policy, breaking changes,
  and package compatibility guidance. Record assumptions and blockers.
- Map project dependencies and upgrade lower-level libraries before dependents
  when that reduces incompatible framework references. Use multi-targeting or
  staged compatibility boundaries only when they have a clear migration value.
- Create a reviewable upgrade plan with scope, sequence, package decisions,
  compatibility risks, validation, deployment impact, and rollback approach.

## Implement incrementally

- Change a coherent project or dependency group at a time. Restore, build, and
  run its focused tests before extending the upgrade.
- Update package versions only when they are compatible with the selected
  target and required by the upgrade. Preserve central package-management and
  lock-file conventions; do not broadly update packages to their latest
  versions without a compatibility reason.
- Replace obsolete APIs based on compiler diagnostics and official migration
  guidance. Preserve public contracts, serialization, configuration,
  authentication, authorization, logging, telemetry, and data behaviour unless
  their change is explicitly part of the upgrade.
- Treat `packages.config` conversion, project-format conversion, nullable
  adoption, and major hosting changes as separately reviewable work when their
  scope obscures the framework upgrade.
- Update build, test, package, container, and deployment configuration with
  the runtime change. Do not add preview SDKs or loosen pipeline checks unless
  explicitly required.

## Validate and release

- Run the repository's restore, build, analyzer, and focused test commands at
  each stage, then its wider validation when the upgrade is complete.
- Exercise affected integration boundaries and deployment-representative
  runtime behaviour where available, including configuration, startup,
  authentication, observability, and background processing.
- Compare generated artifacts and dependency graphs for unintended target,
  package, or transitive changes.
- Document the target versions, compatibility changes, residual risks,
  validation evidence, operational rollout, and rollback procedure. Escalate
  unresolved compatibility or production-readiness gaps rather than masking
  them.
