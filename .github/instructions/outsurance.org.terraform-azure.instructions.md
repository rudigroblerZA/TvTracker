---
description: 'Organisation-wide guidance for secure Azure infrastructure managed with Terraform'
applyTo: '**/*.terraform, **/*.tf, **/*.tfvars, **/*.tflint.hcl, **/*.tf.json, **/*.tfvars.json'
---

# Azure Terraform Development

Inspect existing Terraform structure, provider constraints, backend, environment
configuration, and deployment policy before making changes. Preserve the
repository's established module and file layout unless a structural change is
explicitly requested.

- Use typed, documented variables and only necessary, documented outputs.
- Keep secrets out of Terraform configuration, variable files, output values,
  logs, and source control. Prefer managed identities and approved secret
  stores.
- Use least-privilege RBAC, appropriate network controls, resource tags, and
  diagnostic settings according to the workload's requirements.
- Prefer Azure Verified Modules when suitable and allowed by the repository's
  dependency policy.
- Use implicit dependencies when resource references already establish order.
- Use stable `for_each` keys for collections where resource identity must be
  preserved.
- Use a secure remote state backend for deployed environments; never modify
  generated state or provider/module cache files directly.
- Run `terraform fmt` and `terraform validate` after edits when the tools and
  configuration are available. Obtain explicit approval before commands that
  change infrastructure.

Do not hard-code subscriptions, regions, resource names, environment layouts,
CI systems, or module registries.
