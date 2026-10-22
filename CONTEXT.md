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
| Analytics.Worker | write side of `analyticsdb` | own copy of catalog + user activity from events; hourly `InsightsComputer`; publishes show tags |
| Analytics.API | read side of `analyticsdb` | insights, discovery lists, person insights, tags, "your taste"; applies Analytics migrations |
| Mobile.Bff | — | YARP; the mobile app only talks to this (`/catalog/**`, `/tracking/**`, ...) |
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
  character, grouped into connected components.
  - **Why names:** TVmaze's character ids are per show; not one of 6,514 credits shared an id across shows. So
    `CharacterExternalIds` can't find crossovers, and Analytics' `SharedCast.SameCharacter` (which compares ids) is
    effectively never true.
  - **Matching:** names are split on "/" into aliases, and quoted nicknames, parentheses, titles and punctuation are
    dropped. A match is equal word sets, or one set inside the other with at least two words.
  - **Noise guards:** scripted/animation shows only, no self roles, and a link needs two characters or one
    full-name character. Checked by hand on the 595-show catalog: 30 universes, no false links.
  - **Naming:** a universe is named and addressed by its hub, the show sharing the most characters.
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
- **Privacy rule:** a figure derived from our users needs at least 5 users or ratings behind it (`MinUsersToShow`,
  `MinRatingsToShow`). The exception is "your taste" (`TasteApi`), which uses only the caller's own data.
- **Show tags** (keys in `ShowTags`): `drops-at-once` (or "drops in parts"), `bingeable`, `weekend-binge`,
  `fan-favorite`, `limited-series`, `quick-episodes`, `long-running`, `prolific-creator` / prolific producer.
  - Tags are computed hourly and sent to the catalog as `ShowTagsChangedIntegrationEvent`, in batches of at most 500.
    They're also sent in full after start and daily.
  - Weekly release is deliberately not a tag, since nearly every show would have it.
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
- **Episode patterns:** consistency is the rating spread (steady < 0.3, rollercoaster ≥ 0.6), plus peak/decline arc,
  finales vs. the rest of their season, and each season's best episode.

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

- **Migrations:** Catalog and Analytics use EF Core migrations, applied at startup by their API. The other services
  still use `EnsureCreated`.
- **Array columns:** EF can't translate a GroupBy over an array column's elements, so use raw SQL with `unnest`
  (see `OpsApi`).

## Known gaps

- There are no tests and no CI.
- There's no dead-letter queue.
- The MAUI app has never been compiled.
- Social.API and the weekly digest email aren't built yet.
- Development secrets are in `AppHost.cs` and the realm file.
