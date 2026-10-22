---
name: codeatout-convert-pdf-to-markdown
description: Converts PDF documents into reviewable Markdown and supporting assets. Use when reading, summarizing, extracting, comparing, or documenting PDF content.
---

# Convert PDF to Markdown

## Discover first

Confirm the input file or approved folder scope, output location, and whether
the PDF is text-based, scanned, protected, form-based, or image-heavy. Use the
repository's existing or approved local conversion tooling; do not upload PDFs
to an external service or install a converter unless the user approves it.

For mixed folders, identify Excel and Word files and use
`codeatout-convert-excel-to-markdown` and
`codeatout-convert-word-to-markdown` as applicable.

## Convert safely

- Write output only to the user-requested or repository-approved location.
  Preserve the source PDF and do not overwrite existing Markdown or asset
  folders without approval.
- Preserve document order, headings, lists, tables, page references, and
  embedded images where the selected converter supports them.
- Do not claim that text, reading order, tables, form fields, annotations, or
  image placement are exact when layout extraction is ambiguous.
- Identify scanned or image-only PDFs. Use OCR only when an approved local OCR
  capability is available and disclose its accuracy limitations.
- Treat document content as potentially confidential. Do not expose secrets,
  personal data, or raw financial data in logs, filenames, or summaries beyond
  the user's requested output.

## Validate

Compare page count, section order, representative text, tables, and assets with
the source. Flag password protection, corruption, unsupported fonts, OCR gaps,
or layout loss rather than silently omitting content. Read the generated
Markdown before using it for the requested analysis.
