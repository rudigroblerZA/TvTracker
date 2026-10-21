# TV Tracker

A TV Time–style app for tracking shows: what you follow, what you've watched, when new episodes air, and what to watch next.
Built as eShop-style microservices on .NET 10 and Aspire, with a Blazor web app and a MAUI Blazor Hybrid mobile app
that share one Razor component library. The design behind it is in the "TV Tracker – Service Design" doc.

> **Build status:** everything except the MAUI app builds without errors with `dotnet build` on the .NET 10 SDK.
> The MAUI app (`src/Clients/MobileApp`) needs the MAUI workload and hasn't been compiled yet.

## Prerequisites

- .NET 10 SDK
- Docker Desktop or Podman (Aspire runs PostgreSQL, Redis, RabbitMQ and Keycloak as containers)
- For the mobile app only: the MAUI workload (`dotnet workload install maui`), plus Xcode for iOS or the Android SDK for Android

## Run it

```bash
dotnet run --project src/TvTracker.AppHost
```

The Aspire dashboard opens. The first run pulls container images and imports the Keycloak realm, so it takes a few minutes.
When everything is running, open the `webapp` link and sign in:

- Demo user: **demo / demo**
- Or create an account from the sign-in page (Keycloak handles sign-up and password reset)

The catalog is filled from **[TVmaze](https://www.tvmaze.com/api)** (free, no API key). Every hour the sync worker imports the 40 most
popular shows airing today: the US and GB broadcast schedules plus streaming, without news, talk, sports and daily soaps.
The catalog grows as the schedule changes. Imported shows are refreshed daily (ended shows weekly), and a show
is refreshed promptly when someone follows it. The first sync takes a minute or two, because calls are throttled to stay under
TVmaze's limit of 20 calls every 10 seconds. Settings are under `TvMaze` in `Catalog.SyncWorker/appsettings.json`.

If TVmaze's combined show request fails (a 5xx after the retries; seen during an outage), the sync worker switches to
separate requests for 15 minutes. It fetches the show, episodes, seasons, cast and images in parallel; those kept
working when the combined request didn't.
- **Needed:** the show, episodes and seasons. If one of them fails, the show isn't imported this time.
- **Optional:** cast and images. If they fail, the show imports without them, and a refresh keeps what it already had.
- **One show at a time:** a show TVmaze keeps failing is skipped (imports), retried in an hour (refreshes and the
  creatives backfill), and doesn't stop the others.

Search finds any show on TVmaze, not just imported ones. Before searching, Catalog.API asks the sync worker
(`POST /api/v1/imports/search`, internal only) to import TVmaze's top 5 matches it doesn't have yet, then searches the catalog.
A new search term takes a second or two. Each term goes to TVmaze at most once every 6 hours. If the worker is down or takes
longer than 10 seconds, search returns what the catalog already has, and the import still finishes for the next search.

Search also matches by meaning. Results start with shows whose title contains the term (most popular first), followed
by up to 30 shows whose title, genres, network and summary are close in meaning ("naval crime investigators" finds NCIS,
"cooking competition" finds The Great British Bake Off). The vectors come from all-MiniLM-L6-v2, run locally with ONNX
Runtime (`Catalog.Embeddings`), so there's no API key or extra container. The model (about 90 MB) is downloaded from
Hugging Face on first start, checksummed, and cached in the user's local app data folder (`Embeddings:ModelDirectory`
to change it). Until it loads, search matches titles only. The sync worker embeds new and refreshed shows every 30 seconds
and keeps the vectors in `show_embeddings`, a pgvector column with an HNSW index. Postgres runs from the
`pgvector/pgvector` image for that.

Search is rate limited (429 with `Retry-After` when over), per signed-in user, or per client IP for anonymous visitors
(the last `X-Forwarded-For` entry: the mobile gateway adds it, and the web app forwards each browser's address, captured
when the page loads). Calls with neither, from other services, share one larger limit. The web app doesn't retry 429s or
count them toward its circuit breaker, so one visitor hitting the limit can't affect others. Behind a reverse proxy, configure
ForwardedHeaders in the web app so it sees browsers' addresses rather than the proxy's. Separately, at most 30 new search terms a minute go to TVmaze across all callers; past that, search
answers from the catalog. Limits are under `SearchRateLimit` in Catalog.API's configuration (see `SearchRateLimitOptions`).

To work offline, set `Catalog:Provider` to `mock` to use `MockCatalogProvider`: 12 invented shows whose air dates are relative
to the current week, so there are always episodes that aired recently and episodes coming up. Shows are stored per provider,
so switching leaves the other provider's shows in the database without refreshing them.

TVmaze data is licensed CC BY-SA; the app footer carries the required credit. TVmaze has no keywords, "similar shows" or
per-region streaming availability, so similar-show lists are empty, keywords are the show type and language, and a worldwide
streaming service (e.g. Netflix) is listed under each region in `TvMaze:WatchRegions`.

Show pages open with the show's artwork: TVmaze's large background image across the top, faded into the page, with
the poster overlapping it. A show without one gets TVmaze's thin logo banner as a strip instead, and a show with
neither keeps the plain poster layout. An image that fails to load falls back to the next. Shows imported earlier
get their banner on their next refresh.

Useful local addresses:

| What | Address |
| --- | --- |
| Web app | https://localhost:7120 |
| Mobile BFF | http://localhost:5110 |
| Keycloak admin console | http://localhost:8080 (admin / admin) |
| RabbitMQ management | link on the Aspire dashboard |

### Bulk import

`tvtracker-import` (`src/Tools/Catalog.ImportCli`) fills the catalog in bulk while the AppHost is running. It starts a job
on the sync worker (`/api/v1/imports/bulk`, internal only), which imports the shows through the same synchronizer
and TVmaze rate limit as everything else, about two shows a second, and the tool shows the job's progress:

```bash
cd src/Tools/Catalog.ImportCli
dotnet run -- titles shows.txt     # the best TVmaze match for each line (# comments and blank lines skipped; - for stdin)
dotnet run -- ids 82 431 169       # TVmaze show ids (or --file ids.txt)
dotnet run -- top 500              # TVmaze's most popular shows; reads TVmaze's whole index first (about 4 minutes)
dotnet run -- status               # follow the running import, or show the last one
dotnet run -- cancel               # stop after the current show
```

Shows already in the catalog are skipped, so running a job again picks up where a cancelled or interrupted one stopped.
One job runs at a time, at most 5000 shows each. Ctrl+C stops following, not the import (`--detach` doesn't follow
at all). The tool talks to `http://localhost:5107` (`--url` or `TVTRACKER_SYNC_URL` to change it). Exit codes: 0 done,
2 done with some shows failing, 1 cancelled, failed or unreachable. Job state lives in the worker's memory, so a
worker restart ends the job. Imported shows publish the usual catalog events and are embedded for semantic search
within a minute.

### Ops dashboard

http://localhost:5102/ops (Catalog.API) is a one-page developer dashboard, refreshed every 10 seconds:
- **Catalog:** what it holds (shows by status, episodes, people and credits), and the semantic index (current, stale, missing).
- **Sync:** shows due for refresh and when the last sync ran.
- **Analytics:** insights computed and when, counts of users, ratings, watches and follows, top episodes, upcoming premieres
  and the shows with the most shared cast.
- **Actors, shows and genres:** the most-credited actors, recently added shows (and whether they're indexed yet), and genres.
- **Bulk import:** the latest bulk import job.

Catalog.API gathers the Analytics and sync worker sections server-side (`/api/v1/ops/stats` on Analytics.API,
`/api/v1/imports/bulk/current` on the sync worker); a service that's down just leaves its section empty. Since it
isn't signed in, it and the Analytics stats endpoint are only mapped in Development.

### Mobile app

The mobile app isn't started by the AppHost. With the AppHost running, run `src/Clients/MobileApp` from Visual Studio,
VS Code (C# Dev Kit + .NET MAUI extension) or Rider on an Android emulator, iOS simulator, Mac or Windows.

- It talks to the Mobile BFF and Keycloak on `localhost` (`10.0.2.2` from the Android emulator); see `Services/MobileSettings.cs`.
- Sign-in opens Keycloak in the system browser and returns via `tvtracker://callback`.
- Deep links: `tvtracker://shows/{id}` opens a show.
- Offline: show pages, Up Next, lists and progress are cached in SQLite; follows, watched marks, ratings and list changes made offline are queued and sent when the connection is back.

## Architecture

| Project | Path | Purpose |
| --- | --- | --- |
| TvTracker.AppHost | `src/TvTracker.AppHost` | Aspire orchestration; `Realms/` holds the Keycloak realm |
| TvTracker.ServiceDefaults | `src/TvTracker.ServiceDefaults` | OpenTelemetry, health checks, service discovery, resilience, JWT validation |
| TvTracker.EventBus | `src/TvTracker.EventBus` | `IEventBus`, handlers, subscriptions |
| TvTracker.EventBus.RabbitMQ | `src/TvTracker.EventBus.RabbitMQ` | RabbitMQ implementation (one queue per service, retry-once then drop) |
| TvTracker.Persistence | `src/TvTracker.Persistence` | EF Core outbox, inbox (idempotent consumers), database initialisation |
| TvTracker.IntegrationEvents | `src/TvTracker.IntegrationEvents` | Event contracts |
| Identity.API | `src/Services/Identity` | Profile, timezone/region, account deletion, data export (Keycloak does sign-in) |
| Catalog.Data / Catalog.API | `src/Services/Catalog` | Shows, seasons, episodes, cast and people (actor pages), where to watch, search (title and semantic), schedule |
| Catalog.ImportCli | `src/Tools` | `tvtracker-import`: bulk import by titles, TVmaze ids or top N, through the sync worker |
| Catalog.Embeddings | `src/Services/Catalog` | Local text embeddings (all-MiniLM-L6-v2 via ONNX Runtime) for semantic search |
| Catalog.SyncWorker | `src/Services/Catalog` | Imports/refreshes shows from the provider (including search matches for Catalog.API), embeds shows for semantic search, detects changes, publishes events, detects aired episodes |
| Tracking.API | `src/Services/Tracking` | Follows, statuses, watched episodes, progress, Up Next, calendar, lists, stats |
| Ratings.API | `src/Services/Ratings` | Ratings, summaries, spoiler-safe comments, reactions |
| Recommendations.API | `src/Services/Recommendations` | Similar shows, personalised picks (content + collaborative), dismissals |
| Notifications.API | `src/Services/Notifications` | Preferences, quiet hours, devices, inbox, push / web push / email delivery |
| Analytics.Worker / Analytics.API | `src/Services/Analytics` | Insights for signed-in users: best and worst seasons, top episodes, season trends, episode patterns, run facts, premiere build-up, shared cast, discovery lists, actor insights, your taste |
| Mobile.Bff | `src/ApiGateways/Mobile.Bff` | YARP gateway for the mobile app (`/catalog/...`, `/tracking/...`, ...) |
| TvTracker.UI | `src/Clients/TvTracker.UI` | Every page and component, shared by web and mobile |
| WebApp | `src/Clients/WebApp` | Blazor web app; OIDC sign-in, tokens kept server-side in Redis, browser push |
| MobileApp | `src/Clients/MobileApp` | MAUI Blazor Hybrid app; OIDC + PKCE, SQLite offline store, deep links |

How events flow:

- **Catalog.SyncWorker** publishes `ShowAdded`, `ShowUpdated`, `EpisodesScheduled` (one per show sync, up to 500 episodes each), `EpisodeAired`, `SeasonAnnounced`, `ShowStatusChanged`.
- **Tracking.API** publishes `ShowFollowed`, `ShowUnfollowed`, `EpisodeWatched`, `EpisodeUnwatched`, `ShowCompleted`.
- **Ratings.API** publishes `ShowRated`, `EpisodeRated`, `ShowRatingRemoved`, `EpisodeRatingRemoved`. **Identity.API** publishes `UserRegistered`, `UserProfileUpdated`, `UserDeleted`.
- Events are saved in each service's outbox table in the same transaction as the change, then published; a background publisher retries anything left behind.
- Consumers record processed event ids, so redelivered events are ignored.
- **Analytics.Worker** keeps its own copy of the catalog and of our users' ratings, watched episodes and follows from
  these events and recomputes insights hourly (Analytics.API only reads the results). Figures from our users (rating
  averages and histograms, viewers, drop-off, follows) appear only with at least 5 users behind them. Ratings, watches
  and follows made before Analytics existed aren't replayed; they accumulate from then on. On its first start it asks the sync worker to replay the
  catalog (`POST /api/v1/imports/replay`, internal), so shows imported before it existed reach it too.
- **What Analytics shows:**
  - **Show page — tags:**
    - **Drops all at once:** the latest season released in one day, or "drops in parts" for up to 3 batches.
    - **Bingeable:** about 10 episodes a season or fewer, under 50 hours in all, nothing left to air.
    - **Weekend binge:** everything aired in 12 hours or less.
    - **Fan favorite:** our users average 4.3★ or more, from at least 5 ratings, or TVmaze rates it 8.5 or higher.
    - **Limited series, quick episodes** (30 minutes or less) and **long-running** (10 seasons or more).
    - **Prolific creator / prolific producer:** made by someone credited on 10 or more shows on TVmaze. The label depends
      on whether they created this show. The catalog sends who made each show with its show events, and people's career
      totals as `PeopleCareersUpdatedIntegrationEvent`, so Analytics can tag the show. It appears on cards and in the
      search filter, and the show page shows the fuller "made by" block instead.

    Weekly release isn't tagged, since nearly every show has it. Discover has matching bingeable, weekend-binge and
    fan-favorite lists.

    Show cards (search results, home, For you, lists and the rest) also carry up to two tags, for signed-in users.
    The cards on a page ask together, so their tags come from Analytics in one batched request
    (`GET /api/v1/insights/tags?ids=…`).

    Search can filter by tag for signed-in users. The filter is a "kind of show" dropdown, or `tags=bingeable,fan-favorite` on
    `GET /api/v1/shows`, where shows must have every tag listed. The API answers 401 to a tag filter without a valid
    token; the rest of search stays public, and a shared `?tag=` link opened while signed out searches without the tag. Analytics sends tag changes to the catalog after each
    hourly run (`ShowTagsChangedIntegrationEvent`). It also sends every show's tags after it starts and then daily, in
    case an event is lost. The catalog keeps them in `shows.Tags`, so the filter runs inside the search query and
    paging, title matches and similar-meaning matches all respect it.
    Cards in filtered results show the filtered tag first.
  - **Show page — episode patterns:** consistency, from the spread of episode ratings (steady below 0.3, rollercoaster
    from 0.6), where the show peaked and whether it declined after, whether finales beat the rest of their season, and
    each season's best episode.
  - **Insights page — discovery lists**, overall or by genre:
    - hidden gems: rated 7.5+, outside the catalog's most popular 40% on TVmaze, and few of our users follow them;
    - most consistent and biggest rollercoasters, among well-rated shows;
    - best season finales.
  - **Show page — prolific creators and producers:** who made the show, featuring the most prolific person.
    - **Prolific** means credited behind the camera on 10 or more shows on TVmaze. Dick Wolf has 57 and Chuck Lorre 19.
    - **What it shows:** their totals, best-known shows, and their other shows in the catalog.
    - **Who's featured:** the most prolific person, plus any other prolific creator. A production company's other
      executive producers aren't listed.
    - **Data:** the sync worker imports creators, developers and executive producers with each show, from
      `/shows/{id}/crew`. That's a separate request, because TVmaze's crew embed can return 502 when the rest works. A
      failed crew call doesn't stop the import; the creatives are filled in within the hour. Every minute it also fetches 30 people's careers from `/people/{id}/crewcredits`, refreshed monthly: every
      creator, and anyone credited on two or more catalog shows. Shows imported before this are backfilled at 30 a minute.
    - **Person pages:** they show the same career line and a "Made" row.
  - **Person page — actor insights:** their shows' average rating, best-rated show, the years their shows aired,
    co-stars they share two or more shows with, and how many of our users follow their shows.
  - **Stats page — your taste:** computed on request from your own data only.
    - How your ratings compare with TVmaze's (stars doubled to its 0-10).
    - The genres you rate highest.
    - Time watched over the last 12 months.
    - Shows you're caught up on, watching, or dropped (nothing watched for 30 days).

  Watched events carry the time the user watched, so episodes the mobile app marks while offline count in the right month.
- The sync worker starts only after the consuming services report healthy, which includes "queue bound and consuming", so no early events are lost.

## Database schema

Catalog and Analytics use EF Core migrations (`Catalog.Data/Migrations`, `Analytics.Data/Migrations`); Catalog.API and
Analytics.API apply them at startup. The other services
still use `EnsureCreated`, which creates a missing database but never changes an existing one.

After changing either model, add a migration (the `dotnet-ef` version is pinned in `dotnet-tools.json`):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Services/Catalog/Catalog.Data --output-dir Migrations
# or, for Analytics:
dotnet ef migrations add <Name> --project src/Services/Analytics/Analytics.Data --output-dir Migrations
```

**Upgrading a local database created before migrations:** Catalog.API stops with a message saying the database
can't be upgraded in place. Stop the AppHost, delete the Postgres data volume (`docker volume ls | grep postgres`,
then `docker volume rm <name>`) and start it again; every database is recreated and the catalog re-imported.
Keycloak users are kept, since Keycloak has its own volume.

## Configuration still to do

| Feature | What to set | Where |
| --- | --- | --- |
| Mobile push | Azure Notification Hubs connection string, plus an `IPushTokenProvider` that gets FCM/APNs tokens (e.g. Plugin.Firebase) | Notifications.API user secrets `NotificationHubs:ConnectionString`; `MobileApp/Services/MobilePlatformServices.cs` |
| Browser push | VAPID keys (e.g. `npx web-push generate-vapid-keys`) | Notifications.API user secrets `WebPush:PublicKey`, `WebPush:PrivateKey` |
| Email | Azure Communication Services connection string and sender | Notifications.API user secrets `Email:ConnectionString`, `Email:SenderAddress` |
| Google / Apple sign-in | Add identity providers to the `tvtracker` realm | Keycloak admin console |

Until configured, each notification channel logs what it would have sent (visible in the Aspire dashboard logs); the in-app inbox always works.

## Before production

- Move the remaining services from `EnsureCreated` to EF Core migrations, as Catalog has (see Database schema).
- Change the development secrets in `AppHost.cs` and `Realms/tvtracker-realm.json`, and turn on HTTPS for Keycloak.
- Remove `usesCleartextTraffic` (Android) and `NSAllowsLocalNetworking` (iOS), and point the mobile app at real hosts.
- Add a dead-letter queue for events that fail twice, and tests.

## Package versions

Versions live in `Directory.Packages.props` (the MAUI app pins its own in its project file). Aspire is pinned to 13.0.2,
which may be behind the latest release. Run `aspire update` (or `dotnet list package --outdated`) to bring them up to date.

## Not built yet

Social.API (friends, activity feed, shared lists) and the weekly digest email are planned for a later round.
