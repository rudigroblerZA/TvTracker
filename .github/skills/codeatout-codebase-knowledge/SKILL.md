---
name: codeatout-codebase-knowledge
description: Maps and documents an existing codebase using verifiable repository evidence. Use when asked to onboard to a repository, map its architecture, or create repository-level technical documentation.
---

# Codebase Knowledge

## Establish scope

Confirm the requested audience, focus area, documentation location, and desired
depth. Inspect existing architecture, onboarding, and contributor documentation
before creating new documents; extend the established format and location where
possible. Also inspect existing `.context` records and use the
`codeatout-open-knowledge-context` skill's location and naming conventions.

For a monorepo, identify independently buildable applications, libraries, and
packages before treating the repository as one system.

## Investigate from evidence

Start with stated intent in READMEs, specifications, roadmaps, and contribution
guidance. Then verify it against source, project manifests, build files, test
configuration, deployment definitions, and runtime configuration examples.

Map the requested areas:

- Stack: languages, runtimes, frameworks, package management, and developer
  tooling
- Structure: applications, libraries, entry points, ownership boundaries, and
  generated or vendored content
- Architecture: request, command, data, integration, and asynchronous flows
- Conventions: naming, formatting, error handling, configuration, and testing
- Integrations: external APIs, persistence, authentication, messaging,
  observability, and deployment dependencies
- Testing and concerns: test levels, isolation, known risks, technical debt,
  security posture, performance-sensitive areas, and operational gaps

Use repository history only when it is available and relevant, such as to
identify high-churn areas. Do not treat activity alone as proof of a defect.

## Evidence and safety

- Separate verified facts, reasonable inferences, unknowns, and questions for
  the team. Cite the supporting file paths, configuration, command output, or
  other evidence for material claims.
- Mark a discrepancy between documentation and implementation explicitly;
  neither is authoritative without supporting evidence.
- Exclude generated artifacts, dependencies, build output, local environment
  files, secrets, personal data, production data, and raw credentials from the
  analysis and resulting documentation.
- Do not infer technology, architecture, ownership, or operational guarantees
  from a directory name, variable name, or single dependency alone.

## Document and validate

Create or update the documentation requested by the user, using the
repository's established layout. For each meaningful repository mapping, also
create or update at least one Open Knowledge Format context document according
to `codeatout-open-knowledge-context`. Reuse an existing related record when
appropriate; otherwise use the repository's context location or `.context\`.

The context record must capture the repository scope and observed state in
`# Summary`, evidence-based architectural or operational conclusions in
`# Decision log`, meaningful alternatives or uncertainties in
`# Alternatives considered`, and gaps or questions in `# Follow-up`. Use the
Open Knowledge Format frontmatter and do not include sensitive information.

Keep documentation concise, navigable, and useful for a new contributor.
Include unanswered questions and assumptions where they affect safe
implementation decisions.

Before finishing, check that the output covers the agreed scope, distinguishes
intent from observed reality, has evidence for material claims, includes the
required context record, and does not include sensitive information.
