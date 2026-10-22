# Upgrade Checklist

Work through this list against the pipeline being upgraded. Each row is a
self-contained change. Apply only the rows that actually match the file, and
change the task inputs together with the task version.

## Agent pool

| Current | Replace with |
| --- | --- |
| `vmImage: ubuntu-latest` | pool block below |
| A named pool without `demands` | pool block below |
| An older named agent pool | pool block below |

```yaml
pool:
  name: azmdp-devp-we-agents
  demands:
    - ImageOverride -equals ubuntu-latest
```

The pool belongs in the entry pipeline. Remove any per-job pool override that
was only compensating for a stale top-level pool.

## Task versions

| Old | New | Input changes |
| --- | --- | --- |
| `trivy@1` | `trivy@2` | `path:` becomes `target:` with `type: filesystem`; drop `trivyImage:` and rely on `--db-repository` |
| `replacetokens@3`, `@5` | `replacetokens@6` | `targetFiles:` becomes `sources:` with a full path glob; `rootDirectory` is folded into that glob; `actionOnMissing: continue` becomes `missingVarLog: off`, `warn` becomes `missingVarLog: warn`, and `fail` becomes `missingVarLog: error`; `tokenPrefix`, `tokenSuffix`, `keepToken`, `writeBOM`, and `encoding` are dropped unless a non-default token pattern is in use, in which case set `tokenPattern:` |
| `reportgenerator@4` | `reportgenerator@5` | none |
| `PublishCodeCoverageResults@1` | `PublishCodeCoverageResults@2` | none for Cobertura input |
| `SonarQubePrepare@5`, `@6` | `SonarQubePrepare@7` | none |
| `SonarQubeAnalyze@5`, `@6` | `SonarQubeAnalyze@7` | none |
| `SonarQubePublish@5`, `@6` | `SonarQubePublish@7` | none |
| `gitversion/setup@0`, `@1` | `gitversion/setup@3` | keep `versionSpec: 5.x` |
| `gitversion/execute@0`, `@1` | `gitversion/execute@3` | keep `useConfigFile` and `configFilePath` |
| `OctopusPush@3` | `OctopusPush@4` | none |
| `qetza.replacetokens.replacetokens-task.replacetokens@6` | `replacetokens@6` | same inputs; the short name is preferred |

Do not change `Docker@2`, `HelmDeploy@0`, `HelmInstaller@1`,
`PublishTestResults@2`, `CopyFiles@2`, `UseDotNet@2`, `DotNetCoreCLI@2`,
`NuGetAuthenticate@1`, `NuGetToolInstaller@1`, or `CopilotCodeReview@1`. Those
are the current versions.

## Missing baseline steps

| Symptom | Fix |
| --- | --- |
| Entry pipeline has no `pr:` block | Add one mirroring the `trigger:` branches, otherwise pull-request-only steps never run |
| Checkout has no `persistCredentials: true` | Add it; the tag step cannot push without it |
| Checkout has no `fetchDepth: 0` | Add it; GitVersion needs full history |
| No `clean: true` on checkout | Add it |
| Tags are needed by versioning but not fetched | Add `fetchTags: true` to the checkout only when the repository reports a versioning problem or the requester asks for it. Otherwise leave it: `fetchDepth: 0` already retrieves tags on most agents, and adding it speculatively is not a required change |
| No GitVersion tasks | Add setup and execute before SonarQube prepare |
| No tag step | Add the create tag script after the Copilot review step |
| Tag step present but no `persistCredentials` on checkout | Fix the checkout |
| No `CopilotCodeReview@1` anywhere | Add it after the SonarQube publish step, conditioned to pull requests, with `continueOnError: true` |
| Copilot review already runs in a separate `code_review` stage | Leave it alone; do not add a second one in the build steps |
| Copilot review present but `github-pat` or `devops-pat` unresolved | Add `- group: GitHub Copilot Service Account` to the build variables file |
| No Trivy tasks | Add filesystem scans after the ACR login and image scans after the Docker push tasks |
| One broad Trivy filesystem scan already covers the source | Leave it as is; splitting it into per-project scans narrows coverage |
| Trivy tasks run before the ACR login | Move the ACR login above them |
| A deployable is packaged without a container, for example via Octopus | Scan its source directory with a filesystem scan like any other deployable |
| No test step | Add `dotnet test` over all test project globs with coverage collection |
| Tests run but no results published | Add `reportgenerator@5`, `PublishCodeCoverageResults@2`, and `PublishTestResults@2` |
| No SonarQube tasks | Add prepare, analyze, and publish with the shared conditions |
| `SonarQubeAnalyze@7` without `continueOnError: true` | Add it |
| `SonarQubePublish@7` without `continueOnError: true` | Leave as is; add it only when introducing the step |
| Docker build and push without an ACR login | Add the `Docker@2` login task before the first scan or push |
| `helm push` without `helm registry login` | Add the login bash step before the first push |
| Helm registry login present but credential variables unresolved | Add the `Azure Container Registry` variable group to the entry pipeline variables |
| `helm package` without a preceding `HelmInstaller@1` | Add the installer, pinned to the `helmVersion` variable |
| Helm steps present but no `helmVersion` variable | Declare it in the entry pipeline; do not inline a literal into the installer |
| A step hardcodes a Helm or Terraform version literal where a variable exists | Replace the literal with the variable only if you are already editing that file; otherwise report it |
| Pinned version is older than the standard version (Helm `3.22.0`, Terraform `1.16.4`) | Recommend the upgrade and explain the benefit, but apply it only after the requester confirms and after checking breaking changes. Never bundle it into the pipeline upgrade |
| Pipeline pins Helm 4.x | Ask. The Azure DevOps Helm tasks are not confirmed against Helm 4 |
| Repository has Terraform but no plan stage | Add the pull-request plan stage |
| Trivy reports an accepted finding with no `.trivyignore` | Add the file at the repository root |
| An explicit `dotnet restore` uses a repository `NuGet.Config` | Keep it; do not fold it into the build step |

## Condition drift

Only two condition problems are worth fixing: a step that runs in the wrong
build type, and a release stage that runs during pull request validation.
Everything else in this section is deliberately "leave it and report it".

| Symptom | Fix |
| --- | --- |
| Deployment-side steps conditioned on `eq(variables['Build.SourceBranchName'], 'master')` | Leave it. It differs from `ne(..., 'PullRequest')` only for non-pull-request builds of other branches such as hotfix branches. Report the difference to the requester instead of changing it |
| Copilot review runs on non-pull-request builds | Restrict to `eq(variables['Build.Reason'], 'PullRequest')` |
| Release stages run during pull request validation | Guard them with a non-pull-request condition or an `${{ if }}` wrapper |
| Variables named `AppVersion`, `ContainerVersion`, or `BuildConfiguration` rather than the reference names | Keep the repository's names and reference them in any step you add |

Leave any other existing condition alone. Conditions frequently encode
deliberate branch policy that is not visible from the YAML.

## Things not to change

- Image repository names, chart names, chart file names, and artifact names.
  Release stages and deployed workloads depend on them.
- Existing `sonar.exclusions`, `sonar.coverage.exclusions`, and
  `sonar.cpd.exclusions` values.
- Existing `.trivyignore` entries.
- Pinned Helm and Terraform versions. Reuse what the repository already
  declares, and treat any upgrade as a separate, confirmed change rather than
  part of the pipeline upgrade. `TerraformInstaller@0` is left alone too: it
  takes the same `terraformVersion` input as `@1`, and it lives in release
  steps rather than the build pipeline.
- Repository-specific steps such as network route scripts, VPN secure files,
  Octopus packaging, or extra publish artifacts.
- Release, hotfix, events, and dependabot pipelines, unless they were part of
  the request.
