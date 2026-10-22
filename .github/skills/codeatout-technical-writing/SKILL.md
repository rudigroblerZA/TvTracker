---
name: codeatout-technical-writing
description: Writes and reviews concise, unambiguous technical prose for developer feedback, documentation, context records, and change descriptions. Use when creating or reviewing developer-facing written content.
---

# Technical Writing

Discover the repository's documentation style, templates, terminology, and
required structure before writing. Use real file paths, symbols, commands, and
configuration names from the repository. Follow its terminology consistently.

Apply `codeatout-code-review` for review findings,
`codeatout-open-knowledge-context` for durable engineering context, and
`codeatout-create-readme` when creating or substantially revising a README.
Apply `codeatout-unslop` before publishing to remove AI-sounding filler and
formulaic phrasing without changing technical meaning.
This skill improves the prose in those outputs; it does not replace their
content or validation requirements.

## Write for the reader

Lead with the outcome, decision, action, or risk that matters. State one idea
or instruction per sentence. Put prerequisites, warnings, and conditions before
the action they qualify. Prefer active voice and direct commands for
instructions.

Use short, familiar words where they retain the required precision. Remove
filler, hedging, duplicated conclusions, empty headings, and generic
introductions. Do not replace a precise technical term, repository symbol, or
established domain term with a vague synonym.

Make references clear. Name the subject instead of relying on ambiguous words
such as "it", "this", or "they" when the reference could be unclear. Keep
modifiers, especially "only" and "not", next to the words they modify. Avoid
idioms, metaphors, slash constructions, and unexplained jargon.

## Match the content to its purpose

Keep one primary purpose per document or section:

- **Tutorial:** Guide a learner through an outcome with ordered steps and
  visible results.
- **How-to:** Give an experienced reader the shortest reliable path to a goal.
- **Reference:** Describe facts, options, limits, and errors without advice or
  opinion.
- **Explanation:** Clarify why a bounded technical decision or design exists.

Split or link content when it mixes purposes. For short feedback, change
descriptions, and context records, use the established structure rather than
adding a separate documentation format.

## Structure for scanning

Make headings state a useful point or action. Use sentence case and one level-1
heading per document. Use numbered lists only for sequences, bullets for
parallel items, and tables only when readers need to compare values.

Keep each list item parallel and introduce lists with a complete sentence.
Use fenced code blocks for executable examples and inline code for symbols,
paths, commands, options, and identifiers. Include only examples that match
the repository and task.

## Review before publishing

Confirm that the text:

1. Starts with the information the reader needs most.
2. Uses one consistent name for each concept.
3. Distinguishes verified facts, decisions, assumptions, and open questions.
4. Makes required actions, conditions, and ownership explicit.
5. Contains no unsupported claims, sensitive data, or unnecessary copied code.
6. Can be shortened without losing essential meaning.
