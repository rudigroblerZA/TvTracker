# Templates

These are starting skeletons, not fill-in-the-blank forms - adapt the case list to what the routine under
test actually does. Don't ship a test with a case that doesn't correspond to real behaviour just because
the skeleton has a slot for it.

## Pure calculation test (no file access)

```text
* Program : TEST.{PRODUCTION.PROGRAM}
* File    : BP.TEST
* Purpose : Unit test for BP/{PRODUCTION.PROGRAM}. {one line on what the routine does}
*
* STATUS: not compiled/catalogued or run in this session unless stated otherwise.

PROGRAM TEST.{PRODUCTION.PROGRAM}

  CALL TEST.START.PROGRAM('TEST.{PRODUCTION.PROGRAM}')
  *
  CALL TEST.START.CASE('{normal-result description}')
  * ... build input record(s) inline ...
  CALL {PRODUCTION.PROGRAM}(...)
  CALL TEST.ASSERT.EQUAL({expected}, {actual}, '{what this proves}')
  CALL TEST.END.CASE
  *
  CALL TEST.START.CASE('{boundary description}')
  * ...
  CALL TEST.END.CASE
  *
  CALL TEST.START.CASE('{blank/invalid input description}')
  * ...
  CALL TEST.END.CASE
  *
  * Add a structured-error case here if the routine can return one, and a
  * regression case for any named defect - see SKILL.md's case-coverage list.
  *
  CALL TEST.PROGRAM.SUMMARY(FAIL.COUNT, RUN.STATUS)
  STOP RUN.STATUS
  *
END
```

## File-touching / integration test

Same shape, plus isolation and cleanup. Never substitute a production file or production keys for the
placeholders below.

```text
* Program : TEST.{PRODUCTION.PROGRAM}
* File    : BP.TEST
* Purpose : Integration test for BP/{PRODUCTION.PROGRAM}. Uses an isolated,
*           non-production test file and unique test-only keys.
*
* NOT exercised here unless stated otherwise: real concurrent lock contention
* (needs a second session/process).
*
* STATUS: not compiled/catalogued or run in this session unless stated otherwise.

PROGRAM TEST.{PRODUCTION.PROGRAM}
  *
  CALL TEST.START.PROGRAM('TEST.{PRODUCTION.PROGRAM}')
  *
  TEST.FILE.NAME = '{ZZ.TEST.ISOLATED.FILE.NAME}'
  GOSUB CLEANUP.TEST.DATA
  *
  CALL TEST.START.CASE('{scenario description}')
  * ... arrange this case's own record(s) under a unique 'ZZTEST-...' key ...
  CALL {PRODUCTION.PROGRAM}(TEST.FILE.NAME, '{ZZTEST-key}', ...)
  CALL TEST.ASSERT.EQUAL({expected}, {actual}, '{what this proves}')
  CALL TEST.END.CASE
  *
  GOSUB CLEANUP.TEST.DATA
  *
  CALL TEST.PROGRAM.SUMMARY(FAIL.COUNT, RUN.STATUS)
  STOP RUN.STATUS
  *
*-----------------------------------------------------------------------------
* Runs before (defensive, in case a prior run crashed) and after this
* program's cases, whether they passed or failed.
CLEANUP.TEST.DATA:
  OPEN TEST.FILE.NAME TO F.CLEANUP THEN
    DELETE F.CLEANUP, '{ZZTEST-key}'
  END
  RETURN
  *
END
```

If the routine under test calls `SU.OPEN.FILE`/`B.IO.HANDLER` internally and you don't have their exact
call signature, don't write around that by reimplementing the I/O yourself with plain `OPEN`/`READU`/
`WRITE` and presenting it as equivalent - that's a different code path than production uses. Instead call
the real routine as written, and if the *test's own* scaffolding (arranging/cleaning up the isolated file)
needs I/O, plain BASIC statements are fine there since that's test infrastructure, not the routine under
test.

## TEST.MAP record

```text
Key: {PRODUCTION.PROGRAM}*TEST.{PRODUCTION.PROGRAM}
  Attr 1 PRODUCTION.PROGRAM = {PRODUCTION.PROGRAM}
  Attr 2 TEST.PROGRAM        = TEST.{PRODUCTION.PROGRAM}
  Attr 3 FAST.SUITE.FLAG     = Y  (pure calculation)  or  N  (touches a file)
  Attr 4 ADDED.DATE          = {today, or leave for the developer to fill in}
  Attr 5 NOTES               = {one line - what this test program covers}
```
