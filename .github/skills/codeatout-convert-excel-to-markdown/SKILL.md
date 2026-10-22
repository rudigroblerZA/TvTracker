---
name: codeatout-convert-excel-to-markdown
description: Converts Excel workbooks into reviewable Markdown and supporting assets. Use when reading, summarizing, extracting, comparing, or documenting .xlsx workbook content.
---

# Convert Excel to Markdown

## Discover first

Confirm the input file or approved folder scope, output location, and whether
the user needs formulas, displayed values, tables, images, charts, hidden
worksheets, or comments preserved. Use the repository's existing or approved
local conversion tooling; do not upload workbooks to an external service or
install a converter unless the user approves it.

Use this skill for `.xlsx` workbooks. For legacy `.xls` files, confirm a
compatible conversion path with the user. For mixed folders, identify PDF and
Word files and use `codeatout-convert-pdf-to-markdown` and
`codeatout-convert-word-to-markdown` as applicable.

## Convert safely

- Write output only to the user-requested or repository-approved location.
  Preserve the source file and do not overwrite existing Markdown or asset
  folders without approval.
- Produce Markdown with a clear heading for each worksheet and preserve table
  headers, row order, data types, and empty cells where material.
- Keep extracted images as relative assets. State when charts, drawings,
  embedded objects, comments, hidden sheets, formulas, or formatting cannot be
  faithfully represented.
- Do not calculate, execute macros, or alter formulas. State whether converted
  values are formulas or cached/displayed results.
- Treat workbook content as potentially confidential. Do not expose secrets,
  personal data, or raw financial data in logs, filenames, or summaries beyond
  the user's requested output.

## Validate

Compare worksheet names, table counts, representative values, and asset counts
with the source workbook. Flag conversion failures, unsupported features, and
ambiguous rendering rather than inventing missing values. Read the generated
Markdown before using it for the requested analysis.
