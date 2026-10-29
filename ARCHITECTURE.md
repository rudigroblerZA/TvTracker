# Architecture

TvTracker is a .NET 10 distributed application composed locally by Aspire 13. This reference maps runtime boundaries, data ownership, and cross-service flows for contributors. See [README.md](README.md) to run the application, [FEATURES.md](FEATURES.md) for user-facing capabilities, and [CONTEXT.md](CONTEXT.md) for design rationale and domain constraints.

## Runtime boundaries

`src/TvTracker.AppHost/AppHost.cs` composes the local runtime. It starts the application projects and their dependencies: PostgreSQL with pgvector, Redis, RabbitMQ, Keycloak, flagd, and Docker Model Runner when the selected embedding model needs it. Aspire service discovery resolves project endpoints. This composition describes local development; it is not a production deployment specification.

| Entry point | Request path |
| --- | --- |
| `WebApp` | Blazor Server renders the shared `TvTracker.UI`. It calls service APIs directly through Aspire service discovery. Keycloak handles sign-in; the web host stores tokens in Redis rather than the browser. |
| `MobileApp` | MAUI Blazor Hybrid uses the same `TvTracker.UI` pages, sends API requests through `Mobile.Bff`, and keeps an SQLite offline cache and ordered change queue. |
| `Mobile.Bff` | YARP routes `/catalog`, `/tracking`, `/ratings`, `/recommendations`, `/notifications`, `/analytics`, `/social`, `/research`, `/assistant`, and `/identity` to their APIs. `/features` routes to flagd's OFREP endpoint. |

`TvTracker.UI` owns the pages and typed API clients. `WebApp` and `MobileApp` provide host-specific token and platform services.

## Service ownership

Each service owns its database. Services do not query another service's tables; they exchange data through HTTP APIs or integration events.

| Service or project | Data owner | Responsibility and notable dependencies |
| --- | --- | --- |
| `Identity.API` | `identitydb` | Profiles and account lifecycle. Keycloak provides authentication. Identity calls other APIs for account deletion and export. |
| `Catalog.API` and `Catalog.SyncWorker` | `catalogdb` | Catalog.API reads the catalog and applies its migrations. The sync worker is the only show writer; it calls TVmaze, imports search matches, applies overrides, and publishes catalog events. |
| `Tracking.API` | `trackingdb` | Follows, watches, lists, and schedule-oriented tracking operations. Publishes activity snapshots. |
| `Ratings.API` | `ratingsdb` | Ratings and user posts, including moderation, flags, and corrections. Accepted corrections are sent to the catalog sync worker. |
| `Recommendations.API` | `recommendationsdb` | Recommendation signals, picks, and dismissals. |
| `Notifications.API` | `notificationsdb` | Inbox, devices, and notification preferences. Consumes integration events. |
| `Analytics.Worker` and `Analytics.API` | `analyticsdb` | The worker maintains an analytics copy and computes insight documents and lists. The API reads those results. |
| `Social.API` | `socialdb` | People follows, privacy, activity feed, and shared-list copies. It builds its state from service events and snapshots. |
| `Research.API` | `researchdb` | Moderator research, saved research versions, and episode notes. Catalog fields are applied as sync-worker overrides. |
| `Assistant.API` | In-memory question queue | Runs "Ask the app" through the MCP gateway and Model Runner; it does not own application data. |
| `Mcp.Server` and `Mcp.Gateway` | None | The server exposes read-only tools over Catalog.API, Analytics.API, and Research.API. The gateway provides the shared MCP endpoint and access controls. |

`Catalog.Data` and `Analytics.Data` hold persistence models shared by their APIs and workers. `TvTracker.IntegrationEvents`, `TvTracker.EventBus`, `TvTracker.EventBus.RabbitMQ`, `TvTracker.Persistence`, and `TvTracker.ServiceDefaults` provide shared contracts and infrastructure. `Catalog.ImportCli` and `TvTracker.LoadGen` are operational tools, not runtime services.

## Data and integration flows

### Requests

- The web host calls service APIs directly. `TvTracker.UI` clients use service-discovery names, such as `catalog-api`.
- The mobile app calls the BFF. YARP strips each public service prefix and forwards the remaining path to the matching API.
- Services use HTTP through Aspire service discovery when a synchronous answer is required. Examples include catalog search asking the sync worker to import provider matches and Identity coordinating account operations.
- The MCP server calls service APIs rather than databases, preserving the database ownership boundary.

### Integration events

Event contracts are records in `src/TvTracker.IntegrationEvents`. The RabbitMQ event bus uses a durable direct exchange and one queue per consuming service. Routing keys identify event types.

For database-backed publishers, `IIntegrationEventOutbox.Add` stores an event in the same `SaveChanges` as the domain change. The publisher sends newly added events after saving and retries pending rows in the background. Consumers use the inbox helpers to check and record processed event IDs in the same save as their changes. These boundaries prevent a failed publish or redelivery from silently separating an event from its state change.

RabbitMQ requeues a failed delivery once. A second handler failure is logged and dropped; there is no dead-letter queue. Periodic snapshots and catalog replays repair selected copies, but they do not replace a general dead-letter strategy.

Integration event contracts evolve by appending optional trailing parameters with defaults. Do not rename or reorder existing parameters: queued and persisted events may still use the earlier contract shape.

### Catalog and analytics

Catalog.API is the read side of `catalogdb`; Catalog.SyncWorker owns provider calls and catalog writes. The AppHost starts the sync worker after Catalog.API has initialized the schema and the event-consuming services are ready. The shared TVmaze rate limiter covers foreground imports and background refresh work.

Analytics.Worker consumes catalog and user-activity events into `analyticsdb`. It computes insights hourly in batches, saving each batch's documents and resulting events together. Analytics.API serves the stored results. Tracking and Ratings also publish periodic snapshots so downstream copies can reconcile missed activity.

## Cross-cutting runtime services

| Concern | Implementation |
| --- | --- |
| Authentication | Keycloak issues tokens. APIs validate tokens through shared authentication setup. `WebApp` uses OIDC authorization code flow with PKCE and keeps tokens server-side in Redis. |
| Feature flags | OpenFeature evaluates boolean flags from flagd. Flag keys are shared with the UI; the mobile app evaluates through the BFF's `/features` route. |
| Persistence | Services use separate PostgreSQL databases. Catalog uses pgvector for embeddings; Redis supports token storage and selected caches. EF Core migrations are applied by the owning API for migrated services. |
| Health and telemetry | `TvTracker.ServiceDefaults` configures health checks, service discovery, HTTP resilience, and OpenTelemetry. OTLP export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured. The shared health endpoints are exposed only in Development. |
| Local orchestration | The AppHost defines container lifetimes, service references, and startup dependencies. Change it for local composition; do not infer production topology from it. |

## Change locations

| Change | Primary locations |
| --- | --- |
| Add or change an HTTP endpoint | The owning service's `Apis/` class and its `Program.cs` mapping. |
| Change a service's persisted state | Its EF Core context and migration project; the owning API applies migrations at startup. |
| Publish or consume an integration event | `src/TvTracker.IntegrationEvents`, the publisher's transaction and outbox, and each consumer's handler registration and inbox handling. |
| Change a shared UI page or API client | `src/Clients/TvTracker.UI`; host-specific behavior belongs in `WebApp` or `MobileApp`. |
| Change local service composition | `src/TvTracker.AppHost/AppHost.cs` and the relevant AppHost configuration. |

## Known operational boundary

The AppHost documents the development topology, not production hosting, network exposure, or scaling. RabbitMQ has no dead-letter queue, and event-handler failures are dropped after one requeue. The solution has no test projects, and the repository has no CI pipeline; verify changes using the build commands in [README.md](README.md).
