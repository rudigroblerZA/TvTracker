# Backlog

What to build or fix next, from a code review of the whole repository on 28 October 2026 (about 40,000 lines across
13 services, the shared UI and the tools), plus the product ideas from FEATURES.md. Each item says why (with the code
that shows it), what done looks like, its priority and its size.

- **Priority:** P1 do next, P2 soon, P3 when convenient.
- **Size:** S a day or less, M a few days, L a week or more.
- **Keeping it current:** remove an item when it ships, and record the decision behind it in CONTEXT.md.

## Now

In this order. The first four make every later change safer; the last two are cheap, visible speed-ups.

| # | Item | Priority | Size |
| --- | --- | --- | --- |
| Q1 | [Tests and CI](#q1-tests-and-ci) | P1 | M |
| R1 | [Outbox and inbox retention](#r1-outbox-and-inbox-retention) | P1 | S |
| R2 | [Retry with backoff, and a dead-letter queue](#r2-retry-with-backoff-and-a-dead-letter-queue) | P1 | M |
| S1 | [Secrets out of the AppHost](#s1-secrets-out-of-the-apphost) (before any deployment) | P1 | M |
| P1 | [Show page loads its data in parallel](#p1-show-page-loads-its-data-in-parallel) | P2 | S |
| P2 | [Cache feature-flag answers in services](#p2-cache-feature-flag-answers-in-services) | P2 | S |

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

### Q1. Tests and CI

**P1 · M**

- **Why:** there are no tests and no CI (CONTEXT.md, Known gaps). Every rule since the start has been checked by hand
  against a running AppHost. The last two reviews (season verdict, Ask) found real defects that a unit test would have
  caught.
- **What:**
  - A GitHub Actions workflow that builds `src/TvTracker.AppHost` and `src/Tools/Catalog.ImportCli` and runs `dotnet test`
    on pull requests. The MAUI app needs its workload, so it's Q3.
  - A test project per service, starting with the pure logic that's easiest to get wrong:
    - **Analytics insights**, with a fixed clock and hand-built `ShowInsightContext`s: `SeasonVerdictInsight` (when a
      season is over, verdict ladder), `LandingInsight`, `GetsGoodInsight`, `ForecastModels`, `DnaInsight` anchors.
    - **Planners:** `TonightPlanner` (knapsack, mix cap, overrun), the catch-up planner's week split.
    - **Ask's guards:** `AnswerLoop`'s `Missing`, `NotAllowed` and `UnknownId`, and `Toolbox.Compact`.
    - **Catalog:** Universes' character matching and franchise names, `CatalogSynchronizer.StatusOf` (presumed
      ended), `Text` / HTML stripping.
    - **Ratings:** `Moderation` (blocked terms, links held, hourly limits).
  - Add the test command to CLAUDE.md's Commands.
- **Done when:** the workflow runs on every PR and blocks merging when red, and each area above has tests for its
  documented rules (the ones CONTEXT.md lists with examples).

### Q2. An evaluation set for Ask

**P3 · S**

- **Why:** Ask's answers depend on the chat model, and qwen3 0.6B misreads results (CONTEXT.md, "Ask the app"). Larger
  models couldn't be tried in the sandbox, so nobody knows which one is worth the hardware.
- **What:** 20-30 questions with the facts a right answer must contain ("Ted Lasso season 4: as good as the others"),
  and a tool in `src/Tools` that asks each through Assistant.API and scores the answers.
- **Done when:** one command scores any model set in `ModelRunner:ChatModel`, and README says which model scored how.

### Q3. Compile the MAUI app in CI

**P2 · M**

- **Why:** the MAUI app has never been compiled (CONTEXT.md, Known gaps), so changes to TvTracker.UI's shared pages
  may already have broken it.
- **What:** a CI job on a runner with the MAUI workload that builds `src/Clients/MobileApp` for Android.
- **Done when:** it builds in CI, and fixes for whatever was broken are merged.

## Reliability

### R1. Outbox and inbox retention

**P1 · S**

- **Why:**
  - `integration_event_log` (the outbox) and `processed_integration_events` (the inbox) are never trimmed; nothing in
    `TvTracker.Persistence` deletes from them.
  - After about two weeks of development use, Catalog's outbox held 8,645 rows (45 MB), and Social's inbox 27,117 rows.
    Production grows them with every write and every handled event.
  - `OutboxPublisher` (`OutboxPublisher.cs:26`) scans the outbox every 15 seconds; its `(State, CreatedAtUtc)` index
    keeps that cheap, but table bloat and backups grow regardless.
- **What:** a retention pass in `OutboxPublisher` (or a sibling background service in TvTracker.Persistence):
  - delete published outbox rows older than 7 days;
  - delete inbox rows older than 30 days, longer than any redelivery or snapshot window, so a late redelivery is still
    recognised.
  - Delete in batches (`ExecuteDeleteAsync` with a limit), hourly.
- **Done when:** both tables stay bounded on a long-running AppHost, with a CONTEXT.md note on the two windows.

### R2. Retry with backoff, and a dead-letter queue

**P1 · M**

- **Why:**
  - A failing message is requeued once, immediately, then dropped (`RabbitMQEventBus.cs:185`). A database hiccup of a
    few hundred milliseconds can therefore lose an event for good.
  - Lost events are healed only where a periodic full send exists (tags, DNA, snapshots, catalog replay). Nothing heals
    a lost season verdict, an account deletion step, a list share or a correction.
  - CONTEXT.md lists "no dead-letter queue" as a known gap.
- **What:**
  - Delayed retries: for example 1 s, 10 s and 60 s through a per-queue retry queue with a message TTL that dead-letters
    back to the main queue, counting attempts in a header.
  - Then a dead-letter queue per service queue.
  - Show the dead-letter depth on `/ops`, with a way to replay a dead letter.
- **Done when:** a handler that throws twice and then succeeds still processes the event; one that always throws lands
  in the dead-letter queue, visible on `/ops`; CONTEXT.md's Events section is updated.

### R3. Move Identity and Notifications to migrations

**P2 · S each**

- **Why:**
  - Both still use `EnsureCreated` (`Identity.API/Program.cs:12`, `Notifications.API/Program.cs:15`), which never
    changes an existing database.
  - This has already cost a feature: the season verdict had to borrow the "new seasons" notification preference,
    because a preference of its own needs a column.
- **What:** the baseline pattern other services used. Make `InitialCreate` from the unchanged model, pass
  `baseline: "InitialCreate"`, diff it against an `EnsureCreated` database with `pg_dump -s`, and add the migration
  commands to CLAUDE.md.
- **Done when:** both services start on an existing database and on an empty one, and CONTEXT.md's Persistence section
  lists them as migrated.

### R4. Inbox and delivery retention in Notifications

**P3 · S**

- **Why:** `InboxItem` and `Delivery` rows (`NotificationsContext.cs:79,116`) are deleted only with the account, so a
  long-time follower's inbox grows forever.
- **What:** delete inbox items read more than 90 days ago (or any older than a year), and sent deliveries older than
  30 days.
- **Done when:** a scheduled pass trims both; README says how long the inbox keeps items.

## Security

### S1. Secrets out of the AppHost

**P1 before any deployment · M**

- **Why:**
  - Client secrets, the gateway key and the app admin password are constants in `AppHost.cs` (lines 80-83, 192), and
    the realm file carries the same secrets.
  - Fine for development (CONTEXT.md lists it), but a deployment from this AppHost would ship known secrets.
- **What:**
  - Make each one an Aspire secret parameter (`AddParameter(..., secret: true)`) with the current value as the
    development default only.
  - Generate the realm's client secrets at deploy time, or set them through the admin API on start, as
    `AdminAccountSeeder` does.
  - Document where production values come from (Key Vault, or `azd` environment secrets).
- **Done when:** a publish (`aspire publish` / `azd`) without values fails rather than using the development ones.

### S2. Service-to-service auth for internal APIs

**P2 · M**

- **Why:**
  - The sync worker's import, replay and override APIs (`ImportApi.cs:19`, `OverridesApi.cs:17`) accept any caller.
    Catalog.API, Ratings, Research, Analytics.Worker and Social call them with no token.
  - They aren't routed by the BFF and have no external endpoint, so today only something inside the environment can
    reach them. One compromised container could still rewrite catalog data through overrides.
- **What:** client-credentials tokens for service callers, as `tvtracker-mcp` already does for Analytics, with a role
  claim the sync worker requires. Or, if hosting gives network isolation (Container Apps internal ingress), document
  that as the control.
- **Done when:** an unauthenticated call to `/api/v1/overrides` gets 401, and the five callers still work.

### S3. Accept the Android emulator's issuer only in Development

**P3 · S**

- **Why:** `AuthenticationExtensions.cs:47` always accepts `http://10.0.2.2:8080/realms/tvtracker` as an issuer. Tokens
  still need Keycloak's signing keys, so the risk is small, but a development address has no place in production
  validation.
- **What:** add it to `ValidIssuers` only when the environment is Development.
- **Done when:** production configuration lists one issuer.

### S4. Rate limits beyond search

**P2 · S**

- **Why:**
  - Only Catalog's search and mood scoring are rate-limited (`SearchRateLimiting`).
  - Everything else relies on sign-in: follows, watches, lists, ratings, social follow and people search, and the
    data export, which fans out to five services.
  - Ratings has its own hourly post limits and Ask its own queue.
- **What:**
  - Move `SearchRateLimiting`'s partitioning (user, else forwarded IP, else shared) into ServiceDefaults as a general
    per-user policy.
  - Apply it to every mutating endpoint, with tighter ones on people search and the export.
- **Done when:** a load-test user hammering writes gets 429s with `Retry-After`, and normal use (`tvtracker-load run`)
  sees none.

### S5. Research in the data export

**P3 · S**

- **Why:** saved research versions record the moderator's id and name, but the export only asks tracking, ratings,
  recommendations, notifications and social (`ProfileApi.cs:32`). Account deletion already handles Research.
- **What:** `GET /api/v1/me/export` on Research.API (versions the user saved), added to `ExportSources`.
- **Done when:** a moderator's export lists their saved versions.

### S6. Pin the address a researcher's page is fetched from

**P3 · S**

- **Why:** `WebPageSource` checks a host's addresses before connecting, so a host that changes its DNS answer between
  the check and the connection (DNS rebinding) could reach a private address. The class comment says so. It's
  moderator-only, which keeps the risk low.
- **What:** do the check inside `SocketsHttpHandler.ConnectCallback`, connecting to the address that was checked.
- **Done when:** the check and the connection use the same resolved address.

## Performance

### P1. Show page loads its data in parallel

**P2 · S**

- **Why:**
  - After the show itself, `ShowPage.razor` awaits seven independent calls one after another (lines 313-323): summary,
    similar, follow, progress, rating, mute and lists.
  - Its sections add about 13 more, for about 25 calls per show page.
- **What:** start the seven together (`Task.WhenAll`), as Insights now does, keeping the "signed in" branch.
- **Done when:** the show page's time to full render drops measurably in `tvtracker-load` (record before and after with
  `--csv`).

### P2. Cache feature-flag answers in services

**P2 · S**

- **Why:**
  - `RequireFeature` evaluates the flag on every request with a gRPC call to flagd. The provider's cache is off
    because it failed in OpenFeature.Providers.Flagd 0.8.0 (`FeatureFlagExtensions.cs:18`).
  - The UI caches answers for 30 s; services don't, so every gated request pays an extra round trip.
- **What:** first try a newer provider version with its cache on. Otherwise add a small in-process cache keyed by flag
  and targeting key, for 5-10 s.
- **Done when:** gated endpoints make no flagd call on a cache hit, and turning a flag off still applies within the
  cache time.

### P3. Fewer calls per show page

**P3 · M**

- **Why:** about 25 calls per show page, each with its own token handling and a flag check, from Blazor Server to many
  services.
- **What:** measure first (P1 and P2 may be enough). If not, batch what goes together, such as Tracking's follow,
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

### D2. A notification preference of its own for season verdicts

**P3 · S, after R3**

- **Why:** verdicts use the "new seasons" preference (CONTEXT.md, Season verdict), so you can't have one without the
  other.
- **What:** a `SeasonVerdicts` preference column, defaulting to the user's "new seasons" value, with its toggle in
  Settings.
- **Done when:** turning verdicts off stops them without stopping new-season notifications.

## Features

### F1. Crossover alerts

**P2 · S**

- **What:** when a crossover event in a universe you follow is scheduled ("Reckoning airs across Fire, Med and P.D. on
  Wednesday"), a notification and an Up Next line, with how many episodes you need to catch up on first.
- **Builds on:** watch order's crossover detection (`GetWatchOrderAsync`), follows, and the notifications path
  Season verdict used.

### F2. Weekly digest email

**P2 · M**

- **What:** one email a week, at the time the user picks: premieres of followed shows with catch-up time, season
  verdicts, what's up next, and anything the person they follow finished. It replaces several separate notifications.
- **Builds on:** Notifications' email sender (which logs until configured), premieres, season verdicts, Up Next.
- **Note:** "isn't built yet" in CONTEXT.md's Known gaps.

### F4. Research queue

**P3 · S**

- **What:** a list for moderators of popular shows with no research, and of shows where Wikidata disagrees with TVmaze
  (network, premiere date, episode count).
- **Builds on:** Research's gather and the `/ops` data-quality checks.

### F5. Taste twins

**P3 · L**

- **What:** users whose ratings track yours, and what they loved that you haven't seen. It's shown only with enough
  overlap and enough twins to stay anonymous (the 5-user rule).
- **Builds on:** Ratings, Recommendations' co-follow signal.

### F6. Awards shelf

**P3 · S**

- **What:** awards from saved research on person and show pages, and a "Prestige picks" list: the most-awarded shows
  you haven't started.
- **Builds on:** Research's awards fact, Backlog's show value.

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

### T3. Per-follower flag targeting for notifications

**P3 · S**

- **Why:** the season-verdict handler evaluates its flag once, without a user (`Handlers.cs`, `AnalyticsNotificationHandlers`),
  so a flag targeted at some users can't hold notifications back from the others.
- **What:** evaluate per recipient in `NotificationFanOut`, using `FeatureFlags.ContextFor`'s keys.

## Operations

### O1. A deployment

**P2 · L, after S1**

- **What:**
  - Publish the AppHost to Azure Container Apps (`aspire publish` / `azd`), with managed Postgres and PgBouncer (the
    connection budget in CONTEXT.md assumes a pooler once replicas multiply).
  - RabbitMQ as a container or a managed broker, Redis, Keycloak, and Model Runner where GPU or CPU capacity allows.
  - Start from the hosting estimate already made.
- **Done when:** one command deploys a staging environment from `main`.

### O2. Alerts and dashboards

**P3 · M**

- **What:** alerts on what's already logged or countable:
  - outbox rows pending for over 5 minutes;
  - dead-letter depth (after R2);
  - "Computing insights failed";
  - the sync worker's TVmaze error rate;
  - Ask's queue length.
  
  A dashboard per service from the existing OpenTelemetry data.
- **Done when:** each alert fires in a test of its failure.
