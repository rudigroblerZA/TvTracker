---
name: codeatout-open-knowledge-context
description: Creates or updates Open Knowledge Format context documents for AI-assisted design, implementation, fixes, and technical decisions. Use for meaningful engineering tasks that need durable decision history.
---

# Open Knowledge Context

Use `codeatout-technical-writing` and `codeatout-unslop` to keep context
records concise, unambiguous, and structured for their intended reader.

## Required output

For every meaningful invocation, create or update at least one `.context/*.md`
document. First check the consuming repository for an existing context location,
template, naming convention, or related record. Use those conventions when
present; otherwise use `.context\`.

Use this minimum Open Knowledge Format-style frontmatter:

```yaml
---
type: knowledge
title: <short human title>
description: <what this document captures>
resource: <service, feature, or component identifier>
tags:
  - <stable-tag>
timestamp: <ISO-8601 timestamp>
---
```

## Record decision context

Each context document must include:

1. `# Summary` describing the problem and target behaviour
2. `# Decision log` with dated entries containing **Decision**, **Why**, and
   **Impact**
3. `# Alternatives considered` for non-trivial choices
4. `# Follow-up` for unresolved items, risks, or questions

Prefer one context document per capability or feature. Reuse an existing
document for related work; create a new document when the scope is unrelated.

## Quality and safety

- Capture specific decisions, constraints, trade-offs, and behavioural impact,
  not generic implementation summaries.
- Lead each section with its decision, fact, or open question. State one
  decision or claim per sentence, name the affected component explicitly, and
  remove repeated context that does not change the record's meaning.
- Keep titles, resource identifiers, and tags stable and searchable.
- Do not include secrets, credentials, personal data, raw production data,
  internal incident details, or unnecessary copied source code.
