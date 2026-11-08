# Backlog

What to build or fix next, from a code review of the whole repository on 28 October 2026 (about 40,000 lines across
13 services, the shared UI and the tools), plus the product ideas from FEATURES.md. Each item says why (with the code
that shows it), what done looks like, its priority and its size.

- **Priority:** P1 do next, P2 soon, P3 when convenient.
- **Size:** S a day or less, M a few days, L a week or more.
- **Keeping it current:** remove an item when it ships, and record the decision behind it in CONTEXT.md.

## Now

Retries with a dead-letter queue (R2), secrets out of the AppHost (S1) and cached flag answers (P2) shipped in
November 2026, then inbox retention (R4), research in the data export (S5), pinned page fetches (S6), a season-verdict
preference (D2) and per-follower flags (T3), then CI with tests (Q1), the development issuer in Development only (S3) and
rate limits on every write (S4), then service-to-service auth for the sync worker (S2), then alerts (O2), then Stripe (F1); CONTEXT.md has the decisions.

Production runs on Azure Container Apps and takes payments through Stripe (CONTEXT.md, Production decisions), so the
order to launch is:

| # | Item | Priority | Size |
| --- | --- | --- | --- |
| O1 | [A deployment](#o1-a-deployment) | P1 | L |
| Q3 | [Compile the MAUI app in CI](#q3-compile-the-maui-app-in-ci) | P2 | M |

## What the review found sound

So the rest of the list reads in proportion:

- **Endpoints:** every user-facing API group requires sign-in where it should, and gated features carry
  `RequireFeature`. The ops dashboard and its stats are Development-only. Moderator and admin endpoints answer 404 to
  others.
- **Events:** handlers that skip the inbox are idempotent upserts or go through shared once-only wrappers (Social,
  Analytics). The exchange and queues are durable and messages persistent. Since PR #71 the insights pipeline saves
  events with their documents.
- **Data:** the per-user tables have indexes for their queries (follows, watches, inbox, lists); connection pools are
  capped (`ConnectionPool`).
- **UI:** every image has alt text and every clickable element is a button or link; errors go through `ErrorNotice`;
  the web app refreshes tokens.

## Quality

### Q2. An evaluation set for Ask

**P3 · S**

- **Why:** Ask's answers depend on the chat model, and qwen3 0.6B misreads results (CONTEXT.md, "Ask the app"). Larger
  models couldn't be tried in the sandbox, so nobody knows which one is worth the hardware.
- **What:** 20-30 questions with the facts a right answer must contain ("Ted Lasso season 4: as good as the others"),
  and a tool in `src/Tools` that asks each through Assistant.API and scores the answers.
- **Done when:** one command scores any model set in `ModelRunner:ChatModel`, and README says which model scored how.

### Q3. Compile the MAUI app in CI

**P2 · M**

- **Why:** the MAUI app has been compiled once, by hand (CONTEXT.md, Known gaps), so changes to TvTracker.UI's shared
  pages can break it unnoticed. That first build found three errors (an `IBrowser` clash, a missing `using`, a package
  downgrade).
- **What:** a CI job on a Linux runner that builds `src/Clients/MobileApp` for Android (the project targets Android alone
  on Linux): `dotnet workload install maui-android`, then `-t:InstallAndroidDependencies` with
  `AcceptAndroidSDKLicenses=True` (it needs `USER` set), then the build with `AndroidSdkDirectory`.
- **Done when:** it builds in CI, and fixes for whatever was broken are merged.

## Reliability

Nothing open.

## Security

Nothing open.

## Performance

### P3. Fewer calls per show page

**P3 · M**

- **Why:** about 25 calls per show page, each with its own token handling and a flag check, from Blazor Server to many
  services.
- **What:** measure first (the show page's own calls now run in parallel, and flag answers are cached now). If not, batch what goes together, such as Tracking's follow,
  progress and lists, or Ratings' summary and the user's rating, into one call per service.
- **Done when:** decided by measurement and recorded in CONTEXT.md.

## Catalog data

### D1. Planned episodes per season

**P2 · M**

- **Why:**
  - TVmaze gives each season a planned episode count (`episodeOrder`) and an end date, but the catalog drops them.
  - So the season verdict has to guess when a weekly season is over, waiting 14 days of quiet, and can call a reality
    show over during a mid-season break (CONTEXT.md, Season verdict).
  - Up Next and the calendar can't say "episode 6 of 10", and the essential cut and catch-up planner can't tell a
    finale from the last listed episode.
- **What:**
  - Add `EpisodeOrder` and `EndDate` to the catalog's `Season`, with a migration.
  - Send them in `EpisodesScheduledIntegrationEvent` as optional trailing fields.
  - Keep a season copy in Analytics, with a migration.
  - Use them in `SeasonVerdictInsight.ReadyAt`, so a season with its planned count aired is over three days after
    the finale.
  - Show "6 of 10" in Up Next and the calendar.
- **Done when:** a weekly season with its planned count aired gets its verdict three days after the finale; the
  quiet-period rule stays for seasons without a count.

## Features

All the features from the review have shipped; new ideas go here.

### F2. Affiliate programs

**P2 · S**

- **Why:** affiliate links are counted and can be wrapped (CONTEXT.md, Affiliate links), but no program is configured,
  so every click goes out plain and earns nothing.
- **What:** join the networks the streaming services use (most run through Impact, Rakuten or Partnerize), add a program
  per service and region with its postback key as a secret, and point each network's postback at the BFF. Filter known
  bots from `/go` (user agent) before the counts are used in a negotiation. Count clicks from Your services and the
  catch-up and backlog pages once they link to services.
- **Done when:** a test conversion from a real network shows on `/settings/affiliates` against its click, and the
  report's top unmonetised service has a program.

## Technical debt

### T1. Split `CatalogSynchronizer`

**P3 · M**

- **Why:** at 815 lines it's the largest file. It covers syncing a show, applying overrides, presumed-ended status,
  similar shows and aired-episode publishing.
- **What:** separate classes for override application, status rules and aired-episode publishing, with the sync loop
  calling them. Behaviour stays byte-identical; check it with a replay's events, as was done for `InsightsComputer`.

### T2. One stable-id helper

**P3 · S**

- **Why:** Research's `CatalogOverrides.OverrideId` (`CatalogClients.cs:37-44`) and Analytics'
  `SeasonVerdictPublication.VerdictId` both hash a key into a GUID, differently: one sets the version-8 bits, the other
  doesn't.
- **What:** one `StableId.From(string key)` in a shared library, used by both. Changing the verdict ids re-notifies
  live verdicts, so switch when none is fresh.

## Operations

### O1. A deployment

**P1 · L**

- **What** (Azure Container Apps, decided):
  - Publish the AppHost to Azure Container Apps (`aspire publish` / `azd`), with managed Postgres and PgBouncer (the
    connection budget in CONTEXT.md assumes a pooler once replicas multiply).
  - RabbitMQ as a container or a managed broker, Redis, Keycloak, and Model Runner where GPU or CPU capacity allows.
  - Start from the hosting estimate already made.
  - Send the services' OpenTelemetry to Azure Monitor, with a dashboard per service, and alert rules on the
    `tvtracker.alerts.firing` gauge (CONTEXT.md, Alerts); in development the Aspire dashboard is both.
- **Done when:** one command deploys a staging environment from `main`.

## From the analytics review (October 2026)

Done: shared readings in Analytics.Data (`CopyRules`, `InsightDocuments`), the API's `Viewer`/`ShowLoad` helpers,
transactional handlers, diffed lists, `RecordedAtUtc` on watches, the `cast_credits` index. Left for later:

- **R1. Key constants into IntegrationEvents.** `LandingVerdicts`, `WorthItVerdicts`, the `ShowTags` keys and
  `DiscoveryLists` live in Analytics.Data, which only Analytics references, so the UI (`Models.cs`, `LandingPanel`,
  `WorthItPanel`, `ShowTagOptions`, `Discover.razor`), Mcp.Server and LoadGen retype them as literals. Move the keys
  (not the thresholds) next to `SeasonVerdicts` and `DnaTraits`, and delete the literals.
- **R2. Reunion radar precomputed.** `ReunionsApi` loads every cast row of every loved show and every provider credit
  of their actors per request (uncached; ~10-20k rows for 100 loved shows). An hourly "actors together again" table in
  the worker would leave only the love weights per request.
- **R3. `ShowInsights.cs` in document order**, split into the show document, the person documents, the network
  report and `InsightRules`; and `DateTimeOffset` for the user-activity columns (a CLR-type-only migration), so the
  comparisons stop converting.
- **R4. Shows stubbed from `EpisodesScheduled`** (a title and nothing else, until a replay) get full documents and
  forecasts; a "known from a catalog event" flag the pipeline skips would hide them until then.
- **R5. Season verdict list over a generated column** (`finale_aired_at` from the document, indexed) once the catalog
  is thousands of shows; today the 5-minute cache covers the scan.

