---
name: codeatout-microsoft-graph-sdk
description: Integrates Microsoft Graph through a supported SDK with least-privilege Entra access, resilient collection handling, and safe lifecycle management. Use when an application reads or changes Microsoft 365 resources through Microsoft Graph.
---

# Microsoft Graph SDK

Use this skill for Microsoft Graph integrations in .NET, TypeScript or
JavaScript, Python, and other supported SDK languages. Apply
`codeatout-acl-entra-authentication` for the Entra authentication and
authorization design. Follow the consuming repository's package-management
guidance when adding or changing SDK dependencies.

## Discover before implementation

Identify the target language, installed Graph SDK and identity-library versions,
hosting model, existing token acquisition, dependency-injection or client
lifecycle, retry policy, logging rules, and test conventions. Consult current
Microsoft Graph API, permission, and SDK documentation for the selected API
operation and installed SDK version; generated SDK shapes differ by language
and version.

Confirm the resource, operation, data classification, caller identity, whether
the operation requires user context, and the least-privileged delegated or
application permissions. Use representative non-production data when exploring
calls. Do not expose mail, files, calendar content, directory information,
access tokens, delta links, subscription secrets, or raw Graph responses in
logs or test fixtures.

## Authentication and authorization

- Use delegated access when an operation must respect the signed-in user's
  access; use app-only permissions only for approved unattended workloads.
- For a service acting for a user, validate the incoming token and use the
  supported on-behalf-of flow. Do not accept a caller-supplied user identifier
  as a substitute for user authorization.
- Prefer managed identity in supported Azure-hosted environments. Store any
  unavoidable credential in the approved secret store and prefer certificates
  or federation over client secrets where supported.
- Register exact, minimum Graph permissions and obtain consent through the
  organisation's approval process. Treat a permission increase as a security
  change, and separately authorize the application's intended resource access.

## Client and request design

- Reuse a configured SDK client and its supported credential lifecycle where
  the selected flow permits it. Keep user-scoped clients or credentials scoped
  to the user request; do not share them across users.
- Use the SDK's generated request builders or supported client API rather than
  manually constructing authentication headers. Preserve repository middleware,
  proxy, timeout, tracing, and retry conventions.
- Select only fields required by the use case, apply supported server-side
  filtering and ordering, and impose a bounded page size or result limit.
  Validate and encode user-derived identifiers and query values rather than
  concatenating them into Graph paths or OData expressions.
- Handle every collection as paged, including continuation links. Process
  incrementally where practical instead of retaining an unbounded result set.

## Resilience and long-running synchronization

- Respect Graph error semantics and `Retry-After` for throttling. Use the
  SDK's supported retry behaviour or a bounded, cancellation-aware retry
  policy; do not fan out unbounded parallel calls or retry authorization and
  validation failures.
- Use batching only for independent operations, correlate each response to its
  request, and handle partial failures individually.
- For incremental synchronization, use a supported delta query when it meets
  the resource's requirements. Store its opaque continuation or delta link
  durably and securely, process removals, and recover safely when Graph
  invalidates the synchronization state.
- For change notifications, expose a verified HTTPS endpoint, validate the
  subscription handshake and notification authenticity according to current
  Graph documentation, renew subscriptions before expiry, and reconcile missed
  notifications with a durable synchronization strategy. Treat notification
  payloads as untrusted input.

## Validate

Test the intended and denied identities, granted and missing permissions,
pagination, empty and partial results, cancellation, throttling, transient
failure, and Graph error mapping. For sync or webhook flows, test expiration,
duplicate or out-of-order delivery, state recovery, and deletion handling.
Confirm SDK packages are supported and compatible with the target runtime, and
review final logs and telemetry for sensitive Microsoft 365 data.
