# Code review checklist

## Understand

- Explain what the code is intended to do before judging implementation.
- Inspect surrounding programs, includes, equates and called routine signatures.
- Separate confirmed findings from assumptions and missing context.

## Correctness and maintainability

- Are variables and internal routines self-describing?
- Does main logic orchestrate focused routines?
- Can unnecessary `ELSE` blocks or nesting become guard clauses, `CONTINUE` or `EXIT`?
- Does every `LOOP` have a reachable terminating condition or `EXIT`?
- Is commented-out code explained or removable?
- Is duplicated logic a credible reuse candidate?
- Does a proposed function or subroutine match the documented characteristics?

## Includes, I/O and errors

- Is the program-specific include used and its relevant hierarchy resolved?
- Are shared libraries and I/O routines reused?
- Are numbered dictionary equates used instead of raw positions or legacy aliases?
- Does every fallible operation set an accurate `ERROR.MCODE` before checking it?
- Does error cleanup preserve the original error?
- Can cleanup recursively trigger itself?
- Is an expected missing record distinguished from a system error?

## Locks and transactions

- Build a lock ledger from each successful `READU`/`READVU` to its write or release.
- Check all early returns, continues, exits and error paths.
- Verify soft-lock cleanup independently.
- Build a transaction ledger for related writes.
- Does every failure inside a transaction roll back?
- Can any sequence leave a partial business update?

## Quality evidence

- Was `BASIC BP <PROGRAM>` run successfully?
- Was `CATALOG BP <PROGRAM>` run only after successful compilation?
- Which functional scenarios, missing-record paths, lock conflicts and rollback paths were tested?
- Are untested paths stated clearly?

Do not recommend speculative refactoring without a concrete readability, correctness, reuse or performance benefit.
