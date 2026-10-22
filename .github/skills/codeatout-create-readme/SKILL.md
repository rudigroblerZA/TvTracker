---
name: codeatout-create-readme
description: Creates or improves a concise, evidence-based project README. Use when creating a README or substantially revising project onboarding documentation.
---

# Create a Project README

## Discover before drafting

Use `codeatout-codebase-knowledge` to establish the repository's stack,
structure, architecture, conventions, integrations, test workflow, and
unresolved questions. Inspect the existing README and related documentation
before replacing or duplicating it. Follow the repository's established tone,
documentation layout, and audience.

Do not describe a capability, command, configuration value, deployment path, or
support commitment unless it is verifiable from repository evidence or supplied
by the user.

## Structure for the reader

Adapt the README to the project rather than applying a fixed template. Include
only sections that help a new user or contributor understand and use it:

- Clear title and one-sentence purpose
- Intended use cases, key capabilities, or scope
- Prerequisites and supported environments
- Verified setup, configuration, build, run, and test commands
- Architecture or integration overview when it materially helps adoption
- Links to canonical documentation for deployment, operations, contribution,
  security, licensing, and changelogs where those documents exist

Use GitHub Flavored Markdown with descriptive headings, fenced commands, and
tables only when they improve scanning. Use images or badges only when they
already exist, are licensed for the repository, have useful alt text, and add
meaningful context.

## Safety and maintenance

- Keep credentials, tokens, connection strings, personal data, production
  endpoints, and internal-only operational details out of the README.
- Use placeholders and explain the approved configuration mechanism instead of
  embedding secrets.
- Do not create duplicate governance, contribution, license, or security
  content when a canonical document exists; link to it.
- Keep the README concise and task-oriented. Avoid generated inventories,
  speculative roadmap claims, decorative badges, and unexplained jargon.
- Preserve valid existing content unless the user asks to remove it. Highlight
  assumptions or missing information rather than inventing an answer.

## Validate

Check heading hierarchy, Markdown rendering, relative links, image paths, and
commands against the repository. Ensure the first-use path works from the
documented prerequisites and that the README reflects the current repository,
not an intended or historical design.
