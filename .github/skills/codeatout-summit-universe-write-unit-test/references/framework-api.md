# Framework API reference

The condensed contract for the already-installed Summit UniVerse Unit Test Framework. This skill only
*calls* these - it never redefines or reimplements them. If any of these items don't actually exist in
the target account, stop and say so instead of writing a test against an assumed API.

## Lifecycle

```text
CALL TEST.START.PROGRAM(programName)   * FIRST statement in every TEST.{PROGRAM}. Resets all framework
                                        * result state for this run - the reason two runs never see each
                                        * other's leftover failures.
CALL TEST.START.CASE(caseName)         * one per scenario, before its assertions
CALL TEST.END.CASE                     * closes that scenario
CALL TEST.PROGRAM.SUMMARY(failCountOut, runStatusOut)
                                        * LAST call before STOP - prints every result and returns the
                                        * failure count / 0-or-1 status
```

## Assertions

Every assertion takes a final `description` argument - write it as the sentence that should be true, since
it's the first thing shown on failure.

```text
CALL TEST.ASSERT.EQUAL(expected, actual, description)
CALL TEST.ASSERT.NOT.EQUAL(unexpected, actual, description)
CALL TEST.ASSERT.TRUE(conditionValue, description)         * conditionValue must be 1 or 0
CALL TEST.ASSERT.FALSE(conditionValue, description)
CALL TEST.ASSERT.EMPTY(value, description)
CALL TEST.ASSERT.NOT.EMPTY(value, description)
CALL TEST.ASSERT.CONTAINS(searchText, actualValue, description)
CALL TEST.ASSERT.RECORD.EQUAL(expectedRecord, actualRecord, description)  * dynamic-array records;
                                                                            * reports the first
                                                                            * differing attribute
```

None of these stop the test program on failure - they record a PASS/FAIL row and return, which is what
lets one test program report every failing case in a single run instead of stopping at the first one.

## Runner (for reference - a test-writing task doesn't usually call these directly)

```text
CALL TEST.RUN.ONE(testProgramName, failCountOut, statusOut)
CALL TEST.RUN.MAPPED(productionProgramName, failCountOut, statusOut)
CALL TEST.RUN.ALL(failCountOut, statusOut)
```

## TEST.MAP

File `TEST.MAP`, key `{PRODUCTION.PROGRAM}*{TEST.PROGRAM}` (the compound key is what makes an exact
duplicate mapping structurally impossible to insert).

```text
Attribute 1  PRODUCTION.PROGRAM   e.g. B.CALC.RISK.LOADING
Attribute 2  TEST.PROGRAM         e.g. TEST.B.CALC.RISK.LOADING
Attribute 3  FAST.SUITE.FLAG      Y = runs in TEST.RUN.ALL's fast suite, N = integration-only
Attribute 4  ADDED.DATE           informational
Attribute 5  NOTES                free text
```

`FAST.SUITE.FLAG = Y` only for tests with no file access. A test that opens any file - even an isolated
test-only file - is `N`; it runs via `TEST.RUN.MAPPED`/`TEST.RUN.ONE`, not the fast suite.

## Naming

Test program name is always `TEST.{PRODUCTION.PROGRAM}` (e.g. `B.CALC.RISK.LOADING` →
`TEST.B.CALC.RISK.LOADING`). Framework and test items both live in `BP.TESTS`, never in `BP`. Production
source is never modified by this skill - it only reads it.
