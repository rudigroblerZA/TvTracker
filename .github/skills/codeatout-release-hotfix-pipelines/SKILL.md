---
name: codeatout-release-hotfix-pipelines
description: Designs or improves deployment and hotfix pipelines with controlled promotion and rollback. Use when changing release stages, environment promotion, deployment configuration, or urgent fix delivery.
---

# Release and Hotfix Pipelines

## Discover first

Inspect the deployment platform, environments, approval controls, artifact
provenance, rollback approach, configuration ownership, and existing release
policy. Confirm the urgency and compatibility constraints of a hotfix before
altering normal promotion flow.

## Principles

- Promote the same immutable, validated artifact through environments.
- Separate build-time artifacts from environment-specific configuration and
  secrets.
- Gate production deployment with the organisation's required approvals,
  checks, and audit trail.
- Make rollback or roll-forward behaviour explicit and tested for the delivery
  mechanism in use.
- Limit hotfix exceptions to the minimum necessary scope and restore the normal
  release path afterward.
- Use least-privilege deployment identities and never place credentials in
  pipeline definitions or generated files.

## Validate

Verify promotion conditions, artifact selection, environment variables, secret
resolution, deployment health checks, and rollback behaviour in a safe
non-production environment where possible.

## Azure DevOps reference pattern

When Azure DevOps environments and reusable templates are already in use, a
hotfix entry pipeline can reuse the normal build and release stages while
restricting its trigger:

```yaml
trigger:
  branches:
    include: [hotfix/*]

stages:
- template: pipelines/build-stage.yml
- template: pipelines/release-stage.yml
  parameters:
    dependsOn: Build
    environment: train
- template: pipelines/release-stage.yml
  parameters:
    dependsOn: Train
    environment: production
```

The release stage downloads the immutable build artifact and deploys that exact
version; protected environment approvals remain configured outside the YAML:

```yaml
jobs:
- deployment: Release
  environment: ${{ parameters.environment }}
  strategy:
    runOnce:
      deploy:
        steps:
        - download: current
          artifact: package
        - task: HelmDeploy@0
          inputs:
            command: upgrade
            arguments: '--version $(appVersion)'
```

Adapt the artifact type and deployment task to the consuming platform. Keep
production credentials in protected service connections or secret stores.
