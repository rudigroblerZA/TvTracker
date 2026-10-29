---
name: codeatout-summit-universe-development
description: Build, modify, explain, debug, and review Rocket UniVerse BASIC for Summit accounts. Use for Summit BP programs, five-argument B. objects, F. functions, B.XXX.DEF.VAL screen programs, dynamic arrays, file access, compile or catalogue work, and UniVerse code reviews.
---

# Summit UniVerse development

Use only for a Summit Rocket UniVerse account that follows the conventions documented in this skill's references. Do not apply it to a different MultiValue implementation, including UniData, jBASE, or D3, unless the user confirms compatibility.

## Required workflow

1. Identify the program type and requested outcome.
2. Inspect the relevant source, its program-specific include, all relevant transitive includes, file-equate includes and nearby approved code before proposing a change.
3. Read [company-standards.md](references/company-standards.md) for every generation or review task.
4. Read the references selected by the routing table below.
5. State unresolved business rules, record layouts, include expansions or routine signatures. Never invent them.
6. Prefer the smallest change matching surrounding approved code.
7. Trace error, lock and transaction paths before returning code.
8. Compile and catalogue when the environment permits. Never claim a command ran when it did not.
9. Report the change, assumptions, compiler result, catalogue result, tests and remaining risks.

## Reference routing

- Functions, five-argument objects or screen programs: read [program-patterns.md](references/program-patterns.md).
- Includes, macros or record attributes: read [includes-and-equates.md](references/includes-and-equates.md).
- Reads, writes, update locks, soft locks or transactions: read [io-locks-transactions.md](references/io-locks-transactions.md).
- Reviews: read [review-checklist.md](references/review-checklist.md) plus every domain reference relevant to the code.

## Core constraints

- Generate valid Rocket UniVerse BASIC.
- Use the program-specific include as the entry point to shared definitions.
- Prefer existing company functions, subroutines, macros and I/O handlers.
- Use numbered dictionary equates such as `CD$206$FCL_RISK_KEY`; do not use a raw attribute or legacy unnumbered alias when an approved numbered equate exists.
- Do not guess file names, attribute positions, equates, include contents, error macro expansion or routine arguments.
- Treat `EQUATE ... LIT` definitions as executable source expansions that can alter control flow.
- Use guard clauses, early returns and `CONTINUE` to limit nesting.
- Track every acquired update lock until a successful write or explicit release.
- Keep related writes atomic when partial completion would corrupt business state.
- Do not catalogue in production unless explicitly authorised.

## Build workflow

Compile first:

```text
BASIC BP <PROGRAM.NAME>
```

Only after successful compilation, catalogue the exact item:

```text
CATALOG BP <PROGRAM.NAME>
```

Capture command output. If runtime tests cannot be run, say that the program was compiled and catalogued but not functionally tested.

## Review response

First summarize the intended program flow. Then list evidence-backed findings in descending severity:

- Critical: data corruption, unbalanced transaction or unreleased lock
- High: incorrect result, invalid I/O or broken error propagation
- Medium: company-standard violation, excessive nesting or material duplication
- Low: naming, comments or local maintainability
- Clarification: behaviour cannot be verified from available sources

For each finding, identify the code, explain the concrete failure mode and propose a specific correction. Distinguish confirmed defects from risks requiring missing include or runtime evidence.
