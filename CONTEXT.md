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
| Ratings.API | ratings, comments, reactions, community insights, flags, corrections, moderation log | moderation for every user post; accepted corrections go to the sync worker |
| Recommendations.API | picks, dismissals | |
| Social.API | follows between users, blocks, privacy settings, activity feed | copies of shows, episodes, names, each user's follows/watches/ratings and shared lists, from events and snapshots; behind the `social` flag |
| Research.API | dossiers, sources, fact candidates, saved research versions, episode notes | moderators only; saved catalog facts go to the sync worker as overrides; episode notes go to Analytics as events; behind the `research` flag |
| Assistant.API | — (questions in memory) | "Ask the app": Model Runner's chat model with the MCP tools, through the gateway; behind the `ask` flag |
| Notifications.API | inbox, devices, preferences | push/web push/email log instead of sending until configured |
| Analytics.Worker | write side of `analyticsdb` | own copy of catalog + user activity from events; hourly `InsightsPipeline`; publishes show tags |
| Analytics.API | read side of `analyticsdb` | insights, discovery lists, person insights, tags, "your taste"; applies Analytics migrations |
| Mcp.Server | — | read-only MCP tools over Catalog.API, Analytics.API and Research.API, for internal queries ("landman insights") and Ask |
| Mcp.Gateway | — | one MCP endpoint (API key, tool allow-list, call logs) in front of the MCP servers; Open WebUI's tool server |
| Mobile.Bff | — | YARP; the mobile app only talks to this (`/catalog/**`, `/tracking/**`, ..., `/features/**` for flag evaluation over OFREP) |
| WebApp | — | Blazor Server host; OIDC, tokens in Redis |

## Events

- Contracts live in `src/TvTracker.IntegrationEvents`, one file per publishing service (`CatalogEvents.cs`, ...).
  They are records deriving from `IntegrationEvent`.
- **Evolving a contract:** add new fields as optional trailing parameters with a default, so events already in queues or
  outboxes still deserialize. Don't rename or reorder existing ones.
- **Publishing:** add the event to `IIntegrationEventOutbox` in the same `SaveChanges` as the change, then
  `PublishAddedAsync`. `OutboxPublisher` retries anything left behind.
  - **Channels:** `RabbitMQEventBus` rents publishing channels from a small pool and declares the exchange once per
    connection. A channel opened, declared and closed per event added three round trips to every write; pooling took
    follow and mark-watched from p50 ~50 ms to ~30 ms at 20 load-test users.
- **Consuming:** an `IIntegrationEventHandler<T>` starts with `db.HasProcessedAsync(@event, nameof(Handler))`, and
  calls `db.MarkProcessed(...)` before saving. This is the inbox; redeliveries become no-ops.
- **RabbitMQ:** one queue per service; a failing message is retried once, then dropped (no DLQ yet). Lost-event
  healing is therefore done by periodic full syncs (tags daily), replays (catalog) or snapshots (user activity).
- **User-activity snapshots:** Tracking (`ActivitySnapshotPublisher`) and Ratings (`RatingsSnapshotPublisher`) send
  every user's follows and watches, and ratings, as one event per user, 2 minutes after start and then daily.
  - **Why:** a lost watch or follow left Analytics' copy wrong for good. One user's follow, watch and unwatch from
    20 October never arrived; the first snapshot repaired them.
  - **Consumers:** Analytics (`SnapshotHandlers`) adds what's missing and removes what's gone; Recommendations
    (`SnapshotHandler`) resets each show's signal. Anything recorded after `TakenAtUtc` is kept: Analytics compares
    watch and follow times, Recommendations each signal's `UpdatedAtUtc`.
  - **Known gap:** Analytics stores no rating times, so a rating changed between a snapshot and its delivery can be
    undone until the next day's snapshot. A user with no follows, watches or ratings left gets no snapshot.
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
- **Presumed ended:** TVmaze leaves some finished shows "Running" for years (Girls, last aired 2017; Peaky Blinders,
  2022), often with an empty, undated next season. `CatalogSynchronizer.StatusOf` calls a show the provider says is
  airing ended when nothing aired for 4 years and nothing is scheduled (a dated or TBA episode, or a dated season);
  undated empty seasons don't count. A new episode brings it back on the next refresh. It moved 9 shows; Shōgun and
  Fargo (2024) stay airing.
- **Summaries:** HTML is stripped and entities decoded until stable, since some TVmaze summaries are escaped twice.
- **Data quality** (`/ops`): what TVmaze leaves out (half of aired episodes unrated, mostly daily and reality shows;
  "Episode N" titles on anime and daily shows; duplicate numbers on same-night crossovers), and whether Analytics'
  copy matches the catalog.
- **Isolation:** one bad show never stops a loop. The import, refresh and backfill loops catch per show and retry later.
- **Search imports:** Catalog.API asks the worker (`POST /api/v1/imports/search`) to import TVmaze's top 5 matches.
  - Each term goes to TVmaze at most once every 6 h, and at most 30 new terms a minute overall.
  - It has a 10 s budget. Past that, search answers from the catalog.
- **Where to watch** (`StreamingServices` in Catalog.Data): TVmaze's web channel gives the service and its home page;
  the show's own `officialSite` is usually its page on that service (Netflix, Apple TV, Max, Prime Video, Disney+,
  Hulu, Peacock), which is the link kept, since phones open those addresses in the service's app. Otherwise
  Catalog.API offers the service's search for the title (only services whose search works signed out), else its
  home page.
  - **Show pages by path, per service:** a link is a show's page only under a known service's show-page path
    (`/title/` on Netflix, `/gp/video/detail/` on amazon.com). "Any path" was tried first, and took home pages with a
    path (`/Prime-Video/b`, `/browse`), Amazon store pages and networks' own sites for show pages. A site that isn't the
    service's is never stored under its name.
  - **Duplicates:** `TvMaze:WatchRegions` was stored twice per show, since configuration binding appends to a list
    property's default. The regions are made distinct, the sync dedupes, and migration `DedupeWatchProviders` removed
    the stored copies.
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
    - **Open WebUI** (AppHost `AddOpenWebUi`): a UI on the same Model Runner, sign-in off (development only). Its RAG
      embeds through Model Runner (`RAG_EMBEDDING_ENGINE=openai`), because by default it downloads a
      sentence-transformers model at start. The dashboard's "Pull …" command on `model-runner` is an Aspire HTTP command
      to `POST /models/create`; pulling a chat model failed in the sandbox for the same proxy-certificate reason as
      before, so chatting was not checked there.
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
  - **Crossover alerts** (`Crossovers`, `CrossoverAlerts` in Catalog.API): the watch order's detection, shared, over
    each universe's episodes from a week back to 37 days ahead; events with a part still to come and starting within
    30 days are cached for half an hour.
    - **Straight to the bus:** Catalog.API is the read side and keeps no outbox, so the hourly job publishes
      `CrossoverScheduledIntegrationEvent` directly, for each crossover starting within 14 days, and again every hour
      while it's ahead. A lost event heals on the next send; Notifications keys on `CrossoverId` (universe, name,
      year), as it does on a season verdict's id, so followers hear once.
    - **One item per person:** Notifications fans out per show with that one identity, so someone following Fire and
      Med gets one item. The text says "in 6 days" rather than a date: one text goes to every time zone, and a US
      evening is the next day in UTC.
    - **Catch-up count:** aired episodes not yet watched, only for shows the user has started; for an unstarted show
      "298 to catch up" isn't about the crossover.
    - **Tested** by retitling two upcoming One Chicago episodes as a crossover: 56 followers got one item each, and a
      second send after a restart added none.
  - **Link kinds:** "continuation" needs more than half of the later show's characters from earlier shows to come
    from that link, so a team-up (The Defenders) reads as a crossover.
  - **Caching:** the whole map is rebuilt from the database at most every 30 minutes (HybridCache).
- **Character trail** (`CharacterApi`): one actor's same-named character across shows, by Universes' `SameCharacter`
  and `IsSelfRole`.
  - **Catalog casts plus provider credits:** the actor's `PersonCastCredit` rows add shows outside the catalog (Cheers
    on Frasier Crane's trail). A credit fetched before its show was imported has no `ShowId`, so it's matched to the
    catalog by provider id.
  - **Same actor only:** a recast character can't be told from a namesake by name alone, and TVmaze's character ids
    are per show.
  - **Counts in show detail:** `CastMemberDto.TrailShows` is computed with the show (two queries for the whole cast),
    so the cast links only characters that go somewhere; the detail is cached a minute anyway.
  - **Main casts only:** guest appearances never reach it, and a character who joins later counts for the whole show.
- **Storage:** `show_embeddings` is a pgvector column with an HNSW cosine index. The Postgres image is
  `pgvector/pgvector:0.8.7-pg18-trixie`. It's trixie because existing databases were created with that collation
  version; bookworm reports a mismatch. The data volume is mounted explicitly at `/var/lib/postgresql`, because Aspire
  doesn't recognise the tag as Postgres 18.
- **Nanogenres** (`Catalog.SyncWorker/Nanogenres`): discovered, not curated (a decision: no list to maintain, and it
  finds what's actually in the catalog). `NanogenreService` reruns `NanogenreDiscovery` every 6 hours. It replaces
  the `nanogenres` table, sets `shows.Nanogenres` (slugs, GIN index, for the search filter), and sends
  `ShowNanogenresChangedIntegrationEvent` (all shows on its first run after start and daily, so lost events heal).
  - **Waits for embeddings:** it runs only once 80% of shows are embedded by the current model, else retries in 15
    minutes. Run on a fresh catalog or just after a model change, it found almost no clusters and wiped them all.
  - **Distance:** 0.4 × text-embedding distance (scaled by the model's `MaxDistance` against nomic's 0.38) + 0.6 ×
    genre/type distance. With text alone, Ted Lasso's nearest shows were Scrubs and The Lincoln Lawyer: the
    summaries are only loosely topical.
  - **Clustering:** average-linkage, by the nearest-neighbour chain (O(n²)), cut at 0.25. Groups over 30 shows are
    re-cut 0.03 tighter; groups under 3 are dropped. A show also joins any group whose members it's as close to,
    on average, as the group's median member.
  - **Naming:** the (specific genre, base) pairing covering 60% of a group: "Medical drama", "Animated family comedy".
    - **Duplicates:** a duplicate name tries a two-genre name, which needs 60% of the group to have both genres; if
      that's taken too, the two groups merge.
    - **No vague names:** groups with nothing specific in common are dropped. Animated groups with nothing specific
      are "Animated comedy" only when mostly comedies; animated action series were getting that name.
    - **No summary words:** themes taken from summary words were tried and were too noisy to show.
    - **No language model:** naming clusters with a small local model through Model Runner would read better, but
      it couldn't be pulled through the sandbox proxy, and naming shouldn't depend on one.
  - **Small pairings:** pairings of 2 to 15 shows that no cluster covers are added as nanogenres of their own
    ("genres" source).
  - **Unstable slugs:** slugs change when the catalog changes, so the UI's nanogenre page says when one has gone.
  - **Recommendations:** keeps a copy (`show_nanogenres`). Shared nanogenres add up to 0.25 to the content score, and
    the first shared one makes the reason. A bonus, not a share: most shows are in no nanogenre, and as a 25% share
    every such pair lost a quarter of its score.
  - **Flag:** the search filter is ignored while the `nanogenres` flag is off for the caller, since the UI then can't
    show or clear it.
- **Tag filter:** `tags=a,b` means every tag listed. It's stored in `shows.Tags` (text[], GIN index) and needs sign-in
  (401 otherwise); the rest of search is public.
- **Rate limits:** per user, else per client IP (the last `X-Forwarded-For` entry), else one shared bucket for services.

## Show detail cache

- **In memory only, for a minute:** Catalog.API's show detail stays out of Redis. With a Redis copy (10 minutes), the
  sync worker's `RemoveByTagAsync` didn't reach it: a corrected summary and network stayed stale in Catalog.API for over
  2 minutes after the worker invalidated the tag. In memory, a correction, research save or refresh shows within a
  minute in every Catalog.API process.

## Analytics domain

- **Copy of the world:** Analytics builds its own copy of the catalog and of users' ratings, watches and follows from
  events. Activity from before Analytics existed isn't replayed.
- **Insights pipeline** (`Analytics.Worker/Insights`): `InsightsPipeline` does the plumbing, and each insight is its
  own small class that only computes. All of them are registered in `AddInsights`, where order matters.
  - **Plumbing:** loads `InsightSources` once (catalog copy, ratings, follows, cast, makers), then shows in batches of
    100 (episodes, watches). Each batch's documents and the events they raise are saved together, then published; at
    the end, every list is replaced in one transaction.
  - **Events with their documents:** publications compare the new document with the stored one, so an event added
    after its document was saved was lost for good when the run failed in between (the next run saw no change). Now a
    stored document's events are always in the outbox, and its publisher retries any left behind.
  - **`IShowInsight`:** a stage of the show document (seasons, top episodes, run, next premiere, shared cast,
    patterns, tags, histograms, viewing, momentum). Stages run in registration order on `ShowInsightContext`, which
    works out the shared figures once: seasons, visible ratings, `WellRated`, `ListScore`.
  - **`IShowTagRule`:** one tag. Tags appear in registration order.
  - **`IListInsight`:** a table built across shows (premieres, top episodes, discovery, actor insights). Each run
    starts a fresh `ListBuilder` that sees every finished show document.
  - **`IInsightPublication`:** a part of the document another service keeps a copy of: tags for the catalog, DNA for
    Recommendations.
    - Each run's builder compares the new document with the stored one; after each batch, the pipeline adds what
      changed to the outbox in the same save as the documents.
    - Every show is also sent after start and daily, so lost events heal.
  - **New data:** an insight that needs data nothing loads yet adds it to `InsightSources`.
  - **Why:** it replaced a single 930-line `InsightsComputer`. The split was checked with a fixed clock, and every
    document, list row and tag event came out byte-identical.
- **Privacy rule:** a figure derived from our users needs at least 5 users or ratings behind it (`MinUsersToShow`,
  `MinRatingsToShow`). The exception is "your taste" (`TasteApi`), which uses only the caller's own data.
- **Show tags** (keys in `ShowTags`): `drops-at-once` (or "drops in parts"), `bingeable`, `weekend-binge`,
  `fan-favorite`, `limited-series`, `quick-episodes`, `long-running`, `prolific-creator` / prolific producer.
  - Tags are computed hourly and sent to the catalog as `ShowTagsChangedIntegrationEvent`, one per batch of 100 shows.
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
- **Forecasts** (`Insights/Forecasts/ForecastModels.cs`, `OutlookInsight`): next season's rating, whether a show comes
  back, and when.
  - **Training:** the models train each run on `InsightSources.SeasonHistory`, every show's regular seasons summarised
    by one SQL query.
  - **Backtesting:** each model replays the past. For every past season, it asks what it would have said, compares
    that with what happened, and keeps the misses as its 80% range (10th to 90th percentile). The run logs the
    results ("Forecast backtests: …").
  - **Rating:** 0.7 × last complete season + 0.3 × show average.
    - **No trend:** the trend's direction was right for only 45% of next seasons, worse than a coin.
    - **Mean reversion:** seasons revert towards the show's average (61% of the time when the last one was unusual).
    - **Ranges:** separate for steady, varied and volatile shows, since the error roughly doubles from one end to
      the other.
  - **Renewal:** the share of comparable seasons that were followed by another.
    - **Comparable:** the same half-point rating band, point in the run and era. Groups need 30 seasons, and back off
      by dropping the era, then the point in the run.
    - **Bias:** the catalog is survivor-heavy (92% of seasons came back).
    - **Accuracy:** in a 5-fold backtest by show, the groups beat that base rate only a little (Brier 0.071 against
      0.074), so the UI calls it a weak signal.
  - **Timing:** the median of the last three waits beat the last wait alone (median miss 22 against 28 days). Errors
    are kept as ratios of (wait + 30 days), since they grow with the wait. It needs two past waits.
  - **When there's no forecast:** none for ended shows. Renewal and timing are only forecast between seasons with
    nothing scheduled or announced; a scheduled season is a fact (`NextPremiere`).
- **Will you finish it?** (`FinishOutlookApi`): computed on request from the caller's own watches.
  - **No probability:** per-person history is too thin for an honest one.
  - **What it gives:** the facts that decide it: pace, projected finish, on-track/slowing/stalled (14 and 30 days),
    the user's record from as far in, and the show's drop-off point when it's ahead of them.
- **Previously on…** (`RefresherApi`): a rewatch for coming back after a break, computed on request from the caller's
  own watches.
  - **Due:** 120+ days since the last watch, with aired episodes ahead or a new episode or season scheduled. Up Next's
    list looks only at followed shows.
  - **Only watched episodes:** picking from what the user has seen is what makes it spoiler-proof, so titles show.
    (Recaps from TVmaze's summaries were the other idea; summaries of past episodes would have needed trimming.)
  - **Picks, in order:** where they left off (always in), that season's premiere, the episode before, the season's top
    two, last season's finale and top two, then the rest by rating. Taken greedily while they fit the budget plus
    Tonight's overrun (max(5 min, budget/12)). Two-parters use the essential cut's pattern.
  - **Ratings:** the user's own stars × 2 outrank the provider's; a refresher is about what they remember.
- **Catch-up planner** (`CatchUpApi`): for a followed show with a season premiere (a scheduled episode 1, or an
  announced season) within 120 days, the aired episodes after the furthest watched, spread over the weeks left.
  - **Pace:** the user's minutes over the last 8 weeks, across all shows, with each day capped at 4 hours. Bulk marks
    ("watched up to S05E10") share one timestamp and land on one day; uncapped, seeding three shows read as 15 h a week.
  - **Verdicts** compare the minutes a week needed with that pace: ≤ 0.5× comfortable, ≤ 1× doable, ≤ 1.5× tight.
  - **Cut path:** `EssentialCutApi.Decide` at "lean", offered when it saves 10%+. The cut's loading, runtimes and
    decisions were split out of its endpoint for this; its responses for seven shows at all three levels came out
    byte-identical.
  - **Weeks:** split by cumulative running time, an episode going to the week where its midpoint falls.
- **Is it worth going back?** (`ComebackApi`): for a dropped show (episodes left, nothing watched for
  `StoppedAfterDays`), what's left against the last 10 rated episodes watched.
  - **Why that stretch:** it's likely why they stopped; against the whole show, a strong opening hides a slump.
  - **Order:** a slow start's turn still ahead wins ("gets good"), then the first later season (or the rest of the
    current one) 0.3+ higher ("better"), then all of what's left 0.3+ lower ("worse"), else "same". Parts need 3 rated
    episodes. Ratings are the essential cut's.
  - **TVmaze is flat on some shows:** Breaking Bad's seasons all rate 8.1-8.3, so it's honestly "more of the same".
- **Show value** (`ShowValue`, shared by the backlog and time off): the provider's rating + 0.6 × (share of the show's
  genres the user likes − 0.5) + an ending adjustment (sticks/great finale +0.3, weaker last season −0.1, finale lets
  down −0.3, fizzles −0.5). On the rating's scale, so thresholds read as ratings (let go under 7, time off from 7.3).
  - **Liked genres:** of shows rated 4+ or 80% watched; with four or more such shows a genre needs two.
  - **Simple on purpose:** every part is shown as a reason. Recommendations' scores live in another service and are
    relative to the user's list, so they aren't reused.
- **Backlog triage** (`BacklogApi`): followed shows with nothing watched. Analytics has no copy of Tracking's watch
  status (`ShowFollowed` carries none), and "plan to watch" mostly means exactly that.
- **Fits your time off** (`TimeOffApi`): ended, unstarted shows within the budget plus Tonight's overrun. The mix is
  the best pair or triple of the top 30 by Σ minutes × (value − 6.5), shown only when it fills 60%+ and more than the
  best single show.
- **Prolific:** credited behind the camera on 10 or more TVmaze shows (`ProlificMinShows`). Dick Wolf and Chuck Lorre
  are the reference cases.
- **Discovery lists:** hidden gems (rated 7.5+, outside the catalog's most popular 40%, few followers), most consistent,
  rollercoasters, best finales, bingeable, weekend binges, fan favorites and worth the wait.
  - **Worth the wait** is ordered by popularity: ordered by the lift, thinly rated old shows with a few 9.5s led it.
- **When it gets good** (`GetsGoodInsight`): the episode where a slow start turns, from the provider's episode ratings.
  - **Bar:** the median of the opening run (first 40 rated episodes), not the whole show. Long-running shows' ratings
    drift up over the years, which put Friends' turn in season 2 and Last Week Tonight's in season 5.
  - **Rules:** a slow start opens (first 3) 0.3+ below the bar; good from the start within 0.1; in between, nothing is
    said. The turn is the first episode where the next 3 average the bar and the next 10 stay within 0.05, in the first
    25 episodes, with a lift of 0.3+ over the episodes before.
  - **Checked** against what viewers say: The Office S02E03, Succession S02E01, The Wire S01E07, Community and The Good
    Place S01E07, Severance S01E06.
  - **Personal:** `FinishOutlookApi` adds `GetsGoodAhead` when the turn is past the user's furthest episode.
- **Creator fingerprint** (`CreatorFingerprintList`, table `creator_fingerprints`): each trait of DNA averaged over the
  shows a person created or developed (two or more with DNA).
  - **Executive producers are left out:** their credits follow a production company, not a style.
  - **Signature:** a trait that's distinctive on average and that two-thirds of their shows lean towards. Range: traits
    where their shows sit at both ends.
  - **Made this way:** other people's shows with similarity ≥ 0.85 that share two signature traits (or the only one).
    Only shows with a creator or developer credit count: TVmaze rarely credits one on reality, talk and news shows,
    and Analytics has no kind of show to tell them apart otherwise.
  - **Kindred creators:** fingerprint similarity ≥ 0.85 and a shared signature trait, never having made a show together.
- **Does it stick the landing?** (`LandingInsight`): ended shows only (TVmaze has no "cancelled", so a cut-short show
  can't be told from a planned ending, and nothing is said about it).
  - **Finale:** the last aired regular episode against the median episode: 0.3 above is strong, 0.3 below weak.
  - **Final season:** against the median season (`RatedSeasons`: 3+ rated episodes), 0.3 below is weak, even with two
    seasons; ranked only among 3+. Differences are rounded first, since 8.1 − 7.8 is 0.2999… in floating point.
  - **Verdicts:** strong finale → sticks (or great finale, weaker last season); weak finale → fizzles (weak season too)
    or the finale lets it down; in-line finale → weaker last season, or ends as it ran.
  - **No unchecked claims:** every verdict says how the final season did, so a show whose final season has too few
    rated episodes to count gets no verdict at all (and never reaches Great endings).
  - **Ties:** the finale's and the final season's places are stored as counts (rated higher, lower), not a share or a
    rank alone, so a tie reads as one ("ties for its best") rather than "higher than any other episode".
  - **Spoiler-free:** no titles, in the panel or the Great endings list.
- **TV time machine** (`TimeMachineApi`): computed on request from the catalog copy's air dates and the provider's
  ratings; nothing stored.
  - **Weeks in the user's calendar:** the client sends the profile time zone (`tz`), and a week is 7 local days, so a
    New York premiere at 20:00 counts on its own day, not the next UTC one.
  - **Every year:** the same day in each past year, its week taken whole: 29 December to 4 January near New Year, and
    always at least a year back. (Matching month and day with "an earlier year" pulled in last week's episodes from
    late December as "TV history".)
  - **Cost:** each week is a range scan of `IX_episodes_AirsAtUtc` (76 for every year, ~3 ms), and the result is cached
    in memory for the day per time zone; only the Watched marks are looked up per caller.
  - **Premiere / finale:** a show's first / last regular episode by air time; a finale only for ended shows. Episodes
    that aired together (a streaming drop) resolve to the lowest / highest episode number.
  - **Best episodes:** episodes rated more than 1.5 above their show are left out, since thinly rated shows get a few
    fans' 10s.
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
  - **Episode notes first** (Research's, copied into `episode_notes`): an episode marked essential is protected, and a
    season with any episode marked skippable is cut by its notes alone, at every level. A moderator who marked a
    season's filler has said what to skip; ratings adding more would contradict them. A show with notes is cut even
    when too few episodes are rated (anime filler is often unrated). Backlog and catch-up share `Decide`, so they
    follow the notes too.
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
- **Where do I know them from?** (`KnownFacesApi`): computed on request from the caller's watches and ratings and the
  catalog copy's main casts (`cast_credits`), so no minimum counts.
  - **Seen:** a watched episode or a show rating; a follow alone says nothing about having watched.
  - **Catalog casts, not provider credits:** the user can only have watched catalog shows, so `CastCopy` is enough,
    and it also covers actors whose provider credits haven't been fetched yet.
  - **Spoilers:** casts are today's regulars with no seasons per role (Bob Odenkirk joins Breaking Bad in season 2), so
    a line from a show the user hasn't caught up on (latest aired regular episode watched) is flagged rather than
    dropped. Most such actors are there from the start, and the UI lets the spoiler shield hide the line.
- **Episode patterns:** consistency is the rating spread (steady < 0.3, rollercoaster ≥ 0.6), plus peak/decline arc,
  finales vs. the rest of their season, and each season's best episode.

## Recommendations

- **Signals:** content (the provider's similar shows, genres, keywords, cast, and shared nanogenres), show DNA, and
  co-follows.
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
- **DNA twins** (`GetDnaTwinsAsync`): the reverse of DNA's role in ranking. Shows made alike (similarity ≥ 0.85, two or
  more shared distinctive traits, same kind) but about something else: genre overlap ≤ 0.34 with a genre of its own,
  no shared nanogenre, and not on either's provider similar-shows list. Those would be "Shows like this".
  - **Why distinctive traits:** similarity alone pairs every hour-long weekly drama with every other one.
  - **Computed on request** from all DNA rows (a few hundred), cached 30 minutes like similar shows.

## Community and moderation

- **One owner:** Ratings.API holds everything viewers write (comments, community insights, flags, suggested
  corrections) and the moderation that goes with it, so one service decides what's visible.
- **Hidden, not deleted:** a post gets a `HiddenReason`: `flags`, `held` or `moderator`. Only its author sees it then,
  with why, so nobody is silently shadow-banned and a restore loses nothing.
- **Flags** (`ContentFlag`, one per person per post): 3 open flags hide a post until a moderator looks; 2 spoiler flags
  mark it a spoiler, which is usually all it needs. A moderator's restore or remove settles (resolves) the flags, so a
  restored post needs 3 fresh ones to be hidden again.
- **Light layer** (`Moderation`): blocked terms (`Moderation:BlockedTerms`, whole words) are refused; a link holds a
  post for review, spam's usual shape; 20 posts and 30 flags per person an hour. Moderators are `Admin:Users` plus
  `Moderation:Users`; anyone else gets 404 from the moderation API, which the UI uses to know whether to link it.
- **Community insights** are ranked by helpful votes counted at read time, not a stored counter, so deleting a user's
  votes needs no recount.
- **Corrections are applied by the sync worker:** an accepted one is sent to `POST /api/v1/overrides` (internal, like
  the import API), stored as `CatalogOverride` (`catalog_overrides`, id = the correction's) and applied straight away.
  - **Surviving refreshes:** `SyncShowAsync` patches the provider's show with the overrides before comparing, so a
    refresh neither undoes one nor sees it as a change, and the usual events carry the corrected values.
  - **Fields:** show summary; episode title, summary, air date (the date only; the provider's time of day is kept)
    and runtime. "Other" is recorded, not applied. Both services check values, Ratings so the suggester hears why.
  - **Reverting** deletes the override and makes the show due for refresh, which brings the provider's value back.
  - **Synchronous on purpose:** the moderator sees at once if the catalog refused the value or the show is gone.
- **Ratings' migrations:** Ratings moved off `EnsureCreated` for these tables. Its `InitialCreate` baseline was applied
  to a scratch database and diffed (`pg_dump -s`) against the live one `EnsureCreated` made: identical apart from EF's
  history table.

## Research

- **What it is** (Research.API): moderators gather sources for a show, pick each fact, edit a model-drafted insight and
  save a version. The latest saved version is the show's trusted record: the show page and the MCP tools read it.
- **Field by field** (a product decision): a saved summary, network or premiere date becomes a `CatalogOverride`, the
  same path accepted corrections take, so the sync worker applies it on every refresh. Other facts (creators, awards,
  IMDb link, …) live only in Research.
  - **One override per show and field:** its id is a hash of the show and field (a v8 GUID), so a new version replaces
    the value in place (`AddOverrideAsync` upserts) rather than stacking overrides.
  - **Overrides first:** saving applies them before writing the version, and saves nothing if the catalog refuses one,
    so the record never says what the catalog doesn't. A catalog field dropped in a later version removes its
    override, and TVmaze's value comes back on the next refresh.
  - **New override fields:** `show-network` and `show-premiered` (catalog `CatalogOverrideFields`), added for this.
- **Sources:**
  - **Wikidata by TVmaze id:** a SPARQL query on property P8600 finds the item without guessing from the title, and
    brings its English Wikipedia article. Wikidata's entity API and the MediaWiki API answered 429 from the sandbox;
    the query service and the REST summary didn't.
  - **Awards** are grouped by name ("Primetime Emmy Award × 6"; Breaking Bad has 30 categories): Wikidata lists one item per category.
  - **TMDB** only with `Research:TmdbApiKey`.
  - **Pages by address** (`WebPageSource`): fetched on a moderator's behalf, so only public addresses, checked again
    at each redirect.
  - **Plain HTTP clients:** the standard resilience handler retried Wikipedia's 429s until its 30 s total timeout and
    failed the whole gather. Each source has one try, and a failure leaves that source out.
- **Drafts in the background** (`DraftQueue`): Model Runner's small CPU model (qwen3 0.6B) took 92 s for Breaking Bad,
  past the UI's 30 s request timeout. The draft is queued, the page polls, and a draft running over 15 minutes is
  treated as failed. It reads only the included sources, 1,500 characters each, and cites them as [n].
- **Numbering:** sources are numbered by kind (Wikidata, Wikipedia, TMDB, pages) then time, and a version stores
  them as numbered, so its citations keep pointing at the right source.
- **Deleted moderators:** their name comes off the versions they saved and the notes they wrote ("a former
  moderator"); the research stays.
- **Episode notes** (`EpisodeNotesApi`): a moderator marks an episode essential or skippable, with a reason viewers see
  and a source.
  - **Apply at once, outside versions:** a note is a judgement about one episode, not part of the show's record, and
    waiting for the next saved version would hold back a season's worth of filler marks.
  - **Whole lists:** each change sends the show's notes as they are after it (`EpisodeNotesChangedIntegrationEvent`),
    through the outbox in the same save; Research got an outbox for this. `EpisodeNotesPublisher` sends every show's
    notes in one event after start and daily (notes are hand-written, so few), and Analytics replaces its copy from
    it, so a lost event heals. A copy read later than an event (`TakenAtUtc`) is kept.
  - **Validated against the catalog:** the episode must be the show's (Catalog.API's `/episodes/{id}`).

## Social

- **Following, not friending** (a product decision): one way, no approval, like Letterboxd. Privacy is the followed
  user's "share my activity" setting, on by default; off hides all of their items at once, past ones too, because the
  feed checks it at read time rather than copying items to followers.
- **Read-time feed:** items are stored once per actor (`activities`) and the feed is a query over the people you follow.
  That keeps unsharing, unfollowing and deletion instant; a fan-out copy per follower would need them propagated.
- **Items:** watched (episodes of one show within 6 hours merge, so a binge is one line), finished, followed a show,
  rated a show (a re-rate within a day replaces it). Undo events take items back out.
  - **Follow with a watch:** watching an unfollowed show follows it too, so a follow within the watch's 6 hours is
    dropped, in either order of arrival.
  - **No episode titles:** only numbers, since a follower may not have seen the episode. The copy keeps no titles.
- **Copies:** shows and episode numbers from catalog events (replay requested on first start, as Analytics does);
  display names from `UserRegistered` / `UserProfileUpdated` and Identity's daily `UserProfileSnapshot`, which exists
  because Social started after most users registered. Until one arrives, the token's `name` claim stands in.
- **History:** activity from before Social existed isn't replayed; the feed fills from then on.
- **Visibility** (`Visibility`, one rule for every endpoint): a block, either way, hides the two from each other
  everywhere, and 404s rather than saying so; a private profile shows only its name to anyone but approved followers;
  activity also needs "share my activity". Lists and connections need the profile rule, activity, the show page and
  watching together the activity rule.
- **Private profiles:** a follow is a request (`Approved = false`) until approved. Going public approves every waiting
  request, since nothing is left to approve. Blocking deletes follows and requests both ways.
- **Search:** by display name, for anyone signed in, private profiles included (by name only); blocked people aren't found.
- **User state** (`user_shows`, `user_watches`): each user's follows, ratings and watched episodes, for the show page
  ("people you follow") and watching together. Kept from Tracking's and Ratings' events by `UserStateHandlers`, and
  filled and repaired by their daily snapshots, so unlike the feed it includes history from before Social.
  - **Progress:** regular episodes watched against those aired (season 0 left out, as Tracking does), and the furthest
    one; "finished" is every aired one. Episode air times come with catalog events; the replay is requested again when
    the copy has none (it predated them).
- **Shared lists:** Tracking owns lists and sends a shared one's whole state (`ListSharedIntegrationEvent`) through the
  outbox on every change, and `ListUnsharedIntegrationEvent` when it's unshared or deleted. Social keeps a copy and
  serves it under the profile rule. Updates carry `UpdatedAtUtc`, and an older one is ignored, since events can arrive
  out of order. Tracking's snapshot carries shared lists too (an optional trailing parameter), so a lost event heals.
- **Tracking's migrations:** Tracking moved off `EnsureCreated` for the list `Shared` column. Its `InitialCreate` baseline
  was applied to a scratch database and diffed (`pg_dump -s`) against a database `EnsureCreated` made: identical.

## Season verdict

- **Part of the show document** (`SeasonVerdictInsight`), so it's computed hourly with the rest, and read by the show
  page as it is; Insights' list reads the stored documents' `seasonVerdict` (jsonb) rather than a table of its own,
  cached 5 minutes, since reading every document took tens of milliseconds per visit.
- **Released at once:** one UTC date for every episode (`ShowInsightContext.ReleaseDays`), the same test the
  drops-at-once tag uses.
- **When a season is over:** "every listed episode aired" isn't enough, since TVmaze lists a weekly show's next episode
  only days ahead (Cops in week 17 looked finished). Over means: the show ended, a later season is listed or announced,
  the season dropped at once, or it's been quiet for twice its usual gap (at least 7 days). The catalog doesn't keep
  TVmaze's planned episode count per season, which would settle it; adding it means a provider, catalog, event and
  Analytics change. A reality show back from a long break can still be called over early.
- **Window:** 3 days after the finale (the provider's episode ratings arrive after airing, and viewers catch up) to
  45 days. Daily shows (seasons over 60 episodes) and seasons with fewer than 3 rated episodes get none.
- **Verdicts:** against the median of the show's other seasons that count (3+ rated episodes): 0.3 above or below, else
  in line; "best yet" / "weakest yet" when above or below every other, with two or more to compare. With none to
  compare, "first season", or "unranked" for a later season whose earlier ones have too few rated episodes (reality and
  older shows). The finale against the season's average, with the same 0.3.
- **Shared keys:** `SeasonVerdicts` and `FinaleVerdicts` are in IntegrationEvents (linked into the UI, like
  `DnaTraits`), so Notifications and the UI switch on the same constants as Analytics.
- **Viewers:** "finished" is who watched the finale, out of who watched any of the season, shown with 5 or more who
  started (the privacy rule).
- **Sent once** (`SeasonVerdictPublication`): when a show's document has a verdict for a season its stored document
  didn't, and the verdict is within 2 days of when it could first be given (`ReadyAtUtc`); the event is saved with the
  document (see the insights pipeline), so a failed run can't lose it.
  - **`VerdictId`** (from the show and season): a verdict that drops out of the document for a run and comes back is
    sent again; Notifications uses the id as the event's identity (inbox check and inbox items), so followers hear once.
  - **Why 2 days:** a first run over an empty store, or after a long outage, would otherwise notify followers about
    every season of the last six weeks.
  - **History:** it was briefly sent on every run for a week (then two days), because the pipeline saved documents
    before their events, so a run that failed in between lost the verdict for good. The pipeline now saves them together.
- **Notifications:** under the "new seasons" preference; a preference of its own would need a column, and
  Notifications still uses `EnsureCreated`. The flag is checked once per event, without a user, so it can't target
  some followers only.

## Holiday episodes

- **Found, not curated** (`HolidayApi` on Catalog.API): a Postgres regex per holiday over episode titles and summaries,
  with no list to keep up. A title match counts whenever it aired (Ghosts' "Halloween 3" aired in March); a summary
  match only within the holiday's window (Halloween: 10 October to 7 November), since out of season summaries mostly
  mention it in passing.
- **Left out:** daily shows (a season of more than 60 episodes), whose dated episodes mention every holiday; shows named
  after the holiday, listed whole as "made for the season".
- **Ranking:** TVmaze's popularity tops out (a fifth of the catalog at 99 or 100), so the show's best rated holiday
  episode breaks ties.
- **Stands alone:** a reality, animation, game, variety, documentary or talk show, or a comedy without a story genre
  (drama, crime, thriller, mystery, science fiction, fantasy). A rough rule, shown as "usually".
- **Personal part in the UI:** the page sends the user's follows to the catalog and places each episode with one
  Tracking `/progress` call per matching show, as the watch order does; the catalog never sees watches.
- **Cached** per holiday for 6 hours; a scan of ~90,000 episodes takes under a second.

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
- **For two** (`TonightTogetherApi` on Social): the queue comes from Social's copy of both people's watches, since
  Tracking only knows the caller's and Social already applies `Visibility`. The same `TonightPlanner` plans it.
  - **Start:** after the behind person's furthest episode; episodes seen by both are skipped.
  - **Rewatches:** an episode one has seen is worth 0.6 of a new one in the planner, so a level show wins.
  - **Gap:** over 3 episodes apart, the show is listed apart rather than planned (an evening of reruns for one).
  - **Runtimes:** Social's episode copy keeps `RuntimeMinutes` for this (migration `EpisodeRuntimes`); the replay is
    requested again when no episode has one. Titles stay out of Social, so the plan shows episode numbers.
  - **Marking:** marks only the caller's episodes, and takes the plan out of the queue locally, since Social hears
    of the watches a moment later through events.
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
- **Flags page** (`FlagsApi` on Identity.API, `/settings/flags`): admins (`Admin:Users`, Keycloak usernames) turn a
  flag on or off for everyone.
  - **Overrides file:** changes go to `Flags/overrides.json`, a second flagd source (`--uri`); for a key in both,
    flagd uses the later source. flagd.json stays the checked-in baseline, and removing an override restores it,
    targeting included. The AppHost creates the file when missing; it's git-ignored.
  - **Written in place:** a rename over the file would replace the file flagd watches.
  - **flagd forgets dropped overrides:** in v0.17, removing a key from overrides.json removed the flag altogether
    (FLAG_NOT_FOUND) instead of falling back to flagd.json. Removing one therefore rewrites flagd.json with its own
    bytes, which makes flagd re-read it; overrides still win, and git sees no change. The rewrite waits 5 seconds:
    flagd polls the files every 3, and when both changed in one poll it read flagd.json first and dropped the flag anyway.
  - **404 for non-admins,** so the UI asks the list endpoint to know whether to show the link.
  - **Admin account:** `admin` in the app's realm (not Keycloak's master-realm console admin), so trying the app as
    an ordinary user (demo) and as an admin are separate sign-ins. Keycloak imports the realm only on its first start,
    so `AdminAccountSeeder` creates the account through the admin API when an older realm lacks it.
  - **No flag of its own:** a flag on the flags page could lock admins out of it.
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
- **Show insights layout:** `InsightHighlights` leads with up to six findings (gets good, the ending, next season,
  coming back, consistency, the arc, finales) as cards toned good/warn/bad/info (`--good`, `--warn`, `--bad`, `--info`,
  dark enough to read on their own 10% tint). Ratings are toned by `Format.RatingTone` (8.5+ great, 8+ good, 7+ ok).
  The season chart marks best and weakest only among 3+ seasons, and over 12 seasons squeezes to fit (values on the
  marked seasons only, labels every fifth).
- **Spoiler shield:** `SpoilerShield` (scoped) reads a device preference through `IPlatformServices` (localStorage
  on the web, MAUI `Preferences` on the phone). `<Spoiler Hidden=…>` swaps hidden content for a "Title hidden" button.
  Per device by design, and it needed no column in Identity, which still uses `EnsureCreated`.
- **White label:** the app's name is `Branding:AppName`, set once in the AppHost and passed as `Branding__AppName`.
  - **Two `Branding` types:** services use ServiceDefaults' (`TvTracker.Branding`, registered by
    `AddServiceDefaults`); the UI has its own (`TvTracker.UI.Services.Branding`), since the phone doesn't reference
    ServiceDefaults. The web app passes its configured name to `AddTvTrackerUi`, the phone `MobileSettings.AppName`.
  - **In pages:** `_Imports.razor` injects it as `Brand`. Static text that needs it (Discover's list descriptions,
    ops.html) says `{app}` and is filled in when shown.
  - **Stored text:** Analytics' fan-favorite tag reason includes the name, so a rename shows there after the next
    hourly run.
- **Theme:** the header's `ThemeToggle` sets the opposite of what's showing (`tvtrackerTheme.effective()` reads the
  chosen theme, else the device's); Settings can go back to the device's. Every colour token in `app.css` is `light-dark(light, dark)`, and `color-scheme` picks the side: the
  device's, or `data-theme` on `<html>` from Settings (`ThemeStore`, a device preference like the spoiler shield).
  `theme.js` loads in the head, before the stylesheet, and applies a saved choice from `localStorage` before first
  paint. The phone keeps its choice in MAUI `Preferences`, which the page can't read that early, so `MainLayout`
  also applies it after the first render, and `theme.js` mirrors it into `localStorage` for the next start.
- **Blazor whitespace:** Blazor drops text nodes that are only whitespace. Put a space between inline elements inside a
  span or an expression, not bare between tags.
- **Mobile:** goes through Mobile.Bff and keeps an SQLite offline store and a queue for offline changes, which is why
  `EpisodeWatched` carries `WatchedAtUtc`.

## Persistence

- **Migrations:** Catalog, Analytics, Ratings, Recommendations, Research, Social and Tracking use EF Core migrations, applied at startup by
  their API.
  The other services still use `EnsureCreated`.
  - **Baselines:** a service moving off `EnsureCreated` makes its first migration from the unchanged model and passes
    its name as `AddDatabaseInitializer(baseline: "InitialCreate")`. A database `EnsureCreated` made is then recorded
    as at that migration and migrated from there, rather than refusing to start.
  - **Checked:** Ratings', Recommendations' and Tracking's `InitialCreate` scripts were diffed against databases made by
    `EnsureCreated` (`pg_dump -s`); the schemas match.
- **Connection budget** (`ConnectionPool` in TvTracker.Persistence): every service's Npgsql pool is capped at 8, with
  idle connections returned after 60 s, because all the databases share one server's `max_connections` (100).
  - **Why:** with Npgsql's defaults (100 per pool, idle kept 5 minutes), services that had been busy held their
    connections, and under 80 load-test users Postgres refused ~6,000 new ones in 5 minutes ("too many clients").
    Npgsql calls that transient, so EF's retry strategy backed off for up to 30 s: Tracking, which needed new
    connections last, answered in 2-30 s, reads as well as writes. Capped, the same load ran at 310 requests/s instead
    of 85, with no errors and p95 0.8 s instead of 3.9 s, and no database held more than 12 connections.
  - **The rule:** keep the cap × the processes with a database (11 here, so 88 of 100; more with replicas) under the server's
    `max_connections`, or put a pooler (Azure's built-in PgBouncer) in front once replicas multiply.
  - **Not a server setting:** raising `max_connections` with `WithArgs` on the persistent Postgres container didn't
    apply to an existing container, so the fix lives in the services.
- **Array columns:** EF can't translate a GroupBy over an array column's elements, so use raw SQL with `unnest`
  (see `OpsApi`).

## MCP server

- **Over the APIs, not the databases:** `Mcp.Server` calls Catalog.API, Analytics.API and Research.API like any other client, so
  the one-database-per-service rule holds and the tools see exactly what the app shows.
- **Its own identity:** Analytics needs a token for everything, so the server signs in as the confidential client
  `tvtracker-mcp` (client credentials, with the `tvtracker-api` audience mapper). The token has no user, which suits
  it: the endpoints it calls are about shows and people, never one viewer.
  - **Older realms:** Keycloak imports the realm file only on first start, so in development `ServiceToken` creates
    the client through the master realm's admin API when Keycloak says it doesn't exist (the same reason
    `AdminAccountSeeder` exists).
- **Pass-through answers:** tools return the services' JSON less image URLs, rather than new models, so a new field in
  Analytics' show document reaches the tools without a change here.
- **Stateless HTTP transport:** no MCP sessions, so the server can restart without clients reconnecting.
- **Access:** `/mcp` is mapped only in Development unless `Mcp:ApiKey` is set; it reads Analytics with its own token,
  so it mustn't be open by default.
- **Gateway** (`Mcp.Gateway`): one endpoint for clients, so adding a server or a key is done once, there.
  - **Not Docker's MCP Gateway:** `docker/mcp-gateway` needs the host's Docker socket mounted even for remote
    servers (it inspects networks at start), which gives the container control of the host's Docker. This one only
    forwards to HTTP servers, with the same SDK as the server.
  - **Forwarding:** tools are listed from every upstream (cached a minute, re-listed when a call names an unknown
    tool) and calls are forwarded as they came. A clash of names gets `{server}_` prefixes; a server that's down is
    left out rather than failing the list.
- **Open WebUI** gets the gateway as an MCP tool server in `TOOL_SERVER_CONNECTIONS`, at `aspire.dev.internal`, the
  host name Aspire gives containers for reaching projects.
  - **Not persisted:** `ENABLE_PERSISTENT_CONFIG=False`, since Open WebUI otherwise keeps the first value of each
    setting in its database and ignores the environment afterwards, so a changed URL or key would silently not apply.
    The AppHost sets all of its configuration anyway; changes made in its admin settings last until a restart.
  - **Braces:** `ReferenceExpression` turns `}}` in its literal text into `}`, so the JSON keeps its closing braces apart.

## Ask the app

- **Tools, not retrieval** (Assistant.API, `AnswerLoop`): the chat model on Model Runner is offered the gateway's MCP
  tools and answers from what they return. The tools already answer every question the app can, with the privacy rule
  applied, so there's no index or copy of data to keep up.
- **Through the gateway,** with its key, like Open WebUI: one tool list, and the gateway logs every call.
- **Background queue** (`AskQueue`): one question at a time, since the model on a CPU takes 10 s to 2 minutes; the page
  polls. One waiting question per person and 20 an hour. Kept in memory for 2 hours, never stored.
- **Compact results** (`Toolbox`): nulls, empty values, show/episode ids and URLs go before the model reads a result
  (10,000 characters of JSON became ~5,000, the cut-off). Show links are collected from the full result first.
- **What qwen3 0.6B got wrong, and the guards** (a larger model may need fewer):
  - **Answering without looking:** a follow-up was answered from the conversation with a guess, and after a refused
    call it said "let me check that" and stopped. A reply without a tool call, while no lookup has worked, is sent
    back with "look it up first", twice at most. Model Runner ignores `tool_choice: "required"`.
  - **Made-up ids:** asked who created Severance, it called `person_insights` with "12345678-9012-…". A call with an
    id no tool has returned isn't run; the model is told to look the show up first.
  - **Missing arguments:** `discover_list` without its list got "An error occurred invoking 'discover_list'" from the
    server. Required arguments are checked against the tool's schema, and the reply names what's missing.
  - **Wrong values:** it asked `discover_list` for the list "drama" (hidden gems within drama). The tools' fixed
    values are schema enums now (`[AllowedValues]` on the Mcp.Server parameters: discover lists, premiere sorts,
    holidays), and a value outside one isn't run; the reply lists the allowed ones.
  - **Copied examples:** asked for "best comedy finales", it looked up Landman, the example in show_insights'
    description. Tool descriptions no longer name a show.
  - **Misreading results:** it called Severance's cast its creators and read "as-it-ran" as a great finale. Two
    reading hints in the system prompt didn't fix it, so the page says it can be wrong and lists what it looked up.
- **Default chat model:** qwen3 0.6B (was smollm2, which doesn't call tools reliably). Larger models couldn't be pulled
  in the sandbox (proxy certificate), so none were compared.
- **Links:** only shows a tool returned, matched as whole words in the answer, so a show called "Lost" doesn't link
  part of "lostness".

## Load testing

- **Through the BFF, as users:** `tvtracker-load` signs real (load-test) users in and calls the Mobile BFF, so the
  gateway, token validation and per-user rate limits are part of what's measured. Hitting services directly would skip
  all three.
- **Password grant, for this only:** the realm's clients don't allow it, so `setup` creates a separate public client
  (`tvtracker-loadgen`) through Keycloak's admin API, with the same audience mapper as the app's clients. Development
  only.
- **Pages, not endpoints:** a virtual user visits weighted pages, each a burst of the app's requests in parallel, so
  the mix (and fan-out to services) is roughly a real session's.
- **Cleanup through account deletion:** deleting Keycloak users alone would leave their follows, ratings and so on in
  every service; Identity.API's account deletion removes them everywhere.

## Known gaps

- There are no tests and no CI.
- There's no dead-letter queue.
- The MAUI app has never been compiled.
- The weekly digest email isn't built yet.
- Development secrets are in `AppHost.cs` and the realm file.
