# Terraform Plan on Pull Requests

When a repository contains Terraform, every pull request must produce a plan
for each environment the repository deploys to. Use the shared pipeline
templates repository so that plan behaviour stays consistent and no plan logic
is duplicated per repository.

## Declare the templates repository

Add a `resources` block to the entry pipeline, alongside the pool and
triggers:

```yaml
resources:
  repositories:
    - repository: templates
      type: git
      name: CodeAtOUT/template.pipelines
```

The alias `templates` is what the `@templates` suffix on the template path
refers to.

## Add the plan stage

Place the plan stage after the build stage and restrict it to pull request
builds. List every environment the repository releases to, in release order,
each pointing at the release variables file that environment already uses.

```yaml
stages:
  - template: build/stage.yml
    parameters:
      displayName: build
      stageName: build

  - ${{ if eq(variables['Build.Reason'], 'PullRequest') }}:
      - template: plan/stage.yml@templates
        parameters:
          dependsOn: build
          displayName: Terraform plan
          globalVariablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables.yml
          environments:
            - name: dev
              variablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables-dev-we.yml
            - name: devtest
              variablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables-devtst-we.yml
            - name: test
              variablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables-tst-we.yml
            - name: train
              variablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables-trn-we.yml
            - name: prod_we
              variablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables-prd-we.yml
            - name: prod_ne
              variablesAbsoluteFilePath: /.azuredevops/pipelines/release/variables-prd-ne.yml
```

## Rules

- Paths in `globalVariablesAbsoluteFilePath` and
  `variablesAbsoluteFilePath` are absolute from the repository root and use
  forward slashes, including the leading slash.
- Cover every environment the repository releases to. A plan that skips an
  environment hides drift that only appears at deployment time.
- Environment `name` values must be unique and valid stage-name fragments:
  lowercase letters, digits, and underscores. Use `prod_we` and `prod_ne`
  rather than a shared `prod`.
- Reuse the existing release variables files. Do not create plan-specific
  copies, because the plan must reflect what the release will apply.
- The plan stage runs only for pull requests. The release stages remain
  responsible for apply.
- The global variables file must supply the Terraform backend and credential
  variable groups the plan needs, typically `K8s Service Principal` and
  `PreProd - Terraform CLI - KV`, plus `terraform_version` and the
  per-environment `terraform_key`.
- Do not add a Terraform version parameter or a `TerraformInstaller` task to
  the plan stage. The template accepts no version input and reads
  `terraform_version` from the global variables file. Pointing the plan at
  the same file the release stages use is what guarantees plan and apply run
  the same version.

## When to ask instead of deciding

Stop and ask the requester when any of these apply. Each changes what the plan
stage should contain, and none can be resolved from the YAML alone.

- The repository has more than one Terraform root, for example a main root
  plus a second one applied only in certain environments. Confirm which roots
  need a plan before adding stages for them.
- The environments are split across more than one entry pipeline, so no single
  file lists them all. Confirm where the plan stage belongs and which
  environments it should cover.
- An environment has no release variables file, or two environments share one.
- The repository is Terraform-only and already marks the SonarQube quality
  gate not applicable for pull requests. Leave that arrangement alone.

## When the build also ships Terraform

For a service repository whose release stages consume Terraform from the build
artifact, keep the existing artifact publish in the build steps as well:

```yaml
  - publish: $(Build.SourcesDirectory)/terraform
    artifact: terraform
    displayName: publish terraform
    condition: and(succeeded(), ne(variables['Build.Reason'], 'PullRequest'))
```

The plan stage and the artifact publish serve different purposes; having both
is correct.
