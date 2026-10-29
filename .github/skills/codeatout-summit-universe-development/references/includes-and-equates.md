# Includes and equates

## Include hierarchy

A source program normally includes one program-specific item:

```text
$INCLUDE BP.INCLUDE $B.PRODUCT.SPLIT.PROFILE
```

That item may include `$SU.COMMON`, `$STD.ARGS`, file dictionary equates, and program-specific initialization and assignments. `$SU.COMMON` may transitively include platform common blocks, `$GLOBAL.DEFINES`, `$IO.COMMON`, `WW.FUNCTIONS` and digital common definitions.

## Resolution rules

1. Start at the program-specific include.
2. Resolve relevant nested includes recursively.
3. Record injected common variables, functions, macros, initialization statements and file equates.
4. Do not add a shared include directly when it already arrives transitively.
5. Respect include order. Includes can contain executable statements, not only declarations.
6. If an include is unavailable, say which behaviour cannot be verified.

## Executable macros

Global `EQUATE ... LIT` definitions may expand to `GOSUB EXITERR`, `RETURN`, `CONTINUE`, `EXIT`, `ROLLBACK` or a function call. Examples include `IF$L.ERROR`, `IF$L.ERROR.ROLLBACK`, `IF$ERROR`, `IF$LOCK`, `IF$LOCKERROR`, `IF$IOREAD.ELSE.RTN`, `IF$RTN.FLAG`, `GO$EXITERR` and `LOG$ERROR`.

Do not infer a macro's behaviour from its name when its definition can be inspected.

## Dictionary equates

Preferred format:

```text
<FILE_PREFIX>$<ATTRIBUTE_NUMBER>$<FIELD_NAME>
```

Example: `CD$206$FCL_RISK_KEY`.

- Prefer the numbered form over `CD$FCL_RISK_KEY` and over raw `REC<206>`.
- Preserve the exact existing approved name.
- Apply value/subvalue positions after the equate, such as `REC<CD$13$DEPENDANT_NAMES,POS>`.
- Attribute gaps are valid and multiple aliases may map to one attribute.
- Conflicting numbered equates require verification against existing approved use and the authoritative dictionary; never choose by appearance.
- Treat unnumbered duplicates as legacy compatibility aliases, not templates for new code.

When indexing equates, map file, prefix, attribute, aliases and description, and retain conflicts for review.
