---
name: codeatout-code-review
description: Performs a disciplined review of local code changes for actionable correctness, safety, security, performance, and maintainability problems. Use when a developer invokes /review or explicitly requests codeatout-code-review.
---

# Code Review

Use `codeatout-technical-writing` and `codeatout-unslop` when presenting
developer-facing findings.

## Establish review scope

Review only the current change set and code directly affected by it. Start by
examining the repository's review guidance, the working-tree and branch diff,
the changed files, related tests, relevant call sites, configuration, and
change description when available.

Determine the intended behaviour and the appropriate base revision before
assessing the changes. Use a merge-base diff when reviewing branch changes:

```text
git diff <base-revision>...<head-revision>
```

Interpret added lines as new code and removed lines as deleted code. Do not
raise pre-existing issues unless the current change directly causes or worsens
them. For refactors and organisational changes, report only newly introduced
defects or performance regressions.

## Triage before reporting

Review the complete change set before writing findings. Build an internal list
of all candidate issues, then verify each failure path against the surrounding
code and repository conventions.

Focus on observable risks in these areas:

1. Correctness: contracts, boundary conditions, state transitions,
   concurrency, error handling, backwards compatibility, and data integrity.
2. Safety and security: authorisation, input and output handling, secrets,
   sensitive-data exposure, dependencies, and unsafe defaults.
3. Reliability and operations: cancellation, timeouts, retries, observability,
   configuration, migrations, and safe failure modes.
4. Performance: unbounded work, repeated remote calls, blocking asynchronous
   work, resource leaks, and other measurable or clearly high-risk regressions.
5. Reusability, simplification, and maintainability: duplicated logic,
   unnecessary complexity, or significant divergence from established
   repository conventions.
6. Tests: failing relevant tests or missing focused coverage for changed
   behaviour and likely regressions.

Do not report subjective preferences, cosmetic style feedback already covered
by tooling, speculative concerns, or suggestions unrelated to the change.

## Score and filter findings

Classify each verified candidate by severity and confidence:

- **Critical:** A vulnerability, crash, data loss or corruption risk, or defect
  that must be fixed before merge.
- **Major:** A significant defect, performance regression, or design flaw that
  should be fixed before merge.
- **Minor:** A small but worthwhile, concrete improvement.
- **Nit:** A stylistic or preference-level observation.

- **High confidence:** The issue and failure path are verified from the code.
- **Medium confidence:** The issue is likely but cannot be fully verified.
- **Low confidence:** The issue is speculative.

Report every Critical and Major finding. Report only high-confidence Minor
findings, prioritised by impact. Do not report Nits or low-confidence findings.
Avoid duplicating existing review feedback; on a re-review, limit new findings
to newly introduced or worsened issues, except Critical findings in changed
code.

## Validate tests

Identify the repository's established test tooling and run the smallest
existing test command that covers the changed behaviour. Report relevant test
failures as findings, including failures that indicate a regression in the
change.

Assess whether new or changed behaviour has focused, meaningful automated test
coverage. Do not require tests or a coverage threshold for trivial,
non-behavioural changes; follow existing repository conventions where they
exist.

## Present the review

For each finding, provide:

- Severity and confidence.
- File and line or a precise affected code location.
- A concise explanation of the concrete failure scenario and impact.
- A focused correction path.

Keep findings self-contained, respectful, and actionable. Prefer a location
specific to the changed code. Use a concise roll-up for Minor findings, with
one short bullet per item. Lead with the impact, then state the failure path and
correction. Do not restate the change, use filler, or add generic praise. When
no reportable findings remain, state that no actionable issues were found in
the reviewed changes.
