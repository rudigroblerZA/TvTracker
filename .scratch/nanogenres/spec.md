# Nanogenres: spec

Status: describes the feature as built (flag `nanogenres`, on by default, free, public). Checked against the code on
27 November 2026. Requirements the code does not meet are listed in section 11, not mixed in.

## 1. Summary

A nanogenre is a fine-grained genre the catalog finds in itself: "Sports comedy", "Medical drama", "Animated family
comedy", "Cooking reality". Nobody writes the list. A background job groups shows by what they are about and what
kind of show they are, names each group from the genres its shows share, and throws away any group it cannot name
honestly.

Members meet nanogenres as chips on a show page, a browse page, and a search filter. Recommendations use them to rank
similar shows and to give the plainest reason there is: "Also a sports comedy".

Examples the README records from the development catalog: Ted Lasso is a "Sports comedy"; Wednesday is both a
"Supernatural comedy" and a "Mystery comedy"; The Simpsons is an "Animated family comedy"; The Office has none.

## 2. Terms

| Term | Meaning |
|---|---|
| Genre | A provider's genre on a show (Comedy, Drama, Legal, Sports, …). |
| Kind | The provider's show type, kept in a show's keywords: scripted, animation, reality, documentary, game show, … (`ShowKinds`). A show has at most one. |
| Base | What a show is at heart, the noun a name ends with: anime, reality, documentary, game show, comedy, thriller, horror, drama. |
| Modifier | A specific genre as it reads before a base: Legal → "legal", Science-Fiction → "sci-fi", Food → "cooking". 22 are defined. |
| Pairing | A (modifier, base) pair and the shows that have it: ("sports", "comedy"). |
| Cluster nanogenre | Found by clustering (`Source = "cluster"`). |
| Pairing nanogenre | A small group sharing an uncommon pairing, where no cluster already covers it (`Source = "genres"`). |
| Core | A cluster's members before other shows join it. Names are decided from the core. |

## 3. Goals and non-goals

Goals:

- G1. Find genres finer than the provider's, from the catalog alone, with no curated list to maintain.
- G2. Never show a vague or wrong name. A group that can't be named from what its shows share is not shown.
- G3. A show can be in more than one nanogenre.
- G4. Keep up with the catalog: new shows are placed without anyone doing anything.
- G5. Give recommendations a reason a person would say out loud.

Non-goals:

- Covering every show. On the development catalog 393 of 625 shows were in at least one. A pure comedy with nothing
  more specific about it has none, by design.
- Personalisation. Nanogenres are facts about the catalog; nothing about a member goes in or comes out.
- Stable identity over time. A nanogenre exists while discovery finds it (see R-D8 and section 11).
- Themes from the shows' summaries. Summary words were tried as names and were too noisy ("greatest", "these").

## 4. Discovery

Lives in `Catalog.SyncWorker/Nanogenres/NanogenreDiscovery.cs`: a pure function from the catalog's shows (id, genres,
keywords, embedding) and the embedding model's cutoff to a list of nanogenres with their members.

### 4.1 Distance between two shows

- R-D1. `distance = 0.4 × text × scale + 0.6 × kind`.
  - `text` is the cosine distance between the two shows' embeddings (the vectors semantic search uses: title, genres,
    network, overview).
  - `kind` is the cosine distance between the shows' genre-and-kind vectors: one axis per genre and one per kind,
    unit length, so two shows with the same genres and kind are at 0.
  - `scale = 0.38 / model.MaxDistance`. The cut was tuned with a model whose "similar" cutoff is 0.38; another
    model's distances are scaled to match.
- R-D2. Genres weigh more than text (60/40) so a sports comedy groups with comedies about sport, not with sports
  documentaries.
- R-D3. Only shows embedded by the current model are clustered. A show without an embedding can still be in a pairing
  nanogenre.

### 4.2 Clusters

- R-D4. Average-linkage clustering, cut at 0.25. A cluster larger than 30 shows is clustered again at a cut 0.03
  tighter, repeatedly, until it is 30 or under or the cut reaches zero. Clusters under 3 shows are dropped.
- R-D5. **Joining.** For each cluster, take the median over its members of each member's average distance to the
  others: the "typical member". Any show outside the cluster whose average distance to the cluster's members is at
  most that joins it. This is how a show gets more than one nanogenre.

### 4.3 Names

- R-D6. A cluster's candidate names come from the pairings that cover at least 60% of its core, widest cover first,
  rarer genre first on a tie (rarer says more):
  1. `"{modifier} {base}"` of the best pairing: "Medical drama".
  2. Then two-modifier names on the same base, for when the first is taken, only where at least 60% of the core has
     both: for example "Family sports comedy".
  - If at least 60% of the core is animation (and not anime), every name is prefixed "Animated".
  - "Mystery thriller" is never formed: it says nothing "thriller" doesn't.
- R-D7. A cluster with no covering pairing gets no name and is dropped, with one exception: a cluster that is at
  least 60% animation and at least 60% comedy is "Animated comedy".
- R-D8. A cluster takes its first candidate name not already taken. If every candidate is taken, it is the same
  nanogenre as the holder of its first name, and its members are merged into that one.
- R-D9. Names are sentence case. The slug is the lower-cased name with apostrophes removed and every other run of
  non-alphanumerics turned into one hyphen: "Children's adventure" → `childrens-adventure`.

### 4.4 Pairing nanogenres

- R-D10. Every pairing with 2 to 15 shows becomes a nanogenre named `"{modifier} {base}"`, unless that name is
  already taken, or an existing nanogenre already contains half or more of its shows. Larger pairings ("Crime drama",
  113 shows on the development catalog) are not nano.

### 4.5 Output

- R-D11. The result is ordered by name. Each nanogenre lists its members in catalog order.
- R-D12. Discovery is deterministic for the same input: no randomness is used.

## 5. The job

`NanogenreService`, a background service in `Catalog.SyncWorker`.

- R-J1. First run 3 minutes after the worker starts (so new embeddings are in), then every 6 hours.
- R-J2. **Readiness guard.** Discovery runs only when at least 80% of shows have an embedding from the current model.
  Otherwise the current nanogenres are kept and it tries again in 15 minutes. Without this, a fresh catalog or a model
  change would replace every nanogenre with nothing.
- R-J3. **Replace whole, in one transaction:** delete all rows of `nanogenres`, insert the new ones, update each show
  whose list changed, and add the events to the outbox. Readers never see a half-written set.
- R-J4. A show's list is ordered most specific first: smallest nanogenre first, then by name.
- R-J5. Shows are updated one statement per distinct list, not per show, so the transaction holds show rows for few
  round trips even when most shows change (after a model change).
- R-J6. **Events.** `ShowNanogenresChangedIntegrationEvent` carries (show id, names) for each show whose list changed,
  in chunks. On the first run after starting, and once a day after, it carries every show, so a lost event heals.
- R-J7. A failed run is logged and retried at the next interval. It changes nothing (R-J3).
- R-J8. The job runs whether or not the `nanogenres` flag is on, because recommendations use the result either way.

## 6. Data

Catalog database:

| What | Shape |
|---|---|
| `nanogenres` | `Slug` (key), `Name`, `Source` ("cluster" or "genres"), `ShowCount`, `ComputedAtUtc`. Rebuilt whole each run. |
| `shows.Nanogenres` | `text[]` of slugs, most specific first, default empty, GIN index. The membership; there is no join table. |

Recommendations database:

| What | Shape |
|---|---|
| `ShowNanogenres` | `ShowId`, `Names` (not slugs), most specific first. A copy kept from the event; it can arrive before the show does. |

No table holds anything about a member, so the erasure and export rules do not apply.

## 7. API

All on Catalog.API, public (no sign-in), behind the `nanogenres` flag.

- R-A1. `GET /api/v1/nanogenres` → every nanogenre, largest first then by name: `Slug`, `Name`, `ShowCount`, and the
  posters of its 4 most popular shows that have one. Cached for 10 minutes; posters are picked in SQL so a request
  does not read every show.
- R-A2. `GET /api/v1/nanogenres/{slug}` → `Slug`, `Name`, `Source`, and its shows as show summaries, most popular
  first then by title. 404 for a slug that no longer exists.
- R-A3. `GET /api/v1/shows?...&nanogenre={slug}` filters search to a nanogenre. With the flag off the parameter is
  ignored, not refused.
- R-A4. A show's detail carries its nanogenres as (slug, name) pairs in the show's order. A slug with no nanogenre
  behind it any more is left out.
- R-A5. The ops summary reports how many there are, how many are clusters, how many shows are covered, when they were
  last computed, and the 6 largest.

## 8. What a member sees

- R-U1. **Show page:** a row of chips under the genres, one per nanogenre, each linking to that nanogenre's page.
- R-U2. **`/nanogenres`:** a one-paragraph explanation and a grid of cards: four posters, the name, the count. Empty
  state: "None found yet. They're discovered a few minutes after the catalog's shows are indexed."
- R-U3. **`/nanogenres/{slug}`:** the name, how many shows, one line on how it was found (cluster: "grouped by what
  they're about and what kind of show they are"; pairing: "the catalog's shows that pair these genres"), a "Search
  within it" link, and the shows as cards. A slug that is gone says so and links back to the list.
- R-U4. **Search:** an "Any nanogenre" dropdown, shown when there are any. The choice is held in the address
  (`?nanogenre=`), so a filtered search can be linked to.
- R-U5. **Discover:** one of the three pinned cards: "Fine-grained genres like sports comedy or cooking reality, found
  in the catalog."
- R-U6. With the flag off, the chips, both pages, the dropdown and the Discover card are all gone together.

## 9. Recommendations

- R-R1. Recommendations keeps each show's nanogenre names from the event (handled once per event) and, when any
  show's names changed, marks every member's list for rebuilding.
- R-R2. Two shows' content similarity gains `0.25 × Jaccard(their nanogenres)`, capped at 1. It is a bonus, not a
  share of the score: most shows are in no nanogenre, and a share would have cost every such pair a quarter of its
  score.
- R-R3. The first nanogenre of show A that B also has (so, A's most specific shared one) is the reason shown: "Also a
  sports comedy" on similar shows, "Another sports comedy, like Ted Lasso" on a pick.
- R-R4. DNA twins ("alike in how they play out, different in what they're about") must share no nanogenre.

## 10. Verification

What is tested today: the slug rule (`NanogenreSlugTests`, four cases), and nanogenres' part in the recommendation
score (`Recommendations.UnitTests/MatchTests`).

What a complete test set would cover, none of which exists yet:

- V1. Distance: genres outweigh text; the model scale is applied.
- V2. Clustering: a planted group is found; a group over 30 is split; a group under 3 is dropped.
- V3. Joining: a show equidistant from two groups is in both.
- V4. Naming: cover threshold; rarer genre wins a tie; the animated prefix; no "Mystery thriller"; an unnameable group
  is dropped; a second cluster with the same candidates is merged.
- V5. Pairings: size bounds; skipped when half-covered by an existing nanogenre.
- V6. Determinism: the same input twice gives the same output.
- V7. The job, against a real database: the readiness guard keeps the old set; a run replaces the set; only changed
  shows are updated and sent; the daily full send.
- V8. The endpoints: order, posters, 404, the search filter, the flag.

Discovery is a pure function, so V1 to V6 need no database.

## 11. Known gaps

1. **Discovery will not scale.** It builds every pair's distance in memory: n² doubles for the main pass, and again
   inside clustering. At 5,000 shows that is about 200 MB per matrix; at 50,000 it is 20 GB. The parameters and the
   approach were chosen on 625 shows. Something has to change before the catalog is an order of magnitude larger.
2. **Almost no tests** (section 10). The naming rules in particular are intricate and unprotected.
3. **Slugs are not stable.** A nanogenre is its name; when membership shifts and a cluster's best pairing changes, the
   old slug is gone and its page is a "not found". Links, bookmarks and filtered-search addresses break, and nothing
   redirects. Whether anyone is hurt depends on how often names churn between runs, which is not measured.
4. **Churn is invisible.** The job logs how many shows it sent, but not which nanogenres appeared or disappeared since
   the last run. There is no way to tell a healthy catalog from one whose nanogenres reshuffle every six hours.
5. **Tuned for another model.** The cut and weights were tuned with `dmr/nomic-embed-text-v1.5`; the default model is
   now a different one. Other models are scaled by one number (their search cutoff), which assumes models differ only
   in spread. Nobody has checked the nanogenres the default model produces against the tuned ones.
6. **Nothing shows how a show got its nanogenre.** A member who thinks a chip is wrong has no explanation and no way
   to say so.
7. **Recommendations rebuilds every member's list** whenever any show's names change, and the daily full send repeats
   every show. The handler skips unchanged shows, so the full send is cheap, but one changed show still marks
   everyone.
8. **Recommendations stores names, Catalog stores slugs.** A rename would look like a removal and an addition to
   Recommendations; today that is harmless because a name is the identity.

## 12. Open questions

- How large will the catalog get, and by when? It decides how urgent gap 1 is and which fix fits (cluster within a
  base only; sample; an approximate method).
- Should a nanogenre keep its slug when its membership drifts? That needs an identity other than the name, such as
  matching each new cluster to the previous run's by overlap.
- Should editors be able to pin, rename or hide a nanogenre? The premise is "no list written down"; an override list
  is the usual end of that premise.
- Is 2 shows enough for a pairing nanogenre? A page with two shows is thin; the floor for clusters is 3.
- Should the six-hour interval be replaced by "after enough shows changed"? Most runs on a quiet catalog find the same
  thing.

## 13. Where the code is

| What | Where |
|---|---|
| Discovery | `src/Services/Catalog/Catalog.SyncWorker/Nanogenres/NanogenreDiscovery.cs` |
| The job | `src/Services/Catalog/Catalog.SyncWorker/Nanogenres/NanogenreService.cs` |
| Storage | `Nanogenre` and `Show.Nanogenres` in `src/Services/Catalog/Catalog.Data/Model.cs`; mapping in `CatalogContext.cs` |
| Endpoints | `src/Services/Catalog/Catalog.API/Apis/NanogenreApi.cs`; the search filter in `CatalogApi.cs`; ops in `OpsApi.cs` |
| Event | `ShowNanogenresChangedIntegrationEvent` in `src/TvTracker.IntegrationEvents/CatalogEvents.cs` |
| Recommendations' copy and use | `Recommendations.Data/ShowNanogenres.cs`; `ShowFeaturesHandler` in `Recommendations.API/IntegrationEvents/Handlers.cs`; `RecommendationEngine.cs` |
| Pages | `Nanogenres.razor`, `NanogenrePage.razor`, and the chips in `ShowPage.razor`, the filter in `Search.razor` (`src/Clients/TvTracker.UI/Pages`) |
| Flag | `nanogenres` in `src/TvTracker.AppHost/Flags/flagd.json`; `FeatureFlags.Nanogenres` |
| Tests | `tests/Catalog.UnitTests/NanogenreSlugTests.cs`; `tests/Recommendations.UnitTests/MatchTests.cs` |
