# TvTracker context

Background for anyone (or any agent) changing this codebase: what the system does, how its parts talk, the domain
terms, and the decisions behind the less obvious code. The README covers running it and user-facing behaviour; this
file covers why things are the way they are. Keep it current when a decision changes.

## What it is

A TV Time–style tracker: follow shows, mark episodes watched, see what airs next, rate, get recommendations and
insights. eShop-style microservices on .NET 10 and Aspire 13, a Blazor web app and a MAUI Blazor Hybrid app that
share every page through `TvTracker.UI`. The catalog comes from TVmaze (free, no key, CC BY-SA).

## Services and ownership

One Postgres database per service; no service reads another's tables. Data crosses service lines only as
integration events (RabbitMQ) or as an HTTP call through Aspire service discovery.

| Service | Owns | Notes |
| --- | --- | --- |
| Identity.API | profiles, region, timezone | Keycloak does sign-in; this orchestrates account deletion and data export across services |
| Catalog.API | read side of `catalogdb` | search (title + semantic + tag filter), schedule, people, `/ops` dashboard; applies Catalog migrations |
| Catalog.SyncWorker | write side of `catalogdb` | the only writer of shows; talks to TVmaze; publishes catalog events; embeddings; bulk import; replay |
| Tracking.API | follows, watches, lists | Up Next, calendar, stats |
| Ratings.API | ratings, comments, reactions | |
| Recommendations.API | picks, dismissals | |
| Notifications.API | inbox, devices, preferences | push/web push/email log instead of sending until configured |
| Analytics.Worker | write side of `analyticsdb` | own copy of catalog + user activity from events; hourly `InsightsPipeline`; publishes show tags |
| Analytics.API | read side of `analyticsdb` | insights, discovery lists, person insights, tags, "your taste"; applies Analytics migrations |
| Mobile.Bff | — | YARP; the mobile app only talks to this (`/catalog/**`, `/tracking/**`, ..., `/features/**` for flag evaluation over OFREP) |
| WebApp | — | Blazor Server host; OIDC, tokens in Redis |

## Events

- Contracts live in `src/TvTracker.IntegrationEvents`, one file per publishing service (`CatalogEvents.cs`, ...).
  They are records deriving from `IntegrationEvent`.
- **Evolving a contract:** add new fields as optional trailing parameters with a default, so events already in queues or
  outboxes still deserialize. Don't rename or reorder existing ones.
- **Publishing:** add the event to `IIntegrationEventOutbox` in the same `SaveChanges` as the change, then
  `PublishAddedAsync`. `OutboxPublisher` retries anything left behind.
- **Consuming:** an `IIntegrationEventHandler<T>` starts with `db.HasProcessedAsync(@event, nameof(Handler))`, and
  calls `db.MarkProcessed(...)` before saving. This is the inbox; redeliveries become no-ops.
- **RabbitMQ:** one queue per service; a failing message is retried once, then dropped (no DLQ yet). Lost-event
  healing is therefore done by periodic full syncs (tags daily) or replays (catalog).
- **Startup order:** the sync worker `WaitFor`s every consumer, whose health includes "queue bound and consuming".
- **Catalog replay:** `POST /api/v1/imports/replay` on the sync worker republishes the catalog. It covers shows,
  creatives and careers. Analytics requests it on first start, and when its copy lacks creatives.

## Catalog sync (TVmaze)

- **Provider abstraction:** `ICatalogProvider` has two implementations, `TvMazeCatalogProvider` and
  `MockCatalogProvider` (`Catalog:Provider=mock` for offline). Shows are stored per provider (`Source` + `ExternalId`).
- **Rate limit:** TVmaze allows 20 calls per 10 s. One limiter is shared by everything: hourly import, refreshes, search
  imports, bulk import, the creatives backfill and career refreshes. New background work must spend calls sparingly.
  `CreativesService`, for example, does 30 shows and 30 people a minute.
- **Combined request:** `/shows/{id}?embed[]=episodes&embed[]=seasons&embed[]=cast&embed[]=images`.
  - **Outage fallback:** on a 5xx after retries, `TvMazeEmbedOutage` switches to separate requests for 15 minutes.
  - **Needed parts:** the show, episodes and seasons. Without them the show isn't imported this time.
  - **Optional parts:** cast and images. `CastMissing` / `ImagesMissing` tell the synchronizer to keep what it has.
- **Crew:** always `/shows/{id}/crew` (the crew embed returned 502 while the rest worked). On failure the result is null,
  and the show is retried hourly by `CreativesService`.
- **Careers:** `/people/{id}/crewcredits?embed=show`, refreshed monthly. Fetched for creators and developers, and for
  anyone credited on at least 2 catalog shows.
- **Cast credits:** `/people/{id}/castcredits?embed=show`, for every actor in a catalog show (`CastCreditsService`).
  - **Pace:** 30 people a minute, leads of popular shows first, refreshed monthly; about 6,000 people take ~3.5 hours.
  - **Format:** stored as `PersonCastCredit` and sent as `PeopleCastCreditsUpdatedIntegrationEvent` (each person's
    list replaces the last); also sent with a replay.
  - **Character names:** they come in each credit's `_links.character.name`, so no character embed is needed.
  - **What's left out:** roles as themselves are skipped. TVmaze lists main casts only, so guest roles never appear.
- **Isolation:** one bad show never stops a loop. The import, refresh and backfill loops catch per show and retry later.
- **Search imports:** Catalog.API asks the worker (`POST /api/v1/imports/search`) to import TVmaze's top 5 matches.
  - Each term goes to TVmaze at most once every 6 h, and at most 30 new terms a minute overall.
  - It has a 10 s budget. Past that, search answers from the catalog.
- **IDs:** Show, Season, Episode, Person and WatchProvider ids are set by the app, so they're configured
  `ValueGeneratedNever`. Without it, EF treats new rows with preset ids as existing and refreshes fail with
  `DbUpdateConcurrencyException`.

## Search

- **Order:** title matches first (most popular first), then up to 30 semantic matches with cosine distance < 0.65.
- **Embeddings:** dmr/nomic-embed-text-v1.5 on Docker Model Runner by default (the AppHost's `Embeddings:Model`),
  or all-MiniLM-L6-v2 / bge-small run in-process with ONNX Runtime and Microsoft.ML.Tokenizers (`Catalog.Embeddings`).
  - Either way the model is exposed as `IEmbeddingGenerator<string, Embedding<float>>`, L2-normalised, 384 dimensions.
  - ONNX models are downloaded from Hugging Face at a pinned commit, sha256-checked, and cached under LocalApplicationData.
  - Until it loads, search is title-only.
  - **Configurable model:** `Embeddings:Model` picks an `EmbeddingModel` (files and checksums, pooling, prefixes,
    cutoff). Built in: all-MiniLM-L6-v2 (mean pooling, cutoff 0.65) and bge-small-en-v1.5 (CLS pooling,
    an instruction prefix on search terms, cutoff 0.45), plus dmr/nomic-embed-text-v1.5 on Docker Model Runner
    (the AppHost's default; task prefixes, truncated from 768, cutoff 0.38). With no configuration at all (services run
    outside the AppHost), `EmbeddingOptions.Model` falls back to all-MiniLM-L6-v2, which needs no server. The AppHost sets it on Catalog.API and the sync worker
    together; search only compares vectors made by the current model.
  - **Cutoffs are per model:** each model spreads distances differently, so `MaxDistance` was tuned on test
    searches, between the expected shows and unrelated searches. Both cutoffs held when the catalog grew from 150 to
    595 shows (42 searches): MiniLM ranks best (MRR 0.74; bge-small 0.68, nomic 0.63). The default is nomic on
    Docker Model Runner anyway, by choice: the model runs out of process and can be swapped for any Docker Hub model.
  - **Model servers:** `Source: Server` models are called over the OpenAI embeddings API by
    `ServerEmbeddingGenerator`. Docker Model Runner (`http://localhost:12434/engines/v1/`) is the default endpoint.
    - It uses its own `HttpClient`, without the standard resilience handler, so a server that's down doesn't add
      retries to every search. After a failure, calls fail fast for 30 s and one warning is logged per outage.
    - Docker Hub's embedding models are 768+ dimensions. `Truncate` cuts Matryoshka models to 384 and
      re-normalises; the column stays as it is.
    - Text is cut at about 4 characters a token (`MaxTokens`), since Model Runner's llama.cpp refuses input longer
      than its 512-token batch.
    - **Where Model Runner comes from:** the AppHost runs `docker/model-runner` as a container (CPU, models in the
      `tvtracker-model-runner` volume), unless `ModelRunner:Url` names one already running (Docker Desktop's can
      use the GPU). Either way it sets `Embeddings:Endpoint`, which applies to every server model without its own.
    - **Pulling:** `Pull` models are pulled through Model Runner's own API (`GET /models/{name}`,
      `POST /models/create`, next to `/engines/v1/`). Only the sync worker pulls (`AddLocalEmbeddings(pullModel:
      true)`), so the two services don't download the same model at once. It retries every minute until it works.
    - **Pull responses:** Model Runner streams progress as JSON lines, with the quotes HTML-escaped (`&#34;`), so
      the success check decodes first.
    - **Seeding the volume by hand:** the image runs as `modelrunner`, so the volume's files must be owned by
      that user. Docker sets this when it creates the volume on first mount; a volume made with
      `docker volume create` is root-owned.
  - **Dimensions:** `ShowEmbedding.Dimensions` (384) is the column size. Services refuse to start with a model of
    another size. Changing it needs a migration of the column and its HNSW index.
  - **Search terms vs show text:** `EmbeddingInput.QueryOptions` marks search terms, so the model's `QueryPrefix`
    applies to them. Changing a prefix without changing the model id doesn't re-embed the catalog.
- **Blend** (`BlendApi`): mixes two shows' vectors (weighted by the lean, normalised) and ranks the shows nearest
  the mix by `max(2w·dA, 2(1−w)·dB)`, their weighted distance to the farther show. Ranking by distance to the mix
  alone would favour shows very close to just one of the two (an NCIS spinoff, blended with Friends).
  - It asks for 40 candidates, because pgvector's HNSW scan returns at most `ef_search` (40 by default) rows.
  - Shows titled like either input are excluded (the classic Doctor Who, blended from the new one).
  - `Loose` compares the best score with the model's `MaxDistance`, so it holds when the model changes.
- **Universes** (`Catalog.API/Services/Universes.cs`): shows linked by the same actor playing a same-named
  character, or by a franchise name (`SameFranchise`), grouped into connected components.
  - **Franchise names:** a colon stem ("NCIS: Sydney" ~ "NCIS"), or the same first word plus two or more shared
    makers ("Chicago Fire" ~ "Chicago Med"). One maker isn't enough ("Tyler Perry's …" shows share a producer).
    Same-title pairs are left to the cast (revivals), since two unrelated shows can share a title.
  - **Why names as well:** crossovers made of guest appearances (One Chicago) never reach TVmaze's main casts.
  - **Why names:** TVmaze's character ids are per show; not one of 6,514 credits shared an id across shows. So
    `CharacterExternalIds` can't find crossovers, and Analytics' `SharedCast.SameCharacter` (which compares ids) is
    effectively never true.
  - **Matching:** names are split on "/" into aliases, and quoted nicknames, parentheses, titles and punctuation are
    dropped. A match is equal word sets, or one set inside the other with at least two words.
  - **Noise guards:** scripted/animation shows only, no self roles, and a link needs two characters or one
    full-name character. Checked by hand on the 595-show catalog: 30 universes, no false links.
  - **Naming:** a universe is addressed by its hub, chosen by these rules in order:
    1. the root title others extend ("NCIS");
    2. the show sharing the most characters;
    3. the show with the most links;
    4. the earliest.

    It's named after the hub, unless every show shares one name before a colon and none is just that ("Star Wars").
  - **Timeline:** `UniverseShowDto.Seasons` carries each season's first and last episode dates and aired counts.
    - **Overlap:** the "on air together" line counts a running show as on air until today, even between seasons.
  - **Watch order** (`GetWatchOrderAsync`): dated episodes of the universe's shows (season 0 left out, as Tracking
    does), ordered by air time, then show premiere order.
    - **Crossover events:** two shows' episodes count as one event when their titles match without part markers
      ("Infection, Part I" / "Infection, Part II"), within 7 days, ignoring placeholders such as "TBA".
    - **Why not same night:** same-night airing flagged 534 of One Chicago's 798 episodes, since the shows air back
      to back weekly.
    - **Progress:** the UI reads the user's progress from Tracking, one `/progress/{showId}` call per show.
  - **Link kinds:** "continuation" needs more than half of the later show's characters from earlier shows to come
    from that link, so a team-up (The Defenders) reads as a crossover.
  - **Caching:** the whole map is rebuilt from the database at most every 30 minutes (HybridCache).
- **Storage:** `show_embeddings` is a pgvector column with an HNSW cosine index. The Postgres image is
  `pgvector/pgvector:0.8.7-pg18-trixie`. It's trixie because existing databases were created with that collation
  version; bookworm reports a mismatch. The data volume is mounted explicitly at `/var/lib/postgresql`, because Aspire
  doesn't recognise the tag as Postgres 18.
- **Tag filter:** `tags=a,b` means every tag listed. It's stored in `shows.Tags` (text[], GIN index) and needs sign-in
  (401 otherwise); the rest of search is public.
- **Rate limits:** per user, else per client IP (the last `X-Forwarded-For` entry), else one shared bucket for services.

## Analytics domain

- **Copy of the world:** Analytics builds its own copy of the catalog and of users' ratings, watches and follows from
  events. Activity from before Analytics existed isn't replayed.
- **Insights pipeline** (`Analytics.Worker/Insights`): `InsightsPipeline` does the plumbing, and each insight is its
  own small class that only computes. All of them are registered in `AddInsights`, where order matters.
  - **Plumbing:** loads `InsightSources` once (catalog copy, ratings, follows, cast, makers), then shows in batches of
    100 (episodes, watches). It stores each document, publishes changed tags, and replaces every list in one
    transaction.
  - **`IShowInsight`:** a stage of the show document (seasons, top episodes, run, next premiere, shared cast,
    patterns, tags, histograms, viewing, momentum). Stages run in registration order on `ShowInsightContext`, which
    works out the shared figures once: seasons, visible ratings, `WellRated`, `ListScore`.
  - **`IShowTagRule`:** one tag. Tags appear in registration order.
  - **`IListInsight`:** a table built across shows (premieres, top episodes, discovery, actor insights). Each run
    starts a fresh `ListBuilder` that sees every finished show document.
  - **`IInsightPublication`:** a part of the document another service keeps a copy of: tags for the catalog, DNA for
    Recommendations.
    - Each run's builder compares the new document with the stored one, and the pipeline publishes what changed
      through the outbox.
    - Every show is also sent after start and daily, so lost events heal.
  - **New data:** an insight that needs data nothing loads yet adds it to `InsightSources`.
  - **Why:** it replaced a single 930-line `InsightsComputer`. The split was checked with a fixed clock, and every
    document, list row and tag event came out byte-identical.
- **Privacy rule:** a figure derived from our users needs at least 5 users or ratings behind it (`MinUsersToShow`,
  `MinRatingsToShow`). The exception is "your taste" (`TasteApi`), which uses only the caller's own data.
- **Show tags** (keys in `ShowTags`): `drops-at-once` (or "drops in parts"), `bingeable`, `weekend-binge`,
  `fan-favorite`, `limited-series`, `quick-episodes`, `long-running`, `prolific-creator` / prolific producer.
  - Tags are computed hourly and sent to the catalog as `ShowTagsChangedIntegrationEvent`, in batches of at most 500.
    They're also sent in full after start and daily.
  - Weekly release is deliberately not a tag, since nearly every show would have it.
- **Show DNA** (`DnaInsight`): 12 traits of form, rhythm and quality, each scaled to 0-1. Keys, names, labels,
  weights and typical values are in `DnaTraits` (IntegrationEvents), shared with Recommendations and the UI.
  - **Fixed anchors:** each trait is scaled between fixed anchors, about the 10th and 90th percentiles of the
    615-show catalog. Recomputed percentiles would shift as the catalog grows and need every show at once; fixed
    anchors keep a show's DNA stable and let it be computed alone.
  - **Log scales:** season size and run length use log scales.
  - **Specials:** season 0 is left out.
  - **Distinctive traits:** a trait is named, in tags or recommendation reasons, only when it's at least 0.25 from the
    typical show's (the median). Most shows are hour-long, weekly and recent, so saying so tells nothing.
  - **No kind or language:** DNA is about form, not subject, and doesn't include the show's kind or language.
    Recommendations takes those from the catalog's keywords instead.
  - **Why kind matters:** without it, Succession's closest DNA was The Kardashians.
- **Prolific:** credited behind the camera on 10 or more TVmaze shows (`ProlificMinShows`). Dick Wolf and Chuck Lorre
  are the reference cases.
- **Discovery lists:** hidden gems (rated 7.5+, outside the catalog's most popular 40%, few followers), most consistent,
  rollercoasters, best finales, bingeable, weekend binges and fan favorites.
- **Essential cut** (`EssentialCutApi`): computed on request (one show's episodes plus the caller's watches), not hourly.
  - **Per season:** it skips the lowest-rated episodes at least `Margin` standard deviations below the season
    average (and at least 0.1), up to `Share` of the season.
  - **Protected:** the premiere, a finale (only once nothing more of the season is scheduled), the season's best,
    and two-parters (a "Part 1"/"(2)" title pattern; kept together).
  - **Why per season:** cutting per season keeps the story moving through weak seasons.
  - **Why a margin:** without it, evenly rated seasons lost ~30% to noise, e.g. NCIS episodes at 7.7 against a
    7.8 average.
  - **Tuning:** One Piece's canonical filler list was the check. Smoothing ratings over neighbouring episodes
    didn't help, so it's per episode.
- **Reunion radar** (`ReunionsApi`): computed on request from the caller's own data, like "your taste".
  - **What it is:** shows where two or more actors from one show the user loves are in the main cast again.
  - **Loved:** 5 stars weighs 1 and 4 stars 0.8. Followed, or 3+ episodes watched, and unrated weighs 0.5. Rated 3 or
    lower doesn't count.
  - **Score:** love × Σ 1 / (1 + billing order / 3).
  - **Not reunions:** a show where any of them plays the same character, using Universes' matching rules (copied, since
    services don't share code). Those are spin-offs and revivals, not reunions. Also left out: shows the user already
    knows, and self roles.
  - **Why the provider's credits:** within the catalog alone (587 shows), only 73 shows had any reunion, and none of
    the demo user's favourites did. Across TVmaze, NCIS finds Chicago Hope (three of its cast) and The Wire finds Oz,
    Treme and Bosch.
- **Episode patterns:** consistency is the rating spread (steady < 0.3, rollercoaster ≥ 0.6), plus peak/decline arc,
  finales vs. the rest of their season, and each season's best episode.

## Recommendations

- **Signals:** content (the provider's similar shows, genres, keywords, cast), show DNA, and co-follows.
- **DNA's role** (`RecommendationEngine.Match`): DNA scales the content score by 0.5-1, depending on how alike the
  two shows' DNAs are. It reorders shows that are already topically similar, and can't make a show about something
  else similar.
  - **Why a multiplier:** as an additive share, it filled Ted Lasso's similar shows with dramas made the same way
    (Silo, Mayor of Kingstown).
- **Affinity:** DNA similarity clusters between 0.6 and 0.95, so it's rescaled: 0.65 → 0 and 0.95 → 1.
  - **Kind and language:** a different kind of show (keyword "scripted", "reality", …) multiplies the affinity by 0.4,
    and a different language by 0.8.
- **Reasons:** a reason names the DNA ("Same DNA as X: …") when the affinity is at least 0.6 and the two shows share
  a distinctive trait.
- **Rebuilds:** a DNA event that changes anything marks every user with signals for a rebuild of their list.

## Tonight's plan

- **Split across services:**
  - Tracking gives the queue (`TonightApi`: next unwatched aired episodes of Watching/PlanToWatch shows).
  - Catalog scores the mood (`MoodApi` → `SemanticSearch.DistancesAsync`, rate-limited like search).
  - The UI plans (`TonightPlanner`), so neither service needs the other's data.
- **Mood fits are relative:** a 0-1 fit within the user's queue (min-max of cosine distance), because absolute
  distances shift with the model and how the mood is worded. A show not yet embedded gets 0.5.
- **Planner:** a knapsack over each show's episode prefixes.
  - **Value:** minutes × (score² + 0.15), less 5 per extra show. The fill term stops a plan from leaving half the
    evening empty rather than use a weaker fit.
  - **Mix cap:** in a mix each show is capped at 75% of the budget, so a mix really mixes. The cap is dropped if the
    plan would come out under 75% full.
  - **Overrun:** allowed by max(5 min, budget/12).
- **Keyed blocks:** plan blocks (and Blend's cards) are `@key`ed by show. Without it, Blazor reused the `<img>` of a
  previous plan, and the browser showed the old poster until the new one loaded.

## Feature flags

- **OpenFeature, served by flagd:** flags are in `TvTracker.AppHost/Flags/flagd.json`, mounted into the flagd
  container, which picks up edits.
- **Keys:** in `FeatureFlags` (ServiceDefaults). TvTracker.UI links the same file, so the services and the UI gate a
  feature by the same key. Every flag is a boolean.
- **Services:** `AddFeatureFlags()` registers OpenFeature with the flagd provider (`ConnectionStrings:flagd`).
  `RequireFeature(flag)` on an endpoint answers 404 while the flag is off for the caller.
  - **Provider cache off:** in OpenFeature.Providers.Flagd 0.8.0, every cached evaluation failed with a
    NullReferenceException, and so silently returned the default. Uncached, each evaluation is one gRPC call to flagd.
- **UI:** `FeatureFlagStore` evaluates through OpenFeature's `IFeatureClient`, which each host registers.
  - **Web app:** the flagd provider.
  - **Mobile app:** the OFREP provider, through the BFF's `/features` route to flagd's OFREP port. The phone only
    reaches the BFF.
  - **Caching:** answers are kept 30 s, since the nav menu asks on every navigation.
  - **Gating:** `<Feature Flag=…>` hides links and sections. `[RequiresFeature]` on a page makes the router show
    "Not available" instead, so the page never calls an API that answers 404.
- **Context:** `FeatureFlags.ContextFor(user)` gives `targetingKey` (Keycloak `sub`) and `email`, so flags can
  target users and roll out by percentage.
- **Fail open:** the default is on. With flagd down or a flag unknown, features stay available. Turning one off is a
  deliberate edit.

## UI

- **Shared pages:** every page and component is in `TvTracker.UI`. WebApp and MobileApp supply `HostServices`
  (tokens, platform bits). API access goes through the typed clients in `Services/ApiClients.cs` on `ApiClientBase`.
- **Show page art:** `ShowBanner` uses `BackdropUrl` (TVmaze "background", large) for a full-bleed hero, else
  `BannerUrl` (TVmaze "banner", 758×140 with the logo) as a strip, else nothing. Each image falls back to the next
  on `@onerror`. That works because the web app doesn't prerender; with prerendering, an early error would be missed.
  Both images are optional parts of a sync: with `ImagesMissing`, the catalog keeps the ones it has.
- **Tags on cards:** `ShowCard` gets tags from the scoped `ShowTagStore`. It batches every card on a page into one
  `GET /api/v1/insights/tags?ids=…` call within a 50 ms window. Tags are shown only to signed-in users.
- **Blazor whitespace:** Blazor drops text nodes that are only whitespace. Put a space between inline elements inside a
  span or an expression, not bare between tags.
- **Mobile:** goes through Mobile.Bff and keeps an SQLite offline store and a queue for offline changes, which is why
  `EpisodeWatched` carries `WatchedAtUtc`.

## Persistence

- **Migrations:** Catalog, Analytics and Recommendations use EF Core migrations, applied at startup by their API.
  The other services still use `EnsureCreated`.
  - **Baselines:** a service moving off `EnsureCreated` makes its first migration from the unchanged model and passes
    its name as `AddDatabaseInitializer(baseline: "InitialCreate")`. A database `EnsureCreated` made is then recorded
    as at that migration and migrated from there, rather than refusing to start.
  - **Checked:** Recommendations' `InitialCreate` script was diffed against a database made by `EnsureCreated`
    (`pg_dump -s`); the schemas match.
- **Array columns:** EF can't translate a GroupBy over an array column's elements, so use raw SQL with `unnest`
  (see `OpsApi`).

## Known gaps

- There are no tests and no CI.
- There's no dead-letter queue.
- The MAUI app has never been compiled.
- Social.API and the weekly digest email aren't built yet.
- Development secrets are in `AppHost.cs` and the realm file.
