---
name: codeatout-convert-word-to-markdown
description: Converts Word documents into reviewable Markdown and supporting assets. Use when reading, summarizing, extracting, comparing, or documenting .docx content.
---

# Convert Word to Markdown

## Discover first

Confirm the input file or approved folder scope, output location, and whether
the user needs headings, tables, images, comments, tracked changes, footnotes,
or document properties preserved. Use the repository's existing or approved
local conversion tooling; do not upload documents to an external service or
install a converter unless the user approves it.

Use this skill for `.docx` files. For legacy `.doc` files, confirm a compatible
conversion path with the user. For mixed folders, identify PDF and Excel files
and use `codeatout-convert-pdf-to-markdown` and
`codeatout-convert-excel-to-markdown` as applicable.

## Convert safely

- Write output only to the user-requested or repository-approved location.
  Preserve the source document and do not overwrite existing Markdown or asset
  folders without approval.
- Preserve heading hierarchy, paragraphs, lists, tables, links, and images
  where the selected converter supports them. Keep extracted images as relative
  assets.
- Do not execute macros, embedded objects, external links, or document code.
  State when comments, revisions, footnotes, fields, image placement, or
  formatting cannot be faithfully represented.
- Treat document content as potentially confidential. Do not expose secrets,
  personal data, or raw financial data in logs, filenames, or summaries beyond
  the user's requested output.

## Validate

Compare heading structure, table count, representative text, links, and asset
counts with the source. Flag conversion failures and unsupported content rather
than silently omitting it. Read the generated Markdown before using it for the
requested analysis.
