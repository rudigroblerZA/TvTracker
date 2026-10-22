---
type: knowledge
title: TV Tracker codebase map and current state
description: Evidence-based overview of the solution structure, runtime composition, and contributor checks.
resource: TvTracker
tags:
  - codebase-map
  - onboarding
timestamp: 2026-10-20T00:00:00Z
---

# Summary

TV Tracker is a .NET 10 solution containing 19 projects: the Aspire AppHost, shared libraries, API services, a mobile BFF, two client applications, and a shared Razor UI library. The root `README.md` is the repository's contributor overview. No existing `.context` records or test projects were found.

The README describes intended product behavior and the local run flow. The solution file, project references, AppHost registrations, and runtime entry points confirm the broad boundaries in `TvTracker.slnx`, `src/TvTracker.AppHost/AppHost.cs`, and the project files under `src/`.

# Decision log

## 2026-10-20

- **Decision:** Use the projects listed in `TvTracker.slnx` as the unit for describing independently buildable components. **Why:** The solution explicitly includes 19 project files with distinct references and entry points. **Impact:** The README describes clients, services, the gateway, and shared libraries separately instead of treating `src/` as one application.
- **Decision:** Treat Aspire AppHost as the local runtime composition, not as evidence of production deployment topology. **Why:** `src/TvTracker.AppHost/AppHost.cs` registers PostgreSQL, Redis, RabbitMQ, Keycloak, service projects, and WebApp for local orchestration; no application deployment manifests were found. **Impact:** Local prerequisites and runtime relationships are documented, while production hosting remains an open question.
- **Decision:** Keep the existing root README as the primary onboarding document and correct it instead of adding a second architecture guide. **Why:** The README already documents prerequisites, local execution, project responsibilities, event flow, and production gaps. **Impact:** New contributors have one primary entry point; this record preserves the evidence and uncertainties behind its map.
- **Decision:** Record the current build and test state as an observed snapshot. **Why:** `dotnet build TvTracker.slnx --no-restore` on 2026-10-20 built all non-MAUI projects but failed for each MAUI target because `IBrowser` is ambiguous in `src/Clients/MobileApp/Services/MobileAuth.cs`. No test projects appear in the solution. **Impact:** The README no longer describes MAUI as uncompiled, and the build failure and absence of test projects are visible to contributors.
- **Decision:** Describe integration events as RabbitMQ delivery backed by persistence outbox and inbox helpers, while retaining the documented retry limitation. **Why:** `src/TvTracker.Persistence` stores pending events and processed event IDs; `src/TvTracker.EventBus.RabbitMQ/RabbitMQEventBus.cs` implements per-service subscriptions and drops messages after a second handler failure. **Impact:** Outbox retries do not imply indefinite broker redelivery; a dead-letter strategy remains a production follow-up.

# Alternatives considered

- A separate architecture document would duplicate the existing README's project table and event-flow overview. Keep the map in the README until a focused architecture document has a distinct audience or purpose.
- Treating the build failure as an unverified MAUI workload limitation conflicts with the build output: MAUI targets reached compilation and reported a source-level name ambiguity.

# Follow-up

- Resolve the `IBrowser` ambiguity in `src/Clients/MobileApp/Services/MobileAuth.cs` and rerun the solution build.
- Review the SQLitePCLRaw high-severity and MessagePack moderate-severity package audit warnings reported by the build; update dependencies only after checking compatibility.
- Add focused unit and integration test projects; none are currently included in the solution.
- Define production deployment topology and a dead-letter policy for events that fail processing repeatedly.
- The README refers to a “TV Tracker – Service Design” document that was not present in the repository documentation inventory. Confirm where that document lives or revise the reference.
- Recheck the documented build status and package versions after dependency or SDK updates.
