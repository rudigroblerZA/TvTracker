# Blend: design

Status: as built (on by default, free). Last checked against the code on 27 November 2026.

Blend takes two shows and returns the shows that sit between them: Breaking Bad × The Office gives The Gentlemen,
Brooklyn Nine-Nine and BEEF. This document records how it works, why it works that way, where it is weak, and what
would be worth changing. It describes the code as it is; the "Gaps" and "Open questions" sections say what is not
done.

## 1. What it is for

A viewer who likes two different shows wants a third that has something of both. Search can't answer that (there is no
phrase to type), and "shows like this" answers it for one show only. Blend answers it from two picks and no typing
beyond the titles.

It is also the product's cheapest demonstration that the catalog understands what shows are about, which is why it is
free, works signed out, and is one of the three cards pinned to the top of Discover.

### Goals

- Two shows in, up to 20 shows out, in well under a second.
- Results that are like *both* shows. A show that is very like one and nothing like the other must rank low.
- The viewer can lean the result towards either show.
- Every result says why it is there, in the viewer's terms (genres, and how much of each show).
- A blend is a link: it can be shared and bookmarked, and opens the same for someone signed out.
- When the two shows have nothing in common, say so instead of presenting weak results as good ones.

### Not goals

- Personalisation. A blend is the same for everyone; it does not read what the viewer follows, watched or rated.
- More than two shows. That is Mixtape (`/mixtape`, three to five shows, Pro), which shares the idea but not the code.
- Explaining similarity beyond genres. The vectors carry more than genre, but nothing else is shown.

## 2. How a member meets it

| Where | What |
|---|---|
| Discover | The first of the three pinned cards: "Mix two shows and find what sits between them." |
| Every show page | "Blend *title* with another show", which opens `/blend?a={id}` with the first show picked. |
| Signed-out menu | A "Blend" link, since Discover's tools need sign-in but this one does not. |
| Welcome page | Listed under "Blend" with the other taste tools. |

On `/blend`:

1. Two pickers. Typing a title lists up to 8 catalog matches; picking one shows its poster, year and network with a
   "Change" link. A picker won't offer the show already in the other one.
2. With nothing picked, four example pairs are offered ("Breaking Bad × The Office"). An example whose show isn't in
   the catalog says so and suggests searching for it, which imports it.
3. With both picked, a slider appears ("More A" to "More B") and the results load.
4. Each result is a show card with a two-colour bar (how much of each show) and one line of reason:
   "Crime from Breaking Bad · Comedy from The Office", or "Crime, Drama, like both" when it takes nothing from just one.
5. Above the results, when the two have little in common: "These two don't have much in common, so these are the
   closest middle ground rather than close matches."
6. When either show was imported in the last minute: "One of these shows was just added and is still being indexed.
   Try again in a minute."

The address is the state: `/blend?a={id}&b={id}&lean=70`. Picking a show or moving the slider replaces the address
(no history entry per slider step), and the page loads from the address. `lean` is left out at 50.

## 3. How it works

### 3.1 The data it stands on

Blend adds no storage. It reads `show_embeddings` in the catalog database, which semantic search owns:

- One row per show: a 384-dimension vector (`vector(384)`, pgvector), the model that made it, a hash of the text it was
  made from, and when.
- The text is the title, genres, network and overview (`ShowEmbeddingIndexer.EmbeddingText`).
- `Catalog.SyncWorker` keeps it current: every 30 seconds it embeds shows that are new, changed, or were embedded by a
  different model, 32 at a time, most popular first.
- An HNSW index on the vector for cosine distance. Vectors are normalized.

So a show can be blended once the sync worker has embedded it, normally within a minute of import.

### 3.2 The request

`GET /api/v1/shows/blend?a={id}&b={id}&lean=50` on Catalog.API (`BlendApi.cs`), behind the `blend` flag.

1. `a == b` is a validation error ("Pick two different shows."). `lean` is clamped to 0-100.
2. Load both shows; either missing is a 404.
3. Load both vectors for the current model. Either missing returns `Indexed: false` with no matches (the "still being
   indexed" notice), not an error.
4. **Mix.** With `w = lean / 100`: `mix = normalize(w·A + (1 − w)·B)`.
5. **Candidates.** The 40 shows nearest `mix` by cosine distance, leaving out A, B, and any show with the same title as
   either (a remake, or the classic Doctor Who beside the new one). For each, also fetch its distance to A and to B.
6. **Rank.** `score = max(2w · dA, 2(1 − w) · dB)`, lowest first, top 20.
7. Load the 20 shows and build the answer.

Four queries: the two shows, the two vectors, the candidates (the only one that uses the vector index), the matches.

### 3.3 Why rank by the farther show

Nearness to the mixed vector alone is not enough. The midpoint of two unlike shows sits in a sparse part of the space,
and the nearest things to it can be shows very close to one parent and far from the other: an NCIS spin-off, for
NCIS × Friends. Ranking by the *larger* of the two distances asks the right question, "how far is this from the show it
is least like?", so a spin-off of one parent drops however close it is to that parent.

The lean weights that question. At 50 both weights are 1 and a match must be close to both. At 80 the weights are 1.6
for A and 0.4 for B: distance from A costs four times as much, so results move towards A without B dropping out. The
mix in step 4 moves the candidate pool the same way, so the two steps agree.

### 3.4 What each match carries

| Field | Meaning | How |
|---|---|---|
| `Fit` | How well it fits between the two, 0-100 | `100 · clamp(1 − score, 0, 1)` |
| `LikeA` | Where it sits between them, as a percentage like A | `100 · dB / (dA + dB)`; the bar on the card |
| `GenresFromA` | Genres it shares with A only | set arithmetic on the three genre lists |
| `GenresFromB` | Genres it shares with B only | same |
| `GenresFromBoth` | Genres all three have | same |

`Fit` is returned but the page does not show it; order carries it.

### 3.5 Loose blends

`Loose` is true when there are no matches, or the best score is above the model's `MaxDistance`: the same cutoff
semantic search uses for "similar enough to be a match", tuned per model (0.46 for the default, 0.65 for the in-process
MiniLM). Reusing it means "close" means one thing across search and Blend, and a new model needs one number, not two.

The results are still shown. A loose blend (Stranger Things × The Great British Bake Off) is often the fun one; the
notice only stops it reading as a recommendation.

### 3.6 The answer

```
BlendDto(A, B, Lean, Indexed, Matches[], Loose)
BlendMatchDto(Show, Fit, LikeA, GenresFromA[], GenresFromB[], GenresFromBoth[])
```

`A`, `B` and each `Show` are the catalog's usual show summary (id, title, poster, year, status, network, genres), so
the page needs no further call to draw a card.

## 4. The page

`BlendPage.razor` in `TvTracker.UI`, shared by the web app and the mobile app.

- **State lives in the address.** `OnParametersSetAsync` loads whenever `a|b|lean` changes, and nothing else triggers a
  load. Sharing, back/forward and reload all work for free.
- **Known shows are remembered** for the life of the page, so picking a show doesn't fetch it again when the address
  changes.
- **The slider** runs 20-80 in steps of 10, left is A. The API accepts 0-100; the page keeps both shows in the mix
  because at 0 or 100 the answer is just "shows like one of them".
- **Pickers** call `GET /api/v1/shows/suggest?q=`: title matches from the catalog only, without the TVmaze import that
  search does, so it is cheap enough to call on each keystroke.
- **Gating.** The page carries `RequiresFeature(blend)` and the endpoint `RequireFeature(blend)`. With the flag off the
  page, the Discover card, the show-page link and the endpoint all go together.

## 5. Decisions and what was turned down

| Decision | Instead of | Why |
|---|---|---|
| Reuse search's vectors | A purpose-built similarity model | No new pipeline, storage or tuning; the vectors already separate shows well enough to blend. Cost: Blend inherits whatever the embedded text misses (tone, pacing, quality). |
| Rank by the farther parent | Rank by distance to the mix | The midpoint attracts one-sided matches (section 3.3). |
| Compute on request | Precompute pairs | Pairs are unbounded and the query is one index scan. Nothing to invalidate when a show is re-embedded. |
| Free, and public | Pro | It is the hook: a signed-out visitor can try it from a shared link. Mixtape, the heavier version, is the Pro one. |
| Show loose blends with a notice | Refuse them | An empty page for two picks the viewer chose is worse than honest weak results. |
| Genres as the reason | A generated sentence | Deterministic, free, and never wrong about what the catalog says. |
| No personalisation | Drop shows the viewer has seen | Keeps a blend a shareable fact, and keeps Catalog free of viewer data. |

## 6. Gaps

Found while writing this; none is fixed yet.

1. **No tests.** Nothing in `tests/` mentions Blend. The ranking rule, the lean weighting, the loose cutoff and the
   genre attribution are all untested, and all sit inside the endpoint where a unit test can't reach them.
2. **No rate limit.** Mixtape and search carry `SearchRateLimiting.Policy`; Blend does not, though it is public and
   runs a vector scan per call.
3. **The candidate pool can run short.** The query asks for the 40 nearest the mix, and pgvector's HNSW scan returns
   at most `ef_search` (40) rows *before* the title and model filters apply. For lopsided pairs the best common ground
   is not always among the 40 nearest the average either. Mixtape already works round both by also taking each show's
   own nearest in one UNION ALL; Blend does not.
4. **No "same kinds" rule.** Mixtape requires a pick to be the same kind of show as the inputs (scripted, animation,
   reality) after Ted Lasso + Succession + Breaking Bad brought in The Amazing Race. Blend can make the same mistake.
5. **Quality depends on catalog size.** On the 290-show local catalog most pairs come back loose. This is expected,
   but it means Blend can't be judged on a small catalog, and a demo environment needs a few thousand shows.
6. **Depends on the embedding model being up at index time only.** If the model server is down, shows imported
   meanwhile stay "still being indexed" until it returns; the notice says "a minute", which is then wrong.
7. **`Fit` is unused** by any client.

## 7. Proposed changes, in order

1. **Move the maths out of the endpoint** into a small pure class (mix, score, fit, like-A, loose, genre split), with
   unit tests in `Catalog.UnitTests`. Add one integration test that seeds a handful of vectors and checks that a
   one-sided show ranks below a two-sided one. This is the prerequisite for changing anything else safely.
2. **Add the rate limit** Mixtape has.
3. **Share candidate gathering with Mixtape**: the mix's nearest plus each parent's nearest, in one query. Blend is
   then Mixtape's rule for two shows with a lean, and the two endpoints stop drifting apart.
4. **Apply the same-kinds rule**, shared with Mixtape.
5. Decide `Fit`: show it (a "strong / fair / loose" word per card) or drop it from the contract.

Items 3 and 4 change results, so they want a before-and-after on a fixed list of pairs (the four examples on the page,
plus the pairs named in the README) before they ship.

## 8. Open questions

- Should a signed-in viewer be able to hide shows they already follow or have finished? It would make Blend more
  useful and less shareable; a toggle on the page, applied client-side from data the page can already get, would keep
  the endpoint viewer-free.
- Should a blend be saveable as a list? The matches are already show summaries.
- Is the slider worth its seven positions, or would three (more A, even, more B) say the same?
- How is Blend judged? There is no measure today. Follows that start from a blend result (a `from=blend` marker on the
  card link) would be the obvious first one.

## 9. Where the code is

| What | Where |
|---|---|
| Endpoint, ranking, DTOs | `src/Services/Catalog/Catalog.API/Apis/BlendApi.cs` |
| Vectors and their index | `ShowEmbedding` in `src/Services/Catalog/Catalog.Data/Model.cs`, mapped in `CatalogContext.cs` |
| Keeping vectors current | `src/Services/Catalog/Catalog.SyncWorker/ShowEmbeddingIndexer.cs` |
| Models and the "similar" cutoff | `src/Services/Catalog/Catalog.Embeddings/EmbeddingModel.cs` |
| Page | `src/Clients/TvTracker.UI/Pages/BlendPage.razor` |
| Picker | `src/Clients/TvTracker.UI/Components/ShowPicker.razor` |
| Client call and models | `CatalogClient.GetBlendAsync` in `Services/ApiClients.cs`; `Blend`, `BlendMatch` in `Models/Models.cs` |
| Flag | `blend` in `src/TvTracker.AppHost/Flags/flagd.json`; `FeatureFlags.Blend` |
| The three-to-five-show version | `src/Services/Catalog/Catalog.API/Apis/MixtapeApi.cs` |
