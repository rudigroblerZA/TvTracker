---
name: codeatout-screendriver
description: Guides changes to applications that use the ScreenDriver meta-driven form framework. Use only after confirming the consuming repository uses ScreenDriver.
---

# ScreenDriver

## Confirm applicability

Before changing code, verify that the repository references ScreenDriver
packages or its flow-definition APIs. Inspect the application's installed
version, bootstrap path, module registration, generated definition, and
existing flow/test patterns. Do not assume package names, namespaces, or
application structure.

## Change workflow

1. Identify the flow, step, control path, event, task group, and server/client
   extension points involved in the requested behaviour.
2. Trace the emitted definition and form-state lifecycle before modifying
   visibility, enablement, validation, submission, or navigation.
3. Make server and client extension contracts explicit and keep stable action
   or control identifiers synchronized across both sides.
4. Test the produced definition and the relevant user interaction, including
   conditional state, validation, and submission.

## Guardrails

- Do not treat computed server-only values as client-tracked form state.
- Use registered controls for values referenced by client expressions and
  validators.
- Preserve data classification and authorization at server task boundaries.
- Prefer the framework's public extension points over internal implementation
  details.

See [the framework reference](references\framework-reference.md) for detailed
concepts and troubleshooting guidance.
