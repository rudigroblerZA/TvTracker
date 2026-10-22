---
description: 'Organisation-wide guidance for accessible, secure Angular and TypeScript applications'
applyTo: '**/*.ts, **/*.html, **/*.scss, **/*.css'
---

# Angular and TypeScript Development

Inspect the Angular version, TypeScript configuration, existing component
patterns, styling system, test tooling, and accessibility conventions before
making changes.

- Preserve strict type safety and avoid `any` unless a documented boundary
  requires it.
- Use the Angular APIs supported by the installed version. Prefer the
  repository's established component, dependency-injection, state-management,
  and routing patterns.
- Keep templates semantic and accessible: use native controls where practical,
  provide labels and keyboard support, and expose meaningful validation errors.
- Validate untrusted data at the server boundary and avoid bypassing Angular's
  sanitization or directly injecting untrusted HTML.
- Handle loading, empty, and error states for asynchronous UI behaviour.
- Avoid unnecessary change-detection work and use stable list tracking where
  the installed Angular version and project patterns support it.
- Use the repository's existing unit and end-to-end test tools. Test changed
  component behaviour and critical user flows.

Do not require a UI library, state-management library, rendering mode, or test
runner without an explicit project decision.
