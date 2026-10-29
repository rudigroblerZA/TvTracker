# Company standards

## Naming

- Use meaningful, self-describing variables and internal routine names.
- Prefix functions with `F.`.
- Name screen validation/process programs `B.<FILE>.DEF.VAL`.
- Name object programs `B.<NAME>` and use the standard five arguments where applicable.
- Delimit file names with periods, for example `POLICY.DETAIL`; do not pluralize new file names.
- Delimit dictionary names with underscores, for example `POLICY_END_DATE`; do not pluralize new dictionary names.
- Name define/equate macros with `$`. Prefix conditional macros with `IF$` and Boolean macros with `IS$`.
- Make a macro expansion complete; do not require the caller to finish its logic.
- Name parameter equates `PA$<argument>.<position>$<DESCRIPTION>`.
- Write user-facing messages in proper case.

## Structure and maintainability

- Understand the business purpose before changing or reviewing code.
- Keep main logic as orchestration over focused internal routines.
- Use guard clauses and early exits instead of unnecessary `ELSE` blocks.
- Use `CONTINUE` and `EXIT` to flatten loop logic.
- Avoid deeply nested `IF` structures.
- Explain or remove commented-out code.
- Reuse approved libraries instead of copying logic.
- Treat repeated logic as a reuse candidate, not an automatic reason to abstract it.
- Preserve the local style unless it conflicts with an explicit current standard.

## Functions

- Give each function one simple purpose and design it for reuse.
- Do not update application data from a function; logging is allowed when it does not change business behaviour.
- Do not return an array.
- Pass required records explicitly rather than depending on application common variables.
- Define function arguments in camel case when required by `WW.FUNCTIONS` and dimensioned-array compatibility.
- Boolean functions use `1` for true and `0` for false, as confirmed by the approved `F.CHECK.STRATOS.POLICY` pattern.
- `$IO.COMMON` may be used when required to interpret `B.IO.HANDLER` results; do not introduce unrelated common-state dependencies.
- Prefer a subroutine when the caller requires a structured error response.

## Error handling

- Set a meaningful, unique `ERROR.MCODE` immediately after a fallible call and before invoking its check macro.
- Do not use spaces or throwaway words in an error marker.
- Use the applicable global macro rather than recreating standard error logic.
- Central error logging is performed through `F.ERROR.HANDLER`, commonly through `LOG$ERROR` or an `EXIT$ERR...` macro.
- Preserve the original error marker and message while performing cleanup.
- Do not silently clear an error unless the condition is explicitly a normal business outcome and another return signal is set.

## I/O and attributes

- Open files through `SU.OPEN.FILE` and check `IF$OPENERROR`.
- Use `B.IO.HANDLER` for supported `READ`, `READU`, `READV`, `READVU`, `WRITE`, `WRITEU`, `WRITEV`, `WRITEVU` and `DELETE` operations.
- Use numbered dictionary equates for record attributes.
- Do not guess or silently repair conflicting equates.

## Quality

- Compile, catalogue and test relevant paths.
- State how the change was tested and which paths remain untested.
- Never fabricate successful compilation, cataloguing or testing.
