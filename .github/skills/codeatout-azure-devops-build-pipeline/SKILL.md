---
name: codeatout-azure-devops-build-pipeline
description: Upgrades an existing Azure DevOps build pipeline, or creates a new one, for .NET API, worker, service, and infrastructure repositories so it includes the current agent pool, SonarQube, Trivy, Copilot review, test, tagging, container, Helm, and Terraform plan patterns. Use when a build pipeline is outdated, missing quality or security gates, or a backend repository needs a build pipeline.
---

# Azure DevOps Build Pipeline

Applies to backend .NET repositories: APIs, workers, schedulers, proxies, and
Terraform infrastructure repositories. It does not apply to UI or front-end
repositories, which use a different build shape.

Change only what the baseline requires. Preserve every existing working step,
task input, condition, project glob, repository name, image name, chart name,
and artifact name unless it is explicitly listed as an upgrade below. When the
repository's intent is unclear, ask rather than guess.

## Discover first

Before editing, gather evidence from the repository. Folder and file names in
this skill are illustrative: match on purpose, not on exact spelling. A
Terraform directory may be called `terraform`, `tf`, `infra`, or
`source\environments`; a source directory may be `src` or `source`.

1. Pipeline files. Look under `.azuredevops\pipelines\`, `azure\`, or the
   repository root for `azure-pipelines*.yml`. Note whether templates use a
   flat layout (`build-stage.yml`, `build-steps.yml`, `build-variables.yml`)
   or a folder layout (`build\stage.yml`, `build\steps.yml`,
   `build\variables.yml`). Keep the layout that is already in use.
2. Shared templates. Check whether more than one entry pipeline includes the
   same build stage or steps template. Repositories often have a main
   pipeline and a release or hotfix pipeline that both reuse the build steps.
   An edit to a shared steps file changes every pipeline that includes it, so
   list those pipelines and confirm the change is safe for all of them.
3. Solution and projects. Find `*.sln` or `*.slnx`, the source folder name,
   test project naming, and `global.json`.
4. Versioning. Confirm `GitVersion.yml` exists and note its path.
5. Coverage. Confirm `Coverlet.runsettings` exists and note its path.
6. Deployable units. List each `Dockerfile` and its owning project, plus any
   application packaged without a container.
7. Charts. List each chart directory and the package name each one produces.
8. Terraform. Note every Terraform root, not just the first. Some
   repositories have a second, conditionally applied root. Note the
   per-environment release variable files.
9. Packages. Note whether the repository publishes NuGet packages.
10. Existing exclusions. Note SonarQube `sonar.exclusions`,
    `sonar.coverage.exclusions`, `sonar.cpd.exclusions`, and any
    `.trivyignore` entries. Carry all of them forward unchanged.

## Organisation defaults to confirm

These are the current organisation-wide values. Confirm them against the
repository and with the requester before applying them to a pipeline that
currently uses something different.

| Setting | Value |
| --- | --- |
| Agent pool | `azmdp-devp-we-agents` |
| Pool demand | `ImageOverride -equals ubuntu-latest` |
| Container registry | `outitdev.azurecr.io` |
| Registry service connection | `https://outitdev.azurecr.io/` |
| SonarQube service connection | `https://sonarqube.outsurance.co.za/` |
| Trivy database repository | `outitdev.azurecr.io/aquasecurity/trivy-db` |
| Shared pipeline templates repository | `CodeAtOUT/template.pipelines` |

Required variable groups:

| Variable group | Provides | Needed for |
| --- | --- | --- |
| `GitHub Copilot Service Account` | `github-pat`, `devops-pat` | Copilot code review |
| `Azure Container Registry` | `container-registry-name`, `container-registry-login`, `container-registry-password` | Helm registry login and push |
| `K8s Service Principal` | cluster credentials | Terraform plan and release stages |
| `PreProd - Terraform CLI - KV` | `terraform-*` backend credentials | Terraform plan and release stages |

Some repositories name the registry group `Azure Container Registry
Credentials`. Keep whichever name the repository already resolves.

## Tool versions

Helm and Terraform versions are discovered from the repository, not imposed by
this skill. Reuse whatever the repository already pins. Upgrading a pipeline to
the current pattern must never change which tool version a pipeline runs.

| Tool | Variable | Declared in | Consumed by |
| --- | --- | --- | --- |
| Helm | `helmVersion` | entry pipeline `variables` | `HelmInstaller@1` |
| Terraform | `terraform_version` | release variables file | the shared plan template |

Rules:

- Never declare a Terraform version in the build pipeline. The shared plan
  template takes no version parameter; it inherits `terraform_version` from
  the file given as `globalVariablesAbsoluteFilePath`. That inheritance is
  what keeps plan and apply on the same version, so point the plan stage at
  the same globals file the release stages use.
- Some repositories also declare a `TerraformVersion` alias whose value is
  `$(terraform_version)`. Search for both spellings before concluding a
  version is missing.
- If Helm steps exist but `helmVersion` is not declared, add the variable to
  the entry pipeline. Do not inline a literal version into `HelmInstaller@1`.
- If a step hardcodes a version literal where the repository has a variable
  for it, replace the literal with the variable only when you are already
  editing that file for another baseline reason. Otherwise report it.

## Standard versions

Use these for a new pipeline, and recommend them when an existing pipeline
pins something older.

| Tool | Standard version | Declared in |
| --- | --- | --- |
| Helm | `3.22.0` | entry pipeline `helmVersion` |
| Terraform | `1.16.4` | release variables `terraform_version` |

Helm stays on the 3.x line. Helm 4 is generally available, but the Azure
DevOps Helm tasks have not been confirmed against it, so do not move a
repository to Helm 4 as part of a pipeline upgrade. Note that `3.22.0` is the
final Helm 3 minor release and Helm 3 receives security-only patches until
February 2027, so a Helm 4 migration should be planned separately rather than
deferred indefinitely.

When a repository pins something older than the standard version, recommend
the upgrade clearly and explain the benefit, but treat it as a decision
separate from the pipeline upgrade. Apply it only after the requester
confirms, and only after checking for breaking changes between the pinned
version and the standard version. See
[version-upgrades.md](references\version-upgrades.md) for what changed and
what to verify.

Never bump a version silently, and never bundle a version bump into a pipeline
upgrade as though it were part of the pattern. The two must be reviewable
separately, because a pattern change that breaks a build is easy to attribute
and a version bump that breaks a deployment is not.

Terraform carries the greater risk of the two and has an additional
constraint. State is written forward-only: once a newer Terraform has planned
or applied against a workspace, an older version can no longer read that
state. So every pipeline that touches a given workspace, plan and apply
alike, must move together. Upgrading one repository's build pipeline in
isolation will break the release pipelines still on the old version. Confirm
the scope of the change before applying it, and confirm the new version still
satisfies `required_version` in the Terraform configuration.

## Required baseline

Every build pipeline must contain all of the following.

1. The current pool declaration in the entry pipeline.
2. A `pr:` trigger in the entry pipeline. Without one, no pull request build
   runs, so the Copilot review and the pull-request SonarQube analysis never
   execute even though the steps are present.
3. `checkout: self` with `fetchDepth: 0`, `clean: true`, and
   `persistCredentials: true`. Credential persistence is mandatory because the
   tag step pushes back to the repository.
4. GitVersion setup and execute against the repository's `GitVersion.yml`.
5. SonarQube prepare, analyze, and publish using the `@7` tasks.
6. A full build of the solution.
7. At least one Trivy filesystem scan covering the source that is built and
   deployed. Where a pipeline already has a working scan, keep its existing
   scope; do not split one broad scan into several narrower ones, because that
   silently reduces coverage. Add per-project scans only where no scan exists.
8. A test run over all test projects with coverage collection, followed by
   coverage and test result publishing.
9. `CopilotCodeReview@1`, conditioned to pull request builds only. If the
   repository already runs the review in a separate `code_review` stage, keep
   that stage and do not add a duplicate step.
10. A Git tag step conditioned to non-pull-request builds.
11. `.trivyignore` at the repository root when suppressions are required.

Add these conditionally, and only when the repository already shows evidence
that it needs them. Adding a block the repository has never used is out of
scope for an upgrade; raise it with the requester instead.

| Block | Evidence that it applies |
| --- | --- |
| Container images | A `Dockerfile` exists and the pipeline already pushes images |
| Helm charts | A `charts\` directory exists and the pipeline already packages charts |
| NuGet packages | The pipeline already packs or pushes packages, or the requester states the repository is consumed as a package |
| Terraform plan | A `terraform\` or environments directory exists with per-environment release variables files |
| Non-container deployables | The pipeline packages an application another way, for example an Octopus package; scan its source directory like any other deployable |

## Canonical step order

```text
checkout
UseDotNet (global.json)
NuGetToolInstaller / NuGetAuthenticate
gitversion setup / execute
SonarQubePrepare@7
dotnet build
Docker login (ACR)
trivy@2 filesystem scans
dotnet test
reportgenerator@5
PublishCodeCoverageResults@2
PublishTestResults@2
SonarQubeAnalyze@7 / SonarQubePublish@7
CopilotCodeReview@1        (pull requests only)
create tag                 (non-pull-request only)
dotnet pack / push         (default branch only, when publishing packages)
dotnet publish             (non-pull-request only)
CopyFiles (Dockerfile)     (non-pull-request only)
Docker build and push      (non-pull-request only)
trivy@2 image scans        (non-pull-request only)
replacetokens@6
HelmInstaller@1
helm package / registry login / push
publish artifacts
```

The ACR login must precede the first Trivy task, because Trivy pulls its
vulnerability database from the registry.

This order governs where to insert steps you add. Do not reorder existing
working steps to match it: reordering changes what has already run when a step
executes, and the benefit rarely justifies the risk. The single exception is
an ACR login that sits after a Trivy task, which must be moved up.

Where a repository builds several images or charts, repeat the per-unit steps
as a group, keeping each Trivy image scan with the Docker task that pushed
that image rather than batching all scans at the end.

See [the annotated build steps reference](references\build-steps-reference.yml)
for the complete file, and
[the entry and stage templates](references\entry-and-stage-templates.md) for
the surrounding pipeline, stage, and variable files. Attributes in the
reference that are not named in the baseline above, such as
`retryCountOnTaskFailure`, are recommended for new files but are not a reason
to edit an existing working step.

## Conditions

These are the conditions to use **for steps you add**. An existing step that
already has a condition keeps it: the repository's branch policy is not
visible from the YAML, and rewriting a working condition can start or stop
publishing without anyone noticing. The one exception is a condition that is
outright wrong, such as a Copilot review that runs outside pull requests.

| Step group | Condition for newly added steps |
| --- | --- |
| SonarQube prepare, analyze, publish | `and(succeeded(), or(eq(variables['Build.SourceBranchName'], 'master'), eq(variables['Build.Reason'], 'PullRequest')))` |
| Copilot code review | `and(succeeded(), eq(variables['Build.Reason'], 'PullRequest'))` |
| Tag, publish, images, charts | `and(succeeded(), ne(variables['Build.Reason'], 'PullRequest'))` |
| NuGet pack and push | `and(succeeded(), eq(variables['Build.SourceBranchName'], 'master'))` |

Where the repository's default branch is not `master`, substitute its actual
default branch name in every condition.

Many older pipelines gate deployment-side steps on
`eq(variables['Build.SourceBranchName'], 'master')` rather than on
`ne(variables['Build.Reason'], 'PullRequest')`. The two differ only for
non-pull-request builds of other branches, such as hotfix branches. Leave the
existing form in place and tell the requester about the difference rather than
changing it yourself.

`SonarQubeAnalyze@7` and `CopilotCodeReview@1` carry `continueOnError: true`,
so that a reporting outage does not fail the build. `SonarQubePublish@7` may
carry it too; keep whichever the pipeline already uses, and use
`continueOnError: true` when adding the step fresh.

## Terraform repositories

When the repository contains Terraform, add a pull-request plan stage that
covers every environment using the shared templates repository. This requires
a `resources.repositories` entry and one entry per environment variables file.
See [the Terraform plan pattern](references\terraform-plan.md).

## Upgrading an existing pipeline

Work through this sequence and stop at the first thing you cannot verify.

1. Record the current pipeline's behaviour: stages, conditions, artifact
   names, image repositories, and chart names.
2. Apply the pool upgrade in the entry pipeline.
3. Add missing baseline steps in the canonical order, placing each new step
   relative to the existing steps rather than rewriting the file.
4. Upgrade task versions per
   [the upgrade checklist](references\upgrade-checklist.md). Task inputs
   changed between versions; update inputs together with the version.
5. Add any newly required variable group reference to the build variables
   file, not to individual steps.
6. Add or extend `.trivyignore` only when a scan requires it. See
   [the Trivy ignore reference](references\trivyignore.md).
7. Leave release, hotfix, events, and dependabot pipelines untouched unless
   the requester asked for them. Treat a file as a release or hotfix pipeline
   when its name or triggers say so, even if it also invokes the build
   templates. Where such a pipeline shares the build steps file you edited,
   say so explicitly when reporting the change, because your edit applies to
   it too.
8. Report, rather than fix, anything you find that is already broken or that
   the skill does not cover. Examples: a second Terraform root, environments
   split across two entry pipelines, an artifact whose source directory no
   longer exists, or a condition that looks inconsistent with its neighbours.

## Creating a pipeline for a new repository

Create four files using the repository's preferred layout:

- `azure-pipelines.yml` — pool, triggers, shared variables, stage list.
- `build-stage.yml` — stage, variables template, single build job.
- `build-variables.yml` — Copilot variable group and SonarQube project keys.
- `build-steps.yml` — the canonical steps.

Set the SonarQube project key to the repository name in lowercase and the
project name to the repository name as written. Include only the conditional
blocks the repository actually needs, and add `.trivyignore` only once a scan
reports an accepted finding.

## Validate

1. Confirm the YAML parses and that every `template:` path resolves.
2. Confirm every `$(variable)` used by a step is supplied by a variable group
   or a variable declaration reachable from that stage. Reuse the variable
   names the pipeline already defines; some repositories use `AppVersion` or
   `ContainerVersion` rather than `appVersion`.
3. Confirm each `dotnet publish` project, `Dockerfile` path, `buildContext`,
   chart path, and pushed chart file name matches a real repository path. A
   chart's folder name and its packaged `.tgz` name are often different; check
   both.
4. Confirm each Trivy image target matches a repository and tag that an
   earlier Docker task actually pushed.
5. Confirm image repositories, chart names, and artifact names are unchanged
   from the pipeline you upgraded, so release stages keep working.
6. Confirm the tag step still has `persistCredentials: true` on the checkout.
7. Confirm a `pr:` trigger exists, so pull-request-only steps can run.
8. Re-read the diff and remove any change that is not required by this skill.

If validation uncovers a problem that already existed before your change, such
as a missing artifact source directory or a chart the repository no longer
contains, report it to the requester. Do not repair it as part of the upgrade
and do not invent a replacement path.
