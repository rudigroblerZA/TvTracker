# Helm and Terraform Version Upgrades

Standard versions are Helm `3.22.0` and Terraform `1.16.4`. A repository
pinning something older should be upgraded, but the upgrade is a separate
change from a pipeline pattern upgrade. Recommend it, confirm it, then apply
it on its own.

This file records what changed between the versions commonly found in older
pipelines and the standard versions, limited to what affects a build pipeline.
Verify the findings still hold before relying on them, since both tools
continue to release.

## Why the two are separate changes

A pattern upgrade and a version bump fail in different places and are worth
reviewing separately. A pattern change that breaks a build shows up
immediately and is easy to attribute. A version bump that breaks a deployment
can surface much later, in a different pipeline.

## Terraform

A build pipeline runs `plan` only, which makes the upgrade from `1.11.x` to
`1.16.x` low risk in itself. There is no state format break across this range,
no change to the meaning of `-detailed-exitcode`, and `required_version = ">= 1.9"`
remains satisfied.

The risk is not in the tool. It is that **Terraform state is written
forward-only**. Once a newer Terraform plans or applies against a workspace,
an older version can no longer read that state. Every pipeline touching a
workspace must move together, plan and apply alike. Upgrading one repository's
build pipeline in isolation breaks the release pipelines still on the old
version.

Changes that can affect a pipeline:

| Version | Change | Matters when |
| --- | --- | --- |
| 1.13 | Invalid and dynamic references are truncated in human-readable plan output | A script scrapes plan text |
| 1.13 | Empty-string workspace names are rejected | Automation ever passes `""` as a workspace |
| 1.14 | Parallelism now respects container CPU bandwidth limits | Agents run with CPU limits; `plan` may get slower without any config change |
| 1.15.0 only | `validate` briefly required `backend` block attributes, breaking partial and `-backend-config` workflows. Reverted in 1.15.1 | Never pin exactly `1.15.0` |
| 1.15.x | `init -json` log message types changed and were then reverted within the series | Automation parses `init -json` message types |
| 1.15.6 | Exit code corrected for variable errors in `plan`, `query`, and `refresh` | Automation depended on the previous, incorrect exit code |
| 1.15.6 | Resources removed via `removed` blocks no longer appear in `planned_values` | A script parses `show -json` `planned_values` |
| 1.15 | Applying a plan file against the wrong workspace now hard-fails | Plan files are reused across workspaces |

Confirm before upgrading:

- Every pipeline that plans or applies against the same workspaces moves
  together. This is the one that breaks deployments.
- The configuration's `required_version` still admits the new version.
- No pipeline pins exactly `1.15.0`.
- No automation parses plan text, `init -json` message types, or
  `show -json` `planned_values`.
- Run `init` and `validate` against the repository's real `-backend-config`
  files before rollout. Backend behaviour was verified only against changelog
  silence, not a source-level diff, so smoke-test it rather than assuming.

## Helm

For a build pipeline the only Helm commands used are `package`, `registry
login`, and `push`. Across `3.16` to `3.22` there are no chart schema,
`apiVersion`, or `Chart.lock` changes, and no `helm package` flag or output
changes. An existing chart that packages today will still package.

Two releases matter:

| Version | Change | Matters when |
| --- | --- | --- |
| 3.18.0 only | OCI client migrated to ORAS v2, which broke the legacy Docker credential fallback and `registry login` with a scheme prefix. Fixed in 3.19.0 | Never pin exactly `3.18.0` |
| 3.18.0 | Automatic HTTP fallback added for OCI push and pull | Error and retry behaviour differs from before |
| 3.22.0 | `push` requested the wrong token scope against token-auth registries. Fixed | Directly relevant to registry pushes; a fix, not a regression |
| 3.22.0 | Credentials were dropped on plain-HTTP fallback. Fixed | A fix |
| 3.22.0 | Provenance signing moved to a different OpenPGP library | Charts are signed with `--sign` or verified downstream |

Confirm before upgrading:

- Test `helm push` against the registry explicitly. The 3.22.0 token-scope fix
  changes how the push scope is requested, and the release notes do not name
  specific registries, so verify rather than assume.
- If charts are signed or verified, re-test after the signing library change.
- Land on 3.19.0 or later. Anything at exactly 3.18.0 should move regardless.

## Stay on Helm 3

Do not move a repository to Helm 4 as part of a pipeline upgrade.

`HelmInstaller@1` documents its version input as a semantic version or
`latest`, with no major-version selector, and an open request to support that
confirms it does not exist. `HelmDeploy@0` declares nothing about Helm 4
either way, and exposes commands matching Helm 2 and 3 era semantics. Neither
task is confirmed to work with Helm 4, so treat compatibility as unknown and
verify it deliberately rather than discovering it during a release.

Plan the migration separately and on its own timeline. Helm `3.22.0` is the
final Helm 3 minor release. Helm 3 receives security-only patches until
February 2027 and nothing after that, so a Helm 4 migration needs to be
scheduled rather than deferred indefinitely.
