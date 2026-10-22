---
name: codeatout-unslop
description: Removes AI-sounding filler, vague language, and formulaic phrasing from developer-facing prose while preserving its meaning and intended tone. Use when reviewing or refining technical writing, feedback, documentation, context records, or change descriptions.
---

# Unslop Technical Prose

First identify the text's audience, purpose, required format, and established
repository terminology. Preserve verified facts, required detail, technical
precision, and the author's intended tone. Do not add personality, opinions,
or claims that the source does not support.

Apply `codeatout-technical-writing` for document structure, clarity, and
reader-focused prose. This skill focuses on removing language patterns that
make technical content sound generic, inflated, or machine generated.

## Remove low-value language

Delete introductions, transitions, conclusions, and qualifications that add no
meaning. Replace vague statements with the concrete mechanism, decision,
constraint, action, result, or measurement. Name a source when attribution
matters; otherwise remove the unsupported attribution.

Prefer plain, precise language over promotional or abstract wording. Replace
general claims such as "improves performance" with the observed effect and its
evidence. Use the repository's actual symbol, path, command, option, or domain
term instead of a generic description.

Avoid:

- Stock openers, conversational filler, apologies, praise, and offers to do
  more work.
- Empty importance claims, generic conclusions, or vague attributions.
- Promotional adjectives and abstract metaphors that do not describe behaviour.
- Unnecessary hedging, synonym cycling, and forced groupings.
- Dense noun chains, unnecessary adverbs, and passive voice when the actor
  matters.
- Decorative punctuation, emojis, excessive emphasis, and headings that only
  repeat their section topic.

## Keep the writing natural

Use a direct, professional voice that fits the content. Vary sentence length
when it improves readability, but do not sacrifice clarity for style. Use
first person only when it accurately identifies an actor or opinion that
belongs in the document.

Give each concept one stable name. Replace ambiguous pronouns with the
component, behaviour, or decision they represent. Split a sentence when it
contains multiple instructions, claims, or conditions.

## Verify the revision

Before publishing, confirm that every sentence gives the reader a concrete
fact, decision, action, condition, or reason. Remove any sentence that could
appear unchanged in unrelated project documentation. Ensure the revision did
not change technical meaning, omit a necessary caveat, invent detail, or break
the required document format.
