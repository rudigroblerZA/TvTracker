# Entry, Stage, and Variable Templates

Three small files surround the build steps. Keep the file layout the
repository already uses: either flat (`build-stage.yml`, `build-steps.yml`,
`build-variables.yml`) or nested (`build\stage.yml`, `build\steps.yml`,
`build\variables.yml`). Only the template paths differ between them.

## Entry pipeline

`azure-pipelines.yml` declares the pool, triggers, shared variables, and the
stage list. This is where the pool upgrade is applied.

```yaml
pool:
  name: azmdp-devp-we-agents
  demands:
    - ImageOverride -equals ubuntu-latest

pr:
  branches:
    include:
      - master
    exclude:
      - hotfix/*
  paths:
    exclude:
      - README.md

trigger:
  batch: true
  branches:
    include:
      - master
    exclude:
      - hotfix/*
  paths:
    exclude:
      - README.md

variables:
  - group: Azure Container Registry
  - name: appVersion
    value: $(Build.BuildNumber)
  - name: helmVersion
    value: "3.22.0" # standard version; reuse the repository's own value when one exists and upgrade it separately

stages:
  - template: build-stage.yml
    parameters:
      displayName: build
      stageName: build
      containerRegistry: https://outitdev.azurecr.io/
      sonarQube: https://sonarqube.outsurance.co.za/
```

Release stages follow the build stage. Leave the existing release stage list,
its parameters, and its environment names exactly as they are.

Where release stages must not run for pull request builds, either guard each
stage with `condition: and(succeeded(), ne(variables['Build.Reason'],
'PullRequest'))` or wrap the whole list in
`${{ if ne(variables['Build.Reason'], 'PullRequest') }}:`. Keep whichever form
the pipeline already uses.

## Build stage

`build-stage.yml` passes parameters through to the steps and pulls in the
build variables. Use `workspace: clean: all` when the repository already does.

```yaml
parameters:
  - name: containerRegistry
    type: string
  - name: dependsOn
    default: ""
    type: string
  - name: displayName
    type: string
  - name: sonarQube
    type: string
  - name: stageName
    type: string

stages:
  - stage: ${{ parameters.stageName }}
    dependsOn: ${{ parameters.dependsOn }}
    displayName: ${{ parameters.displayName }}
    variables:
      - template: build-variables.yml

    jobs:
      - job: build
        displayName: build job
        workspace:
          clean: all

        steps:
          - template: build-steps.yml
            parameters:
              containerRegistry: ${{ parameters.containerRegistry }}
              sonarQube: ${{ parameters.sonarQube }}
```

## Build variables

`build-variables.yml` holds the variable groups and SonarQube identity. The
`GitHub Copilot Service Account` group supplies `github-pat` and `devops-pat`
for the Copilot code review step; without it the step fails to authenticate.

```yaml
variables:
  - group: GitHub Copilot Service Account
  - name: Sonar.ProjectKey
    # Same as repo name, all lowercase
    value: contoso.orders.service
  - name: Sonar.ProjectName
    # Same as repo name, first letter capitalised
    value: Contoso.Orders.Service
```

Add repository-specific build variables here rather than inline in steps, for
example an image repository name, Dockerfile path, and build context used by
more than one task.

## Infrastructure-only repositories

A Terraform-only repository has no .NET build. Its build stage publishes the
Terraform files and runs the Copilot review:

```yaml
steps:
  - checkout: self
    fetchDepth: 0
    clean: true
    persistCredentials: true

  - publish: $(Build.SourcesDirectory)/source/environments
    artifact: terraform
    displayName: publish terraform files

  - task: CopilotCodeReview@1
    displayName: Copilot Code Review
    condition: and(succeeded(), eq(variables['Build.Reason'], 'PullRequest'))
    continueOnError: true
    inputs:
      githubPat: $(github-pat)
      azureDevOpsPat: $(devops-pat)
```

Its `build-variables.yml` needs only the Copilot variable group. Because no
code is analysed, the branch policy's SonarQube quality gate check has to be
marked not applicable for the pull request; keep any existing step that does
this and do not add one where it does not already exist.
