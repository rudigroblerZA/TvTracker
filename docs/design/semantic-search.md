# Semantic search: technical write-up

Status: as built. Checked against the code on 27 November 2026. Measurements quoted here are the ones recorded in
the README and in code comments; nothing was re-measured for this document, and section 12 says what has never been
measured.

## 1. What it is

Search matches titles first. Semantic search adds the shows whose *meaning* is close to what was typed: "naval crime
investigators" finds NCIS, "cooking competition" finds The Great British Bake Off, "chemistry teacher becomes a drug
dealer" finds Breaking Bad. It does this by turning every show's text, and each search term, into a 384-number vector
and comparing them.

The same vectors serve four other features, so this is less one feature than the catalog's sense of what shows are
about:

| Feature | Uses the vectors to |
|---|---|
| Search | Add up to 30 similar shows after the title matches |
| Blend and Mixtape | Find shows between two or more others (`Catalog.Mixes`) |
| Nanogenres | Cluster shows into fine-grained genres (40% of the distance) |
| Mood | Rank a known set of shows by how well they fit a mood text |

## 2. What a member sees

- Typing in the search box suggests catalog titles as you type. That is title matching only (`/shows/suggest`); no
  vectors are involved, which keeps it cheap enough for every keystroke.
- Pressing Enter runs the full search. Title matches come first. Shows that match by meaning follow under their own
  heading, "Similar in meaning", so they don't read as wrong answers. With no title match: "No titles match; these are
  similar in meaning."
- When the model is unavailable, search silently returns title matches only. Nothing tells the member.

## 3. The parts

```
                    ┌──────────────────────── Catalog.SyncWorker ────────────────────────┐
 provider ──sync──▶ │ shows ──▶ ShowEmbeddingIndexer (every 30 s, 32 at a time) ──┐       │
                    └──────────────────────────────────────────────────────────────┼──────┘
                                                                                   ▼
                                     embedding model  ◀── text ──   show_embeddings (pgvector, HNSW)
                               (model server or in-process)                        ▲
                                          ▲                                        │
                    ┌─────────────────────┼──────────── Catalog.API ───────────────┼──────┐
 search term ─────▶ │ SemanticSearch: embed the term ─────▶ nearest shows under the cutoff │
                    └──────────────────────────────────────────────────────────────────────┘
```

| Part | Where | Job |
|---|---|---|
| `Catalog.Embeddings` | library | The model: which one, how to call it, how close counts as a match |
| `ShowEmbeddingIndexer` | Catalog.SyncWorker | Keeps one vector per show current |
| `show_embeddings` | catalog database | The vectors, with an index for nearest-neighbour search |
| `Catalog.Search` | library | `TitleSearch` (by title), `SemanticSearch` (embeds a term and finds the nearest shows), and `ShowSearch`, which puts title matches, near-spellings and similar shows into pages |
| Search endpoint | Catalog.API (`CatalogApi.cs`) | Filters, the import from the provider, the rate limit and the answer's shape |

Both services load the same model from the same configuration; the AppHost passes its whole `Embeddings` section to
each. A vector records which model made it, and vectors from different models are never compared.

## 4. Indexing

### 4.1 What is embedded

One string per show (`ShowEmbeddingIndexer.EmbeddingText`):

```
{Title}. {Genre, Genre}. On {Network}. {Overview}
```

Parts that are missing are left out. Cast, keywords, tags, episode titles and reviews are not included. The model's
document prefix, if it has one, goes in front.

### 4.2 Keeping it current

Every 30 seconds the indexer takes up to 32 shows, most popular first, that have no vector from the current model
embedded since the show was last synced. That one condition covers a new show, a refreshed show and a change of model.
It repeats until none are left, then sleeps.

For each batch it computes a SHA-256 of each show's text and only calls the model for shows whose text or model
changed. A sync that changed nothing that is embedded just gets its timestamp moved forward. So a routine refresh of
the catalog costs no model calls.

On first start this backfills the whole catalog. With the default model on a 4-core CPU a batch of 32 takes about 25
seconds, so 846 shows took about eight minutes.

If the model is unavailable the round is skipped and tried again in 30 seconds. Any other failure is logged and
retried the same way.

### 4.3 Storage

`show_embeddings`, one row per show:

| Column | |
|---|---|
| `ShowId` | key |
| `Model` | the model's id; a different model's rows are ignored and replaced |
| `Embedding` | `vector(384)`, unit length |
| `SourceHash` | SHA-256 of the embedded text |
| `EmbeddedAtUtc` | compared with the show's last sync to find stale rows |

An HNSW index on `Embedding` with cosine operators. Postgres runs from the `pgvector/pgvector` image for this.

The column is 384 wide. Both services check the model against it at start-up and refuse to start on a mismatch, since
changing it needs a migration of the column and its index.

## 5. Searching

`GET /api/v1/shows?search=…` on Catalog.API, public, rate limited (a burst of 20 and 30 a minute per member or client
address).

1. **Import.** On the first page, the sync worker is asked to import the provider's matches for the term that the
   catalog doesn't have, with a bounded wait. Shows imported now are not embedded yet, so they can appear by title but
   not by meaning until the indexer's next round.
2. **Filters.** Genre, status, tags and nanogenre narrow the set all three kinds of match draw from.
3. **Title matches**, best first: the exact title, then titles with a word starting with the term, then the rest; most
   popular first within each. These fill the first pages.
4. **Near-spellings.** When fewer than 5 titles contain the term and it is at least 4 characters, up to 12 titles
   spelled nearly like it (Postgres trigrams): "breakng bad".
Steps 3 to 6 are `ShowSearch.PageAsync` in `Catalog.Search`; the endpoint does 1 and 2 and hands it the shows to search.

5. **Similar in meaning**, from shows whose title does *not* contain the term (`SemanticSearch.FindSimilarAsync`):
   - Terms under 3 characters are skipped: too little meaning to compare.
   - The term is embedded as a query (the model's query prefix in front).
   - Shows are joined to their vectors for the current model, kept when their cosine distance to the term is under the
     model's `MaxDistance`, ordered closest first, and cut to 30.
   - Any that were already listed as near-spellings are dropped.
6. **Paging.** Title matches first; near-spellings and similar shows follow the last title match. The answer carries
   the total and how many were title or spelling matches, which is how the page knows where to put the heading.

The order is a rule, not a score: a title match always outranks a match by meaning, however weak the one and strong
the other.

### 5.1 The cutoff

`MaxDistance` is the cosine distance beyond which a show is not a match. Each model spreads its distances
differently, so it is set per model, by hand, from test searches:

| Model | Cutoff | Where related and unrelated searches landed |
|---|---|---|
| `dmr/qwen3-embedding-0.6b` (default) | 0.46 | expected shows around 0.40 (90th percentile 0.455); unrelated from 0.463 |
| `dmr/nomic-embed-text-v1.5` | 0.38 | related 0.25 to 0.38; unrelated from about 0.38 |
| `all-MiniLM-L6-v2` | 0.65 | related 0.3 to 0.64; unrelated from about 0.66 |
| `bge-small-en-v1.5` | 0.45 | related 0.22 to 0.50; unrelated from about 0.46 |

The margins are thin: for the default model the gap between the 90th percentile of wanted results and the first
unwanted one is 0.008. The cutoff is also what Blend, Mixtape and nanogenres use for "close", so it is the one number
a new model has to get right.

## 6. Models

A model is a setting, `Embeddings:Model`. Four are built in; others can be defined under `Embeddings:Models:{id}`.

| Model | Runs | Size | Notes |
|---|---|---|---|
| `dmr/qwen3-embedding-0.6b` (default) | Docker Model Runner | ~640 MB | 1024 dimensions cut to 384; about 130 ms a search on a CPU |
| `dmr/nomic-embed-text-v1.5` | Docker Model Runner | ~270 MB | 768 cut to 384; the previous default |
| `all-MiniLM-L6-v2` | in-process, ONNX | ~90 MB | native 384; needs no server |
| `bge-small-en-v1.5` | in-process, ONNX | ~130 MB | native 384; about half MiniLM's speed |

Two ways to run one, behind the same `IEmbeddingGenerator<string, Embedding<float>>`:

**A model server** (`ServerEmbeddingGenerator`): any server with the OpenAI `POST /embeddings` API. The AppHost runs
Docker Model Runner as a container and points both services at it; the sync worker pulls the model if the server
doesn't have it, retrying every minute. Text is cut to about four characters per token of the model's limit before it
is sent, because the server refuses input longer than its batch. A longer vector is cut to 384 and re-normalised, only
for models trained for that (Matryoshka); others are refused.

**In-process** (`LocalEmbeddingGenerator`): ONNX Runtime with a WordPiece tokenizer. The model files are downloaded on
first use to a temporary name, checked against a pinned SHA-256, and moved into place, so a stopped download or two
services downloading at once never leave a partial file. Token vectors are pooled (mean, or the first token, per
model) and normalised. Inference runs off the request thread.

Queries and documents can be embedded differently: a model may have a query prefix (Qwen3's instruction line, nomic's
`search_query:`) and a document prefix. Callers say which they are embedding.

Changing the model re-embeds the catalog within minutes. Until it finishes, semantic results come only from the shows
already redone, and nanogenre discovery waits until 80% are.

## 7. When things fail

The design rule: search never fails because of the model. It degrades to titles.

| Failure | What happens |
|---|---|
| Model server down, or model not pulled | The generator throws `EmbeddingsUnavailableException`; search returns title matches only. For the next 30 seconds it fails immediately without calling the server, so searches don't each wait on it. One warning is logged per outage. |
| In-process model not downloaded, or download failed | Same, with a 5-minute pause before trying to load again. |
| Server returns the wrong number or size of vectors | Treated as unavailable. |
| Indexer can't embed | Round skipped; retried in 30 seconds. Shows stay unembedded and are found by title only. |
| A show isn't embedded yet | It is simply not among the similar shows. Blend and Mixtape say "still being indexed". |
| Unknown model, incomplete settings, or wrong dimensions | The service refuses to start, with a message naming what is missing. |

The ops summary reports the index: the model, how many shows are current, stale and missing, and when one was last
embedded.

## 8. How the model was chosen

Two comparisons are recorded, both by hand-written test searches with one expected show each.

- **41 searches over 846 shows** (the current default's): Qwen3 0.6B put the expected show first for 30 and in the top
  3 for 34 (MRR 0.78). Nomic managed 18, 27 and 0.57. MiniLM and bge-small were not run on this set.
- **42 searches over 595 shows** (earlier): MiniLM put the expected show first for 27 and in the top 3 for 35;
  bge-small 24 and 31; nomic ranked below both (MRR 0.63). MiniLM ran about 2.5 times faster than bge-small.

So the default was chosen against nomic only, and the best in-process model was never compared with it on the same
searches. The two sets are not comparable with each other: different searches, different catalogs.

## 9. Cost

- A search costs one embedding (about 130 ms on a CPU with the default model) plus one vector query. Paging to page 2
  repeats both.
- Nothing is cached: the same term embedded twice costs twice. The rate limit bounds it per caller.
- Indexing is the heavier load but is amortised: a full backfill once, then only shows whose text changed.
- The model server keeps the model loaded; its memory use has not been measured here.

## 10. Tests

- `EmbeddingModelsTests`: every built-in model can be selected by name alone (added on 27 November 2026, after the
  in-process models were found to stop the service at start-up).
- `ShowMixerTests`: the vector queries Blend and Mixtape make, against a real database.
- `TitleSearchTests`: the title half of search.
- `ShowSearchTests` (11, against a real database with a scripted model): the order of the three kinds of match, the
  cutoff, a show with no vector, a misspelling counted once, paging across the boundary, the model being down, a
  term too short to embed, the term embedded as a query, searching only the shows handed in, and suggestions.

There is no test of the indexer (what it re-embeds and what it skips) or of either generator (pooling, truncation,
the outage pause).

## 11. Decisions

| Decision | Instead of | Why |
|---|---|---|
| Titles first, meaning after, under its own heading | One blended ranking | A typed title is nearly always what was wanted; a wrong "similar" result above it reads as a broken search. |
| Vectors in Postgres (pgvector) | A separate vector store | One database, one transaction model, filters and joins for free. Enough at this catalog's size. |
| A local model | A hosted embedding API | No key, no per-search cost, no show text or search terms leaving the deployment. |
| One fixed width (384) | A column per model | Any model fits one column and one index; longer vectors are cut where the model allows it. |
| Model as configuration, id stored per vector | Hard-coded model | A model can be swapped by changing a setting; old vectors are replaced, never mixed. |
| Degrade to titles | Fail the search | Search is the front door; the model is an enhancement. |
| A cutoff | Always return the 30 nearest | "knitting patterns for beginners" should return nothing, not the 30 least-unlike shows. |

## 12. Gaps

1. **The test searches are not in the repository.** The 41 and 42 searches and their expected shows exist only as
   results quoted in the README. Nobody can re-run them, so a new model, a change to the embedded text or a new cutoff
   can't be checked against the old numbers. Blend and Mixtape now have `mix-eval` and a list; search has neither.
2. **The indexer and the generators are untested** (section 10). The in-process path was broken at start-up without
   anyone noticing until it was needed.
3. **Whether the vector index is used by search is not verified.** The search query joins a filtered set of shows to
   their vectors, filters on distance and then orders. Postgres may answer that with an exact scan instead of the HNSW
   index. That is correct, and fast at hundreds of shows; nobody has looked at the plan or timed it on a large catalog.
4. **Cutoffs are hand-tuned with thin margins**, on catalogs under 1,000 shows. A larger catalog has more near-misses
   around the cutoff.
5. **The embedded text is thin.** No cast, no keywords, no tags, and only as much of the overview as fits the model's
   limit. "The one with the therapist" works only if the overview says so.
6. **English only.** The models and the test searches are English; shows with overviews in other languages, and
   searches in them, are untested.
7. **No cache of term vectors**, and page 2 of a search embeds the term again.
8. **A silent degrade.** When the model is down the member gets title matches and no sign that anything is missing.
9. **A wider model needs a migration.** 384 is in the schema.
10. **The README is out of date in one place**: its opening search paragraph still says the vectors come from MiniLM
    run in-process, which stopped being the default.

## 13. What I would do next, in order

1. Put the test searches in the repository with a command that runs them (recall at 1 and 3, MRR, and how many
   unrelated searches return nothing), as `mix-eval` does for mixes. Everything else on this list needs it.
2. Run all four models on that one set, so the default is chosen against the in-process models too.
3. Test the indexer against a real database, and the generators. (Search itself is covered since it moved into
   `Catalog.Search`.)
4. Look at the query plan on a catalog of 10,000 or more shows before it matters.
5. Try a richer embedded text (cast and keywords) and measure it with 1.

## 14. Where the code is

| What | Where |
|---|---|
| Models, settings, the cutoffs | `src/Services/Catalog/Catalog.Embeddings/EmbeddingModel.cs` |
| Registration and start-up checks | `Catalog.Embeddings/EmbeddingServiceCollectionExtensions.cs` |
| Model server client | `Catalog.Embeddings/ServerEmbeddingGenerator.cs` |
| In-process model | `Catalog.Embeddings/LocalEmbeddingGenerator.cs` |
| Indexer and the embedded text | `src/Services/Catalog/Catalog.SyncWorker/ShowEmbeddingIndexer.cs` |
| Storage | `ShowEmbedding` in `src/Services/Catalog/Catalog.Data/Model.cs`; mapping in `CatalogContext.cs` |
| Finding similar shows | `src/Services/Catalog/Catalog.Search/SemanticSearch.cs` |
| The three kinds of match put into pages; suggestions | `Catalog.Search/ShowSearch.cs` |
| Title matching | `Catalog.Search/TitleSearch.cs` |
| The search endpoint | `SearchShowsAsync` and `QueryShowsAsync` in `Catalog.API/Apis/CatalogApi.cs` |
| Rate limit | `Catalog.API/Services/SearchRateLimiting.cs` |
| The model server container | `src/TvTracker.AppHost/AppHost.cs` (the `model-runner` resource) |
| Search page | `src/Clients/TvTracker.UI/Pages/Search.razor` |
| Other users of the vectors | `src/Services/Catalog/Catalog.Mixes`; `Catalog.SyncWorker/Nanogenres`; `Catalog.API/Apis/MoodApi.cs` |
