# Program patterns

## Five-argument object

Canonical signature:

```text
SUBROUTINE B.NAME(PASS.CASE,PASS.ARGS,PASS.REC,RET.REC,ERR.REC)
```

Use the program-specific include. Unpack `PASS.ARGS` into meaningful variables during initialization and copy `PASS.REC` into a domain-specific working record. Keep main logic as short orchestration, checking the approved return macro after each `GOSUB`.

Separate work into initialize and validate, retrieve related records, process records, commit updates, and set `RET.REC` where applicable. Populate `ERR.REC` through the standard `EXITERR` and logging conventions. Do not reuse the caller's argument variable names when invoking a nested object; use `P.CASE`, `P.ARGS`, `P.REC`, `R.REC`, `E.REC` or the surrounding approved equivalents.

## Function

Canonical declaration: `FUNCTION F.NAME(theArgument)`.

Include the standard function/common definitions, set `PROG.NAME`, initialize the result, reject blank input with a guard clause, perform no business-data writes and return one simple result.

Always exit with `RETURN (RESULT)`, never a bare `RETURN`: in the main routine, in every internal GOSUB routine (`VARIABLES.INIT`, `VARIABLES.VALIDATE`, processing routines, `EXITERR`) and in guard clauses (for example `IF theKey = "" THEN RETURN (RESULT)`). Macros such as `IF$L.ERROR.RESULT`, `IF$ERROR.RESULT` and `EXIT$ERR` follow the same convention. For Boolean functions, return `1` for true and `0` for false.

## REST API wrapper

Canonical name: `B.API.<NAME>`, wrapping the five-argument object `B.<NAME>` of the same `<NAME>`. This is a thin adapter, not a place for new business logic - if a requested change needs the wrapper itself to branch on business rules or call more than one object, say so explicitly and confirm the shape with the user rather than forcing it into this pattern.

Canonical signature:

```text
SUBROUTINE B.API.<NAME>(PASS.ARGS,TRACK.ID,RESPONSE.REC,RETURN.PAYLOAD)
```

- `PASS.ARGS` - inbound request arguments, passed through to the wrapped object as `P.ARGS`. Confirm each attribute's meaning against the program-specific include or a working example before assuming a position; do not guess a layout.
- `TRACK.ID` - inbound correlation/tracking identifier. Thread it through only where its use is confirmed; do not invent a use for it.
- `RESPONSE.REC` - OUT parameter. Leave it untouched in the wrapper's own logic; it is populated only on the error path, by `$EXITERR.RESTAPI`.
- `RETURN.PAYLOAD` - OUT parameter. Set it only on the success path, from the wrapped object's `R.REC`. Reshape it only if the REST contract has been confirmed to diverge from the object's native return record.

Use the program-specific include, then orchestrate through focused internal routines:

1. `VARIABLES.ASSIGN` - initialize `L.ERROR`, `ERROR.STATUS`, `ERROR.MESS`, `MESS`, `E.REC` and any other locals the wrapped call needs.
2. Any request-shaping step the endpoint genuinely requires - for example resolving a caller-facing identifier into an internal key via an approved `F.` function before it reaches the wrapped object. Add this only when the wrapped object's expected argument shape actually differs from `PASS.ARGS`; do not copy a resolution step from another wrapper on the assumption every endpoint needs one.
3. The call to the wrapped object - fresh `P.CASE, P.ARGS, P.REC, R.REC, E.REC` locals (never the wrapper's own argument variables, per the five-argument object convention above), a unique `ERROR.MCODE`, checked with `IF$ERROR`. The REST signature carries no `CASETYPE`-equivalent input, so `P.CASE` is conventionally left blank (`""`) unless a specific value is confirmed for the wrapped object.
4. `RETURN.PAYLOAD.SET` - set `RETURN.PAYLOAD` from the wrapped object's `R.REC`.

Orchestrate these from a `MAIN.PROCESS` label, checking `IF$L.ERROR` after every `GOSUB`, same as any other program:

```text
MAIN.PROCESS:
GOSUB VARIABLES.ASSIGN
GOSUB <request-shaping step, if needed>
IF$L.ERROR
GOSUB CALL.B.<NAME>
IF$L.ERROR
GOSUB RETURN.PAYLOAD.SET
IF$L.ERROR
RETURN
```

Route the error label through the REST-specific include instead of the general `EXITERR` conventions in company-standards.md:

```text
EXITERR:
$INCLUDE BP.INCLUDE $EXITERR.RESTAPI
RETURN
```

As confirmed against the live include, `$EXITERR.RESTAPI` normalizes the error flag, builds `ERROR.STATUS`/`ERROR.MESS` from `E.REC`/`ERR.REC` (defaulting to `"1"` / `"No Error Message"`), prefixes `ERROR.MCODE` with `PROG.NAME` for uniqueness, calls the standard `F.ERROR.HANDLER` (which can override the message, status and error code and returns a log key and reference number), and builds `RESPONSE.REC` as a five-attribute envelope: `<1>` status, `<2>` message, `<3>` log key, `<4>` error code, `<5>` reference number - then sets `L.ERROR = 1`. Includes can change; inspect the live copy rather than relying on this summary when it matters, per the resolution rules in includes-and-equates.md.

## Screen validation program

Canonical name: `B.<FILE>.DEF.VAL`.

Screen programs normally use framework common variables rather than explicit arguments. Common semantics include `PARAM` for `CASETYPE`; `RECORD` and `ORIG.REC`; `WORK`; `KEY`, `CNT` and `LINE`; `REFRESH`; and `RTN.FLAG`.

Route events with `BEGIN CASE`; place each case in a focused internal routine and check `IF$RTN.FLAG`. Treat an unknown case as an error. Validation should normalize the field, restore the prior value when rejected, set `MESS` and `ERROR.MCODE`, and use the standard error path.

Typical cases include field validation, display, selection, update, new-record setup, multivalue removal, mandatory validation, auditing and display/storage conversion. Do not copy raw attribute numbers or excessive nesting from legacy screen code.
