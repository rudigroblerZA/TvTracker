---
name: codeatout-skill-authoring
description: Authors and reviews compliant, portable Agent Skills for this catalog. Use when creating, changing, validating, or documenting a skill in this repository.
---

# Authoring Skills for This Catalog

Follow the [Agent Skills specification](https://agentskills.io/specification)
and this repository's `README.md`. This catalog is organisation-wide and
content-only: do not add plugin manifests, hooks, installation scripts, or
repository-specific implementation guidance.

## Establish scope

Before creating a skill, confirm that its guidance is useful across teams and
repositories. Keep technology-specific skills only when their applicability and
prerequisites are explicit.

Do not embed application names, repository paths, internal tool aliases,
tenant values, credentials, environment-specific configuration, or a mandatory
architecture. Instruct consumers to discover their repository's conventions
and apply conditional technology guidance instead.

## Create the skill

1. Choose a lowercase, hyphen-separated `codeatout-<topic>` name. It must be
   64 characters or fewer, contain only letters, digits, and hyphens, avoid
   consecutive hyphens, and exactly match its containing directory.
2. Create `.github\skills\<name>\SKILL.md` with YAML frontmatter. Include a
   specific `description` that states both the capability and when to activate
   the skill.
3. Write concise, actionable instructions that begin with repository and
   technology discovery, then state applicable principles, guardrails, and
   validation.
4. Keep `SKILL.md` under 500 lines. Move detailed optional material to focused
   files directly under `references\`, and link them with paths relative to the
   skill root.
5. Reuse the catalog's existing patterns and terminology where they remain
   portable. Update the README skills inventory when adding or renaming a
   skill.

## Required shape

```markdown
---
name: codeatout-example
description: Explains the capability and when to use the skill.
---

# Skill Title
```

Do not add optional frontmatter fields unless they provide a real,
cross-environment benefit. In particular, use `allowed-tools` only when its
support and restrictions are understood by the consuming clients.

## Validate before submitting

1. Run the README metadata validation command to confirm every skill name is
   valid and matches its parent directory.
2. Run `git diff --check` to detect whitespace errors.
3. Check every relative reference path, YAML frontmatter delimiter, description,
   and Markdown code example.
4. Confirm the entry skill is concise and detailed material uses one-level
   progressive disclosure.
5. When available, run `skills-ref validate .github\skills\<name>` for
   specification-level validation.
6. Review the diff for organisation-wide scope, no secrets, no proprietary
   project assumptions, and documentation consistency.
