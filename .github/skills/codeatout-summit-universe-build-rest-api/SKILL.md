---
name: codeatout-summit-universe-build-rest-api
description: Build a REST API endpoint for a Summit Rocket UniVerse account that already uses the B.REST.SUBMIT dispatcher. Use when adding or exposing a Summit REST service, endpoint, API, or JSON web service, or wrapping a task group or existing routine as a REST call.
---

# Build a REST API in Summit

Summit has one generic REST dispatcher, `B.REST.SUBMIT`. A new endpoint is **data plus one subroutine**: a RESTAPI.SETUP record, two DATA.MAP records and a BP subroutine. Never edit `B.REST.SUBMIT` to add an endpoint.

Worked example: `B.REST.RATING.REQUEST` (actuary rating over task group 0090). `B.REST.UPDATE.PROFILE` is the simplest live template.

## 0. Ground rules

- Read live source with `read_bp` (`file_name` = `BP`, `BP.INCLUDE` or `INCL.ERROR`). Do not work from memory or from this file alone for anything you will copy.
- Check that every name you create does not already exist (`LIST BP "NAME"`, `LIST DATA.MAP WITH @ID LIKE "REST*NAME..."`, `LIST RESTAPI.SETUP "NAME"`). Never overwrite.
- `write_bp`, `compile_bp`, `catalog_bp`, `write_record` and `execute_tcl` need `confirm=true` and user approval. `write_bp` does not compile or catalog.
- If the user asks for an audit trail, keep an `audit.md` and log every write, compile, catalog and test run as you go.
- If connection calls start timing out, `disconnect` then `connect`. A long `LIST` on a big file (for example `LIST POLICY.DETAIL`) can hang the session; always use `SAMPLE` or a narrow selection.

## 1. Decide the contract first

Write down, before any code:

- **Request fields**: JSON property name, which are required, and the position each lands in `PASS.ARGS`.
- **Response fields**: JSON property name and the position in `RETURN.PAYLOAD`.
- **Errors**: the error codes (`ERROR.MCODE`) and messages.
- **Side effects**: does it write? If yes, say so, and decide how callers avoid persisting (see section 6).

## 2. How the dispatcher works

`B.REST.SUBMIT(PASS.ARGS,REQUEST.ID,TRACK.ID,RESPONSE.REC,RETURN.PAYLOAD)`:

1. `REQUEST.ID` is the subroutine name. It reads `RESTAPI.SETUP` keyed by `REQUEST.ID`. Field 3 is the IN map key and field 4 is the OUT map key. If blank they default to `REST*<name>*IN` and `REST*<name>*OUT`.
2. It reads the IN `DATA.MAP` and `B.REST.JSON.CONVERT("JSONTODB")` turns the JSON into `SUB.ARGS`, ordered by the DATA.MAP target position (field 36).
3. Required check: DATA.MAP field 39 = `Y`, and a blank `SUB.ARGS<n>` fails with "<name> is a required field".
4. It calls `CALL @SUB.NAME(SUB.ARGS,TRACK.ID,RESPONSE.REC,SUB.RET.REC)`.
5. `SUB.RET.REC` is converted back to JSON with the OUT DATA.MAP (`DBTOJSON`).
6. Errors: `RESPONSE.REC<1>` = 1 means failure, and the payload is `{}`.
7. **`TRACK.ID` starting `TESTMODE*` wraps the call in `BEGIN TRANSACTION ... ROLLBACK`**, so nothing the subroutine wrote is kept.

Request ids starting `TASK*<code>` call `B.TASK.ENGINE` directly with a `TASK*<code>*IN/OUT` map and no subroutine. Use that only when the task group already does what you need. It does not populate the task `SUB.REC` input record, so a risk-based task needs a wrapper subroutine.

## 3. The subroutine

Signature (fixed):

```
 SUBROUTINE B.REST.<NAME>(PASS.ARGS,TRACK.ID,RESPONSE.REC,RETURN.PAYLOAD)
```

Conventions (copy from `B.REST.UPDATE.PROFILE`):

- Header comment listing `PASS.ARGS<n>` and `RETURN.PAYLOAD<n>`.
- `PROG.NAME = "B.REST.<NAME>"` then `$INCLUDE BP.INCLUDE $B.REST.<NAME>`.
- Main block of `GOSUB X` followed by `IF$L.ERROR`.
- `EXITERR:` paragraph containing only `$INCLUDE BP.INCLUDE $EXITERR.RESTAPI` and `RETURN`.
- Set `L.ERROR = 0`, `E.REC = ""`, `MESS = ""`, `ERROR.MESS = ""`, `ERROR.STATUS = ""` and `RETURN.PAYLOAD = ""` at the start so values do not leak between calls.
- Before each I/O call set `ERROR.MCODE = "..."` and follow it with `IF$ERROR`. For a business error set `MESS` and `ERROR.MCODE`, then `GOSUB EXITERR ; RETURN`.
- I/O goes through `CALL B.IO.HANDLER("READ",file,key,"",REC,E.REC)`. Not-found is `IO$READ.ELSE`. Use `READ`, not `READU`, unless you really will write.
- `$EXITERR.RESTAPI` fills `RESPONSE.REC` = status, message, log key, `PROG.NAME*ERROR.MCODE`, reference.
- End the file with a change-log comment line: `* DD MMM YYYY PRJ*nnnnn-DESCRIPTION`. Use the real project number, never a placeholder.
- `RETURN.PAYLOAD<n>` positions must match the OUT DATA.MAP order. Keep values single-valued unless the map expects multivalues: any `@VM`, `@SM` or `@FM` in a value changes the JSON shape.

### The two includes

- `INCL.ERROR $B.REST.<NAME>`: copy `INCL.ERROR $B.REST.UPDATE.PROFILE`. It is a `DEFFUN F.ERROR.HANDLER` that packs `PASS.ARGS`, `TRACK.ID`, `RESPONSE.REC`, `RETURN.PAYLOAD` and `PARAM` into `ERROR.ARGS`.
- `BP.INCLUDE $B.REST.<NAME>`: `$INCLUDE BP.INCLUDE $SU.COMMON`, then the INCL.ERROR include, then only the equate includes you use (`$POLICY.DETAIL.EQUATES`, `$CUSTOMER.DETAIL.EQUATES`, ...).

Write the includes **before** compiling the program.

## 4. The data records

All written with `write_record`; read the examples first with `read_record` and copy their shape.

`RESTAPI.SETUP` key = subroutine name:
- f2 purpose text, f3 IN map key, f4 OUT map key, f5 default payload (optional, used when the subroutine returns nothing).

`DATA.MAP` `REST*<NAME>*IN`:
- f1 JSON property names (multivalued), f36 target `SUB.ARGS` position for each (1, 2, 3 ...), f39 `Y` for required fields. Property names are case-sensitive and are what callers send.

`DATA.MAP` `REST*<NAME>*OUT`:
- f1 JSON property names in `RETURN.PAYLOAD` order. Other fields are blank for flat responses.
- Child objects and arrays use f2 (child map name) and f55 (sub-map flag). Copy a live example such as `TASK*0059*OUT` before attempting nesting.
- Do not use the map key `IGNORE` for new endpoints. It bypasses the mapping and returns raw data.

## 5. Build order

1. Confirm names are free.
2. Write `INCL.ERROR` and `BP.INCLUDE` includes.
3. Write the subroutine to BP.
4. `compile_bp`, read the output, fix, then `catalog_bp`.
5. Write the three records (SETUP, IN, OUT). Read them back and check the field positions.
6. Verify (section 6), then write the unit test (section 7).
7. Update the audit log, if one is kept.

## 6. Verify on the account

Test the subroutine directly with `begin_transaction`, `call_subroutine` (`args` = the four arguments, `PASS.ARGS` fields separated by the field mark `þ`, `num_args` = 4), then `rollback_transaction`. Always use a `TESTMODE*...` TRACK.ID for anything that might write.

Check:
- the happy path returns the expected `RETURN.PAYLOAD` positions;
- each error code returns `RESPONSE.REC<1>` = 1 and the expected message and code;
- the underlying records are unchanged afterwards (read them back).

Then test the real dispatcher with `call_subroutine` on `B.REST.SUBMIT` (`PASS.ARGS` is the JSON text, `REQUEST.ID` is the subroutine name) if an HTTP caller is not available, and confirm the JSON response parses. Payloads that carry XML, quotes, backslashes or line breaks must be checked explicitly: the JSON character scrub does not escape `"`, `\` or CR/LF, so the escaping depends on the JSON conversion, and it should be proved, not assumed.

### Write side effects

If the subroutine (or anything it calls) writes, a caller that omits `TESTMODE*` will commit. Either state clearly in the spec that callers must send `TESTMODE*`, or make the subroutine refuse to run unless `FIELD(TRACK.ID,"*",1) = "TESTMODE"`. Decide this with the user rather than assuming.

## 7. Unit test

Use the [Summit UniVerse unit-test skill](../codeatout-summit-universe-write-unit-test/SKILL.md). Tests live in the `BP.TEST` file. For a REST subroutine:

- Call the subroutine inside `BEGIN TRANSACTION ... ROLLBACK ... END TRANSACTION`.
- Cover: each validation error, an unknown-key error, the happy path for each variant, no persistence of changes, and one regression per defect.
- Mark `FAST.SUITE.FLAG` = `N` in `TEST.MAP` when the test reads live data, and note the fixture key it depends on.
- Run it with `execute_tcl` `TEST.ONE TEST.<NAME>` (needs `confirm=true`).

## 8. Report back

State: the files and records created, what was verified and how, the unverified parts (for example HTTP escaping or untested branches), the fixture data the tests need, and any write side effects and how callers must avoid them.
