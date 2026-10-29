---
name: codeatout-summit-universe-write-unit-test
description: Write a Rocket UniVerse BASIC unit test for a Summit production program when the account already has the Summit UniVerse Unit Test Framework installed. Use when adding a unit, regression, or test case for a named Summit BP program, or testing a changed Summit routine.
---

# Write a unit test for a Summit program

This skill turns "test `{PROGRAM}`" into one compilable `TEST.{PROGRAM}` item plus its `TEST.MAP`
record, using the test framework that's already installed in this estate (`TEST.ASSERT.*`,
`TEST.START.PROGRAM`/`TEST.START.CASE`, `TEST.PROGRAM.SUMMARY`, `TEST.RUN.ONE`/`MAPPED`/`ALL`,
`TEST.MAP`). It does not design or modify that framework — it consumes it. If any of those framework
items don't exist yet in this account, say so and stop; that's a [Summit UniVerse development](../codeatout-summit-universe-development/SKILL.md) task, not
this one.

Read [framework-api.md](references/framework-api.md) once per session before writing a test — it's the condensed contract
for every framework call this skill uses. Skim [templates.md](references/templates.md) for the two test-program
skeletons (pure calculation vs. file-touching) and the `TEST.MAP` record shape.

## Why this is a discovery task before it's a writing task

A unit test is only as trustworthy as its understanding of what the routine actually does. Guessing a
routine's argument order, what a blank input does, or whether it touches a file produces a test that
compiles but proves nothing — or worse, asserts the wrong behaviour and trains the next developer to
"fix" correct code. So before writing anything, do the same discovery a change to the routine itself
would require:

1. **Get the target routine's actual source.** Ask for it if it wasn't supplied — don't work from a
   description or a guessed signature.
2. **Resolve its program-specific include and anything it transitively pulls in**, to the extent it's
   supplied. Note any injected COMMON, macros, or numbered dictionary equates the routine relies on -
   your test cases will need to construct or reference the same shapes.
3. **If a required include, macro expansion, or equate wasn't supplied and the routine's behaviour can't
   be understood without it, say so explicitly and ask for it (or state it as an unresolved dependency in
   the delivered test's header comment) rather than inventing plausible-looking contents.** A wrong guess
   here is worse than an honest gap, because it produces a test that looks authoritative and isn't.

## Decide the test's shape: pure calculation or file-touching

Read the routine's body, not just its name, to answer this:

- **No file access, no locks, no application COMMON dependency** → an in-memory test: build input
  records inline in the test program, call the routine, assert on its outputs. This is the common case
  and the default pattern in [templates.md](references/templates.md).
- **Touches `READ`/`READU`/`WRITE`/`DELETE`, `B.IO.HANDLER`, locks, or a transaction** → this needs the
  file-integration pattern instead: an isolated, non-production test file, unique test-only keys, a
  cleanup step that runs both before (defensive) and after the test (even on failure), and a look at the
  routine's lock/transaction lifecycle before you write assertions about it. Never point a test at a
  production file or production keys — if the only file available to test against *is* a production file,
  say that plainly and ask whether an isolated test file should be created, rather than writing a test
  that reads or writes real data.

This decision also sets `FAST.SUITE.FLAG` on the `TEST.MAP` record: `Y` for the fast in-memory case, `N`
for anything touching a file (integration tests don't belong in the suite developers run on every build).

If the routine's I/O goes through company macros (`SU.OPEN.FILE`, `B.IO.HANDLER`) whose exact call
signature you don't have, don't reconstruct a plausible-looking call. Write the surrounding test logic for
real, and mark the specific unresolved call as a flagged placeholder in a header comment naming exactly
what's missing - the same way you'd flag it in production code you couldn't verify.

## Check for an existing test before writing a new one

Look up the routine's name in `TEST.MAP`. If a `TEST.{PROGRAM}` already exists:

- Read it before touching it. Add new `TEST.START.CASE`/`TEST.END.CASE` blocks for whatever isn't already
  covered (a bug being fixed, a scenario the developer just mentioned, a boundary that's missing).
- Don't create a second test program for the same routine, and don't delete or rewrite passing cases you
  don't have a reason to change - the point of a regression suite is that old cases keep proving old
  behaviour still holds.
- If every case the developer asked for is already present, say so instead of manufacturing a redundant
  case just to have produced something.

If no mapping exists, this is a new test program: write it, then add its `TEST.MAP` record (see
[templates.md](references/templates.md) for the exact fields and the compound-key convention that makes a duplicate
mapping structurally impossible).

## What every test program must do, structurally

- `CALL TEST.START.PROGRAM('TEST.{PROGRAM}')` is the first statement, always - it's what resets the
  framework's shared result state so this run can't inherit a previous run's failures.
- One `CALL TEST.START.CASE('{description}')` / `CALL TEST.END.CASE` pair per scenario.
- Every check is a `CALL TEST.ASSERT.*(...)` (`EQUAL`, `NOT.EQUAL`, `TRUE`, `FALSE`, `EMPTY`,
  `NOT.EMPTY`, `CONTAINS`, `RECORD.EQUAL` - pick the one that states the intent most directly, don't
  reach for `EQUAL` on everything). Never write a bespoke `IF ... THEN PRINT` check - that bypasses the
  structured result collection the whole framework exists to provide.
- Last statements: `CALL TEST.PROGRAM.SUMMARY(FAIL.COUNT, RUN.STATUS)` then `STOP RUN.STATUS`.

At minimum, cover: a normal/expected-result case, a boundary case (a limit, a zero, a just-over/just-under
value - whatever the routine's own logic treats as a threshold), a blank-or-invalid-input case, a
structured-error case if the routine can return one, and a regression case for any defect the developer
names. Don't pad the test with cases that don't correspond to a real behaviour of the routine just to hit
this list - five thin cases are worse than three that actually probe the routine's logic.

## Reporting back

State plainly:

- Which cases you added and why each one matters (what it would catch).
- Any unresolved dependency you flagged instead of guessing, and what's needed to resolve it.
- That the test has not been compiled, catalogued, or run unless you actually have live account access to
  do that - never imply a test passed without having run it.
- The exact `TEST.MAP` record you added or confirmed already exists.
