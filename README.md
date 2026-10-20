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

Search finds any show on TVmaze, not just imported ones. Before searching, Catalog.API asks the sync worker
(`POST /api/v1/imports/search`, internal only) to import TVmaze's top 5 matches it doesn't have yet, then searches the catalog.
A new search term takes a second or two. Each term goes to TVmaze at most once every 6 hours. If the worker is down or takes
longer than 10 seconds, search returns what the catalog already has, and the import still finishes for the next search.

Search is rate limited (429 with `Retry-After` when over), per signed-in user, or per client IP for the mobile app
(the last `X-Forwarded-For` entry, which the gateway adds). The web app calls from its own server, so anonymous web visitors
share one larger limit. Separately, at most 30 new search terms a minute go to TVmaze across all callers; past that, search
answers from the catalog. Limits are under `SearchRateLimit` in Catalog.API's configuration (see `SearchRateLimitOptions`).

To work offline, set `Catalog:Provider` to `mock` to use `MockCatalogProvider`: 12 invented shows whose air dates are relative
to the current week, so there are always episodes that aired recently and episodes coming up. Shows are stored per provider,
so switching leaves the other provider's shows in the database without refreshing them.

TVmaze data is licensed CC BY-SA; the app footer carries the required credit. TVmaze has no keywords, "similar shows" or
per-region streaming availability, so similar-show lists are empty, keywords are the show type and language, and a worldwide
streaming service (e.g. Netflix) is listed under each region in `TvMaze:WatchRegions`.

Useful local addresses:

| What | Address |
| --- | --- |
| Web app | https://localhost:7120 |
| Mobile BFF | http://localhost:5110 |
| Keycloak admin console | http://localhost:8080 (admin / admin) |
| RabbitMQ management | link on the Aspire dashboard |

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
| Catalog.Data / Catalog.API | `src/Services/Catalog` | Shows, seasons, episodes, cast, where to watch, search, schedule |
| Catalog.SyncWorker | `src/Services/Catalog` | Imports/refreshes shows from the provider (including search matches for Catalog.API), detects changes, publishes events, detects aired episodes |
| Tracking.API | `src/Services/Tracking` | Follows, statuses, watched episodes, progress, Up Next, calendar, lists, stats |
| Ratings.API | `src/Services/Ratings` | Ratings, summaries, spoiler-safe comments, reactions |
| Recommendations.API | `src/Services/Recommendations` | Similar shows, personalised picks (content + collaborative), dismissals |
| Notifications.API | `src/Services/Notifications` | Preferences, quiet hours, devices, inbox, push / web push / email delivery |
| Mobile.Bff | `src/ApiGateways/Mobile.Bff` | YARP gateway for the mobile app (`/catalog/...`, `/tracking/...`, ...) |
| TvTracker.UI | `src/Clients/TvTracker.UI` | Every page and component, shared by web and mobile |
| WebApp | `src/Clients/WebApp` | Blazor web app; OIDC sign-in, tokens kept server-side in Redis, browser push |
| MobileApp | `src/Clients/MobileApp` | MAUI Blazor Hybrid app; OIDC + PKCE, SQLite offline store, deep links |

How events flow:

- **Catalog.SyncWorker** publishes `ShowAdded`, `ShowUpdated`, `EpisodesScheduled` (one per show sync, up to 500 episodes each), `EpisodeAired`, `SeasonAnnounced`, `ShowStatusChanged`.
- **Tracking.API** publishes `ShowFollowed`, `ShowUnfollowed`, `EpisodeWatched`, `ShowCompleted`.
- **Ratings.API** publishes `ShowRated`, `EpisodeRated`, `ShowRatingRemoved`, `EpisodeRatingRemoved`. **Identity.API** publishes `UserRegistered`, `UserProfileUpdated`, `UserDeleted`.
- Events are saved in each service's outbox table in the same transaction as the change, then published; a background publisher retries anything left behind.
- Consumers record processed event ids, so redelivered events are ignored.
- The sync worker starts only after the consuming services report healthy, which includes "queue bound and consuming", so no early events are lost.

## Database schema

Catalog uses EF Core migrations (`Catalog.Data/Migrations`); Catalog.API applies them at startup. The other services
still use `EnsureCreated`, which creates a missing database but never changes an existing one.

After changing the Catalog model, add a migration (the `dotnet-ef` version is pinned in `dotnet-tools.json`):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Services/Catalog/Catalog.Data --output-dir Migrations
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
