# TV Tracker

A TV Time–style app for tracking shows: what you follow, what you've watched, when new episodes air, and what to watch next.
Built as eShop-style microservices on .NET 10 and Aspire, with a Blazor web app and a MAUI Blazor Hybrid mobile app
that share one Razor component library. The design behind it is in the "TV Tracker – Service Design" doc.

> **Build status:** everything except the MAUI app builds without errors with `dotnet build` on the .NET 10 SDK.
> The MAUI app (`src/Clients/MobileApp`) needs the MAUI workload and hasn't been compiled yet.

What sets it apart (universes and their timelines, the essential cut, blend, the reunion radar, where you know an actor from, show DNA, nanogenres, search by meaning, computed show tags)
is collected in [FEATURES.md](FEATURES.md), along with ideas for what to build next.

## Prerequisites

- .NET 10 SDK
- Docker Desktop or Podman (Aspire runs PostgreSQL, Redis, RabbitMQ, Keycloak and Docker Model Runner as containers)
- For the mobile app only: the MAUI workload (`dotnet workload install maui`), plus Xcode for iOS or the Android SDK for Android

## Run it

```bash
dotnet run --project src/TvTracker.AppHost
```

The Aspire dashboard opens. The first run pulls container images and imports the Keycloak realm, so it takes a few minutes.
When everything is running, open the `webapp` link and sign in:

- Demo user: **demo / demo**
- App admin: **admin / admin**, who gets Settings → Admin (the feature flags page). It's a user of the app's realm,
  separate from Keycloak's own console admin.
- Or create an account from the sign-in page (Keycloak handles sign-up and password reset)

The catalog is filled from **[TVmaze](https://www.tvmaze.com/api)** (free, no API key). Every hour the sync worker imports the 40 most
popular shows airing today: the US and GB broadcast schedules plus streaming, without news, talk, sports and daily soaps.
The catalog grows as the schedule changes. Imported shows are refreshed daily (ended shows weekly), and a show
is refreshed promptly when someone follows it. The first sync takes a minute or two, because calls are throttled to stay under
TVmaze's limit of 20 calls every 10 seconds. Settings are under `TvMaze` in `Catalog.SyncWorker/appsettings.json`.

TVmaze still calls some long-finished shows "Running" (Girls, Peaky Blinders). A show with nothing aired for 4 years and
nothing scheduled is shown as ended until a new episode is scheduled. Users' follows, watches and ratings are re-sent
daily to the services that keep copies of them, so a lost event repairs itself within a day.

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
to change it). Until it loads, search matches titles only.

The model is a setting: `Embeddings:Model` in the AppHost's `appsettings.json`. The AppHost passes its whole
`Embeddings` section to Catalog.API and the sync worker, so both use the same model. Three are built in:

| Model | Runs | Notes |
| --- | --- | --- |
| `all-MiniLM-L6-v2` | In-process (ONNX), ~90 MB download | Fast; matches below a cosine distance of 0.65 |
| `bge-small-en-v1.5` | In-process (ONNX), ~130 MB download | About twice as slow; separates related from unrelated shows more clearly, and finds some that MiniLM misses ("time travel mystery in a small german town" → Dark); matches below 0.45 |
| `dmr/qwen3-embedding-0.6b` (default) | Docker Model Runner, ~640 MB model | Ranked best of the server models on the test searches (top-1 30/41, top-3 34/41, against nomic's 18 and 27); ~130 ms a search on a CPU; matches below 0.46 |
| `dmr/nomic-embed-text-v1.5` | Docker Model Runner, ~270 MB model | The previous default; matches below 0.38 |

**Docker Model Runner** serves models from Docker Hub's `ai/` namespace over an OpenAI-compatible API. It runs the
default model, so semantic search needs nothing installed beyond Docker.
- **Container:** the AppHost runs `docker/model-runner` as the `model-runner` container, on CPU. Models are kept
  in the `tvtracker-model-runner` volume.
- **First start:** the sync worker pulls the model from Docker Hub (~640 MB). Until it's ready, search matches
  titles only, then the catalog is embedded within a few minutes. If the pull fails, for example because Docker Hub
  rate-limits it, it retries every minute.
- **Your own Model Runner:** set `ModelRunner:Url` in the AppHost's configuration (e.g. `http://localhost:12434`)
  to use one that's already running instead of the container.
  - With Docker Desktop: enable Model Runner with host-side TCP support. It can use your GPU.
  - With Docker Engine: install the `docker-model-plugin`.
  - The sync worker pulls the model there too, if it's missing.
- **Endpoint:** the AppHost sets `Embeddings:Endpoint` for every server model to its Model Runner. A model's own
  `Embeddings:Models:{id}:Endpoint` takes precedence; without either, it's `http://localhost:12434/engines/v1/`.
- **In-process instead:** set `Embeddings:Model` to `all-MiniLM-L6-v2`, which ranked best on the earlier test
  searches and needs no server. The AppHost then doesn't start Model Runner at all.
- **Other servers:** any server with the same `POST /embeddings` API works (llama.cpp, Ollama, vLLM).
- **When it's down,** or doesn't have the model, search answers with title matches only, and the sync worker
  embeds once it's back. Each service logs one warning per outage.
- **Open WebUI** (`open-webui` on the dashboard): a web UI for Model Runner, to see what it serves and chat with it.
  - **Sign-in is off,** since it's for development; it's named after the app (`Branding:AppName`).
  - **Chat needs a chat model.** Model Runner only has the embedding model until you pull one: the `model-runner`
    resource on the dashboard has a "Pull ai/qwen3:0.6B-Q4_K_M" command (another model: `ModelRunner:ChatModel`), or run
    `docker model pull` against your own Model Runner. It shows in Open WebUI once pulled.
  - **Its document search** embeds with the same embedding model, so it downloads no model of its own.
  - **With your own Model Runner** (`ModelRunner:Url`), it's reached from the container through
    `host.docker.internal`; the pull command is only on the AppHost's container.
  - **Size:** the image is about 6 GB. `ModelRunner:OpenWebUi` = `false` leaves it out. Not started for an
    in-process embedding model, since there's no Model Runner then.
- **Another model:** add it under `Embeddings:Models:{id}` with `Source: Server`, `ServedModel` (e.g.
  `ai/embeddinggemma`), `Dimensions: 384`, `MaxDistance`, and the model's `QueryPrefix` and `DocumentPrefix`.
  - Set `Truncate: true` for a model trained to be shortened (Matryoshka models such as Qwen3-Embedding, nomic and EmbeddingGemma):
    its longer vectors are cut to the column's 384 dimensions.
  - A model that makes 384 dimensions natively needs no truncation. A longer one without `Truncate` is refused,
    since cutting other models loses most of their meaning.

**Qwen3-Embedding 0.6B** became the default after 41 test searches over the 846-show catalog ("chemistry teacher
becomes a drug dealer" → Breaking Bad, "vets in the yorkshire dales" → All Creatures Great and Small): it put the
expected show first for 30 and in the top 3 for 34 (MRR 0.78), against nomic's 18, 27 and 0.57. It finds what nomic
missed (Stranger Things, The Sopranos' therapist, The Expanse) and keeps unrelated searches ("knitting patterns for
beginners") at 0.46 or further, where nomic let them within its cutoff. MiniLM and bge-small weren't run on this set.

An earlier comparison, on 42 test searches over a 595-show catalog (TVmaze's top 500 plus the hourly imports), MiniLM put the expected show
first for 27 and in the top 3 for 35; bge-small did so for 24 and 31, and found slightly more of the expected shows in
the top 10 (84% against 82%). Each model wins different searches: bge-small finds Dark for "time travel mystery in a
small german town" (10th against 64th) and The Mandalorian for "bounty hunter protects a child in a galaxy far away"
(31st against 213th); MiniLM finds Breaking Bad and Doctor Who from descriptions bge-small misses. With both cutoffs,
8 unrelated searches ("best mortgage rates", "replace a car battery") match nothing, apart from bge-small matching
"best mortgage rates" to two property shows. MiniLM ranks better than bge-small and runs about 2.5× faster. Changing the model re-embeds the catalog within a few minutes; until then semantic
results are limited to the shows already embedded with the new model. Any built-in setting can be overridden, e.g.
`Embeddings:Models:bge-small-en-v1.5:MaxDistance`, and another BERT-style ONNX model with a WordPiece `vocab.txt` can be
added under `Embeddings:Models:{id}`: `BaseUrl`, `OnnxSha256`, `VocabSha256`, `Dimensions`, `MaxDistance`, plus
`Pooling` (`Mean` or `Cls`), `MaxTokens`, `QueryPrefix` and `DocumentPrefix` as the model needs. Models must make
384-dimension vectors, the size of the `show_embeddings` column; services refuse to start otherwise. The sync worker embeds new and refreshed shows every 30 seconds
and keeps the vectors in `show_embeddings`, a pgvector column with an HNSW index. Postgres runs from the
`pgvector/pgvector` image for that.

**Universes** (`/universes`, and "Shared universe" on show pages) finds spin-offs, sequels, revivals, crossovers and
franchises. The 625-show dev catalog has 35, all real:
- **Shared characters:** the Marvel shows (10, with Agents of S.H.I.E.L.D., Hawkeye and Daredevil: Born Again),
  Dexter, Yellowstone, Bosch, Breaking Bad and Better Call Saul, Doctor Who and Torchwood, Frasier's revival.
- **Franchise names:** One Chicago, NCIS (7 shows), Star Trek (7), Star Wars, 9-1-1, FBI.

**Each universe's page:**
- **Timeline (the default):** one bar per show across the years, split into seasons, with today marked.
  - **Seasons:** aired seasons are solid and scheduled ones hatched; hover a season for its dates and episodes.
  - **When each started:** every later show says how long after the first it started ("Chicago P.D. · 1 year
    3 months after Chicago Fire", "Chicago Med · 3 years 1 month after").
  - **Overlap:** a line says how many ran at once ("All 3 on air together since November 2015"; for NCIS, "4 of
    these shows on air together since October 2026").
- **Watch order:** every episode of the universe's shows in the order it aired, so crossovers land in sequence.
  - **Show colours:** each row is coloured by show, with the list grouped by year.
  - **Crossover events** are marked when the shows' episodes share a title within a week. Found so far: One Chicago's
    "Infection" (Fire, then Med, then P.D.) and "In the Trenches", and NCIS's "Sister City" and "Pandora's Box"
    with New Orleans. Airing the same night isn't enough, since One Chicago airs back to back every week.
  - **Leaving shows out:** untick a show to drop it from the order; the address keeps the choice
    (`?view=order&hide=…`).
  - **Signed in:** the page tracks your place across all the shows. It shows how many aired episodes you've
    watched and the hours left, and the next episode with a "Mark watched" button. Ticking an episode marks it
    watched, and "Up to here" marks everything before it too.
  - **Opens where you are:** the year of your next episode, or the first year if you haven't started. A universe
    like NCIS has over 1,100 episodes, so only open years are drawn.
  - **API:** `GET /api/v1/shows/{id}/universe/watch-order` (public).
- **Crossover alerts** (`crossover-alerts` flag): when a crossover is coming up in a universe whose shows you follow:
  - **Notification:** two weeks ahead, once ("Crossover ahead: Reckoning. Airs across Chicago Fire, Chicago Med and
    Chicago P.D., starting in 6 days."), to followers of any of its shows, under the "new episodes" preference.
  - **Up Next:** "Crossover ahead" lists each show's part and air date, and where you are in it: caught up, so many
    to catch up on, not started or not following. The total to catch up on links to the universe's watch order.
  - **API:** `GET /api/v1/crossovers` (the next 30 days) and `POST /api/v1/crossovers` with `{ "showIds": [...] }`
    (those involving the shows), on Catalog.API, public.
- **Links:** a map of the shows with arcs joining linked ones. Colour shows the kind of link: continues the story,
  crossover, revival, or same franchise (dashed). Thickness shows how many characters they share. Selecting a show
  lists its links, with the characters and actors.
- **The address** holds the view (`?view=map`).

**How links are found:**
- **Characters:** TVmaze gives each show its own character ids, even for the same character in a spin-off, so
  characters are matched by name. The same actor playing a character with the same name counts, ignoring titles,
  nicknames and aliases ("Matt Murdock / Daredevil" matches "Matt Murdock").
  - Only scripted and animated shows count, and self roles and narrators don't.
  - A link needs two shared characters, or one with a full name.
- **Franchise names:** catch franchises whose crossovers are guest appearances, which TVmaze's main casts don't list.
  Two shows are one franchise when:
  - one title extends the other's name before a colon ("NCIS: Sydney" with "NCIS" or "NCIS: Origins"); or
  - both start with the same word and share two or more makers (creators, developers, executive producers):
    "Chicago Fire", "Chicago P.D." and "Chicago Med" share seven.

  One shared maker isn't enough: "Tyler Perry's …" shows share a producer, not a story.

**API:** `GET /api/v1/universes` and `GET /api/v1/shows/{id}/universe` (Catalog.API, public). The second includes
each show's season dates for the timeline. The universe map is cached for 30 minutes.

**Tonight's plan** (`/tonight`, linked from Home and Up Next; signed in) fills an evening from the shows you're
watching and your watchlist.
- **You choose:** the time (30 minutes to 3 hours, or any number), a mood (a chip such as "Light & funny" or
  "Mind-bending", or your own words), and Mix it up (up to three shows) or One show.
- **You get:** which shows, and how many of each one's next episodes, landing close to the time. Each pick says
  why ("Fits “Light & funny”", "You're in the middle of it", "On your watchlist").
  - Example: for 2 hours of "Light & funny", it gave two Ted Lasso episodes and two of The Patrick Star Show,
    exactly 2 hours.
  - **Swap** drops a show and plans again.
  - **Start watching** opens the first episode.
  - **I watched all of this** marks the plan watched.
  - The address holds the settings (`/tonight?minutes=90&mood=…&one=true`).
- **Tonight for two:** "Watching: With …" picks someone you follow (or "Plan tonight with …" on their page). The plan
  then comes from the shows you both follow or have started.
  - **Nobody skips anything:** it picks up after whoever is behind. Episodes the one ahead has seen are marked
    "Rewatch for …", and count for less, so a show you're level on wins.
  - **Too far apart:** a show where one of you is more than 3 episodes ahead is left out and listed below the plan.
  - **No titles:** episodes show as numbers, since the other person may not have seen them.
  - **We watched all of this** marks the plan watched for you; they mark their own.
  - Needs you to see their activity, as watching together does. Behind the `tonight-together` flag (and `social`).
  - **API:** `GET /api/v1/people/{id}/tonight?perShow=8` on Social.API.
- **How the mood is matched:** with the same vectors as search by meaning. If the embedding model is down, it
  plans from what you're watching alone and says so.
- **How it chooses:** see `TonightPlanner`.
  - **Score:** each show's score mixes its mood fit with continuity. A show watched in the last two weeks beats one
    left for months, and both beat starting something new.
  - **Choice:** a small knapsack over each show's next episodes, in order. It may run over by a few minutes (an
    episode rarely fits exactly), never more.
  - **Mixes:** in a mix, no show takes more than three-quarters of the evening, unless that would leave it under
    three-quarters full.
- **API:** `GET /api/v1/tonight/queue` (Tracking.API, signed in) gives the queue, with runtimes and the show's
  typical one where an episode has none. `POST /api/v1/shows/mood` (Catalog.API, rate-limited like search) scores
  shows against a mood.

**Where the cast went next** (on every show page, under the cast; `cast-afterwards` flag): for a show that has
ended, each main cast member's main roles in shows that premiered after the finale, earliest first; for a running show,
the roles they've taken since it started. It leads with the biggest of them (Breaking Bad: Bob Odenkirk as Jimmy
McGill in Better Call Saul) and says how many of the cast went on to another main role. Shows outside the catalog
link to a search, which imports them. Main casts only, as TVmaze lists them, so guest arcs and films don't appear;
members whose credits haven't been fetched yet are counted as pending. It works signed out.
- **API:** `GET /api/v1/shows/{id}/afterwards` (Catalog.API, public, cached an hour per show).

**Memorable lines** (on show and episode pages; `memorable-lines` flag): lines and short exchanges from
[Wikiquote](https://en.wikiquote.org), credited to it under CC BY-SA 4.0. The show page shows the three shortest
exchanges about the whole show (or, when its page has none, from the first episode with lines, which says the least),
with how many quotes there are across how many episodes. Each episode page shows up to six of its own, behind the
spoiler shield until you've watched it, and links Wikiquote for the rest. Shows without a Wikiquote page show nothing.
It works signed out.
- **Where they come from:** the sync worker finds each show's Wikiquote page through Wikidata (by TVmaze id), then
  reads the page and its season pages one at a time, ten seconds apart, most popular shows first, about a show a
  minute. Lines are refreshed every 60 days.
- **Not scripts:** scripts and transcripts are copyrighted (BBC Writersroom's, the only official ones online, forbid
  any computer analysis), so the app never fetches them. Wikiquote's quotes are chosen and kept short by its editors.
- **API:** `GET /api/v1/shows/{id}/quotes` and `GET /api/v1/episodes/{id}/quotes` (Catalog.API, public; 404 for a show
  with none).

**Who said it?** (`/who-said-it`, from Discover; `who-said-it` flag): a round of five lines from Memorable lines,
each with three or four of the show's characters to choose from. Signed in, every line comes from an episode you've
watched, and the choices are characters with lines in those episodes, so it can't spoil a scene or introduce someone
you haven't met. After each answer it says which episode the line is from. Signed out, or before any of your shows
have lines, it uses popular shows' first episodes instead, and says so.
- **API:** `POST /api/v1/quiz/who-said-it` (Catalog.API, public) with `episodeIds`, the watched episodes; the page
  gathers them from Tracking for up to 20 started shows, so the catalog never stores watches.

**TV atlas** (`/atlas`, from Discover; and "Where it's set" on show pages; `tv-atlas` flag): where shows are set, on a
world map. Signed in, it maps the shows you follow, with how many of them are on the map, the places with the most
shows, and your TV passport (the countries your shows have taken you to); "Popular shows" maps the catalog's 150 most
popular instead, and is what signed-out visitors see. Select a dot to see the shows set there. "Filmed in" maps where
the same shows were filmed instead, and **Stand-in cities** lists the filming places that play somewhere else (Atlanta
playing Indiana for Stranger Things). A show page says where it's set and where it was filmed, with a small map (dots
for settings, rings for filming places) and a line for each stand-in.
- **Where it comes from:** Wikidata's narrative and filming locations for each show (found by TVmaze id), kept when the
  place has coordinates on Earth. The sync worker looks shows up 100 at a time, most popular first, and again monthly. Plenty of
  shows have none (17 of a sample of 30 popular ones did), and those simply aren't on the map.
- **The map** is drawn from Natural Earth's public-domain land outlines, shipped with the app, so it loads no map tiles
  from anyone.
- **API:** `GET /api/v1/shows/{id}/settings` and `POST /api/v1/atlas` with `showIds` (Catalog.API, public; the page
  sends the user's follows, so the catalog never stores them).

**Episode lengths** (on show pages, and `/episode-lengths` from Discover; `episode-lengths` flag): how long a show's
episodes run, season by season. The show page draws a bar per season (its typical episode, in minutes) and says
whether episodes got longer, shorter or kept steady, and which season changed most. The Discover page lists the scripted
shows whose episodes grew and the ones that were trimmed, first season against latest, most popular first. It works signed out.
- **The rule:** each season's median runtime, so a long finale or special doesn't move it; seasons with fewer than
  three timed episodes are drawn faded and left out of the verdict. Longer or shorter means a fifth or more and at
  least eight minutes. Daily shows are left out.
- **API:** `GET /api/v1/shows/{id}/episode-lengths` (404 with fewer than two seasons to compare) and
  `GET /api/v1/episode-lengths` (Catalog.API, public).

**Between seasons** (on show pages, and `/season-gaps` from Discover; `season-gaps` flag): how long a show kept its
viewers waiting. The show page draws each wait between seasons as a bar, says how far apart seasons usually came and
which wait was longest, and for a running show how long it's been since the last episode and when it's back, if dated.
The Discover page lists popular scripted shows' longest waits (under five years), the shows revived after five years or
more, and the running shows with nothing new for a year or more. It works signed out.
- **The rule:** a wait runs from a season's last episode to the next season's first, from TVmaze's air dates; a next
  season with a date still to come counts as a scheduled wait. Daily shows are left out.
- **API:** `GET /api/v1/shows/{id}/season-gaps` and `GET /api/v1/season-gaps` (Catalog.API, public).

**Cast ages** (on show pages, and `/cast-ages` from Discover; `cast-ages` flag): how old the main cast was at a show's
latest episode (its finale, once it's ended), as bars, with the cast's median age and its youngest and oldest member; and
who grew up on screen (Stranger Things' Millie Bobby Brown, 12 → 21). The Discover page lists popular scripted shows
where two or more of the cast grew up on screen, and the youngest and oldest casts (Star Wars: Skeleton Crew's median
14, Slow Horses' 66). It works signed out.
- **Grew up on screen:** 6 to 15 at the first episode and 18 or over by the latest, in a run of up to 15 years.
- **A known gap:** TVmaze lists today's main cast without saying when anyone joined, so a later arrival can look like a
  child at the premiere (Kaley Cuoco joined Charmed in its last season). The run cap rules out most such cases, the list
  needs two per show, and both pages say so.
- **API:** `GET /api/v1/shows/{id}/cast-ages` and `GET /api/v1/cast-ages` (Catalog.API, public).

**Characters' names** (`/names`, from Discover; `character-names` flag): type a first name to find the TV characters who
share it (Jesse: Jesse Pinkman, Jesse Custer, Jesse Katsopolis and 18 more), how common it is (161st), how many there
were in each decade by the show's premiere, and who has it as a surname. Without a name, the page lists the most common
women's and men's names and each decade's top five. John is first by far (243 characters), three times Sarah, the
most common woman's name. Each result links to its show page when the show is in the catalog. It works signed out.
- **Where the names come from:** the main casts of shows across TVmaze (the actors' cast credits), plus the catalog's
  casts; about 21,000 characters with a first and last name. Titles ("Detective", "Dr.", "Lady"), nicknames in quotes
  and self roles are left out, and a one-word role ("Bartender") isn't counted, since it can't be told from a name.
- **Women's and men's names** go by the actor's gender on TVmaze, so a voice role can land on the other side.
- **API:** `GET /api/v1/character-names` and `GET /api/v1/character-names/{name}` (Catalog.API, public).

**Title formulas** (on show pages, and `/title-formulas` from Discover; `title-formulas` flag): shows whose episode
titles follow a pattern, found in TVmaze's titles rather than listed by hand. Friends' "The One…" (234 of 236), Monk's
"Mr. Monk…", Scrubs' "My…", Stranger Things' and The Mandalorian's chapters, 24's "Day…", and The Big Bang Theory's
"The…" (278 of 279). The show page gives the formula, the first three titles that follow it, and the ones that break it
(Friends: only the finale, "The Last One"); titles you haven't watched stay behind the spoiler shield. The Discover
page lists scripted and animated shows by kind: the same opening, chapters and parts, and always the same article. It
works signed out.
- **The rule:** the most common opening of one to three words. A phrase counts from two in five titles, one word from
  three in five, and "The" (or another language's article) from nine in ten. Placeholders such as "Episode 5" and
  "TBA" are left out, as are daily shows, and a show needs six real titles.
- **API:** `GET /api/v1/shows/{id}/title-formula` (404 when there's none) and `GET /api/v1/title-formulas` (Catalog.API, public).

**Six degrees** (`/degrees`, from Discover; `six-degrees` flag) finds the shortest chain of actors between any two
shows: someone from the first was in a second show with someone from a third, and so on, up to six actors. Game of
Thrones to The Office is two (Aidan Gillen to The Wire, Amy Ryan to The Office); Breaking Bad to Friends is three, through
two shows the catalog doesn't have yet (each is marked, with a search to add it). "Challenge me" picks two popular shows
that connect, says how many steps, and keeps the chain hidden until you ask. It works signed out.
- **Main casts only:** TVmaze lists a show's regulars, not guest roles, so chains run longer than the real world's, and
  reality, game and talk shows (whose casts play themselves) link to nothing. Of 40 random pairs of popular shows, 27
  connected (2 in one step, 8 in two, 17 in three, 1 in four); the 12 that didn't involved a reality or game show, or an
  anime whose cast has no other catalog credits.
- **API:** `GET /api/v1/degrees?from={id}&to={id}` (Catalog.API, public): the chain, 204 when none within six, 404
  for an unknown show. `GET /api/v1/degrees/challenge`: two shows and their distance.

**Guess the show** (`/puzzle`, from Discover; `show-puzzle` flag) is a daily puzzle: one popular show, the same for
everyone, found in six guesses. Each guess is compared with the answer: when it premiered (earlier or later, amber within
3 years), its network (amber for the same streaming service), the genres they share, scripted or animated, seasons (amber
within one), running or ended, and actors in both casts. Each wrong guess adds a clue: the decade, the network, one of its
characters, then its lead. A finished puzzle gives a spoiler-free square per guess to share, and your record (played,
solved, days in a row) is kept on the device. It works signed out.
- **API:** `GET /api/v1/puzzles/{date}` (Catalog.API) gives the day's number and first clue; `POST
  /api/v1/puzzles/{date}/guesses` (`{ showId, attempt }`) compares a guess, returns the clues unlocked, and the answer
  once it's solved or the sixth guess is in. Yesterday, today and tomorrow by UTC are playable, so every time zone has
  its own today.

**Mixtape** (`/mixtape`, from Discover and Blend; `mixtape` flag) is Blend for three to five shows: the shows that fit
all of them, not just one. Breaking Bad + Ozark + Better Call Saul gives The Lincoln Lawyer, Bosch and Criminal Minds;
Stranger Things + Severance + Black Mirror gives Travelers, Westworld and The Expanse; The Office + Superstore + Abbott
Elementary gives Seinfeld, The Good Place and Brooklyn Nine-Nine. Each pick shows how close it is to each show (a bar in
the shows' colours), the one it's closest to, and a genre it takes from one of them; shows you follow say so. The
address holds the shows, so a mixtape can be shared, and it works signed out.
- **How:** it averages the shows' search vectors, gathers candidates nearest the average and nearest each show, and
  ranks them by their distance to the farthest of the shows. Picks must be the same kinds of show as the mixtape's
  (three scripted shows give scripted picks).
- **API:** `GET /api/v1/shows/mixtape?ids=a,b,c` on Catalog.API.

**Blend** (`/blend`, and "Blend … with another show" on every show page) finds the shows that sit between two others:
Breaking Bad × The Office gives The Gentlemen, Brooklyn Nine-Nine and BEEF; NCIS × Friends gives Scrubs and
Brooklyn Nine-Nine. A slider leans the blend towards either show. Each match shows where it sits between the two and the
genres it takes from each. The address holds the blend, so it can be shared, and it works signed out.
- **How:** it mixes the two shows' search vectors, takes the 40 catalog shows nearest the mix, and ranks them by
  their distance to the farther of the two, so a show close to only one of them ranks low.
- **Loose blends:** when even the best match is farther from one show than search's cutoff for "similar"
  (Stranger Things × The Great British Bake Off), the page says the two have little in common.
- **API:** `GET /api/v1/shows/blend?a={id}&b={id}&lean=50` (lean 0-100, how much like the first show). The pickers use
  `GET /api/v1/shows/suggest?q=`: up to 8 catalog title matches, without the TVmaze import search does, so it's cheap
  enough to call as you type.

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

**Watch on Netflix:** a streaming show's page has a "▶ Watch on Netflix" button (and the same link under Where to watch)
that goes straight to the show on the service: `netflix.com/title/…`, `tv.apple.com/show/…`, `hbomax.com/show/…`,
`primevideo.com/detail/…`, Disney+, Hulu, Peacock and others. On a phone, the OS opens these in the service's app when
it's installed (iOS universal links, Android app links); the MAUI app hands external links to the OS. Where TVmaze has
no page for the show on the service, the link searches the service for it ("Find it on Netflix") when the service has a
search that works signed out, else goes to its home page. A show on a network whose official site is a streaming
service's show page is listed under that service (House of the Dragon on HBO Max). Only a known service's show-page
addresses count as the show's page (`netflix.com/title/…`, not `netflix.com/browse` or a network's own site). The detail
API's `watchProviders[].link` says which (`WatchLink`: site, search or title). Shows pick up their links on their next
refresh. The button is behind the `watch-now` flag.

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
| flagd (feature flags) | `src/TvTracker.AppHost/Flags/flagd.json`; see [Feature flags](#feature-flags) |
| DbGate (browse and query every database) | link on the Aspire dashboard (`dbgate`), already connected to Postgres |

### Backing up development data

`scripts/backup.sh` saves every Postgres database (`pg_dump`, one file each) and Keycloak's `tvtracker` realm with its
users to `backups/<UTC timestamp>` (git-ignored); `scripts/restore.sh <dir>` puts them back. Either takes
`--databases-only` or `--keycloak-only`.

- **Users keep their ids:** Keycloak's own export carries each user's id and password hash, and every service keys its
  data on that id. Re-creating a user by hand gives a new id, and their follows, watches and ratings no longer belong
  to anyone.
- **Keycloak is stopped for a few seconds** during a backup, since its embedded database is held open while it runs;
  sign-in is unavailable meanwhile.
- **Restore with the AppHost stopped** (the script refuses otherwise): services would write over a database mid-restore.
  Each database's objects are replaced in one transaction. Keycloak's current database is moved aside in its volume
  (`h2-replaced-<timestamp>`), never deleted, and the realm imported into a fresh one.
- **A corrupted Keycloak** ("File corrupted while reading record" at start) was the reason for these: after a Docker
  crash its H2 file wouldn't open. Keycloak's export could still read it, and a restore of that export brought every
  user back with their id.

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

### Load testing

`tvtracker-load` (`src/Tools/TvTracker.LoadGen`) generates load against a running AppHost. Load-test users sign in
through Keycloak and use the app through the Mobile BFF, so the gateway and every service see realistic traffic: the
Aspire dashboard's metrics and traces fill up as they would with real users.

```bash
cd src/Tools/TvTracker.LoadGen
dotnet run -- setup --users 50                 # once: a load-test Keycloak client and users load-0001 … load-0050
dotnet run -- run --users 50 --duration 5m     # browse for 5 minutes
dotnet run -- run -u 20 -d 2m --think 200ms --writes --csv run.csv   # busier, and follow, watch and rate
dotnet run -- cleanup                          # delete the users and everything they did
```

- **What users do:** each signs in, then visits pages until the run ends, each page a burst of the requests the app
  makes for it: a show page (seven services), home, an episode, a search, Discover, the feed, stats. Shows come from
  the catalog's trending list and a few genres. `--writes` adds following, marking watched and rating, and now and then
  unfollowing so long runs stay bounded.
- **Pacing:** `--ramp-up` (default 10s) starts users evenly; `--think` (default 1s, ±50%) is the pause between pages;
  `--think 0` runs flat out.
- **Output:** a progress line every 5 seconds (req/s, p95, errors), then a table per endpoint: count, req/s, errors,
  429s, 404s and p50/p95/p99/max. Errors are 5xx and failed connections; 429s (the services' per-user rate limits) and
  404s (a feature flag off, "not following") are counted apart. The exit code is 2 when there were errors.
- **Cleanup** signs each user in and deletes the account the way Settings does, so every service drops their data.
- **Development only:** `setup` uses Keycloak's console admin (`--admin-password`, default `admin`) and creates a
  `tvtracker-loadgen` client that accepts passwords directly.
- A first run on a laptop-class sandbox: 5 users at 300 ms think time with writes gave about 55 req/s, p95 ~110 ms
  once warm (the first seconds are slower while caches fill), and no errors.

### MCP server (internal queries)

`mcp-server` (http://localhost:5112/mcp) is a read-only [MCP](https://modelcontextprotocol.io) server over the
catalog, Analytics and saved research, so an MCP client can answer questions like "landman insights" or "hidden gems in drama" from
the running app. Add it to Claude Code with:

```bash
claude mcp add --transport http tvtracker http://localhost:5112/mcp
```

| Tool | Answers |
| --- | --- |
| `show_insights` | Everything computed about a show: season ratings, top episodes, consistency and arc, when it gets good, the landing, DNA, tags, forecasts, viewing |
| `show_details` | The catalog record: summary, status, genres, nanogenres, seasons, cast and creators (with person ids), next and latest episode, where to watch |
| `search_shows` | Search by title or description, as in the app (imports from TVmaze when missing) |
| `season_episodes`, `show_universe`, `holiday_episodes` | A season's episodes; the franchise a show is in; a holiday's best-known episodes |
| `show_research` | The show's saved research: the checked insight with its sources, and the checked facts |
| `discover_list`, `upcoming_premieres`, `top_episodes` | Discover's lists, premieres with catch-up time, best-rated episodes |
| `person`, `person_insights` | A person's credits; their insights and, for creators, their fingerprint |
| `network_reports` | Each network's report card (endings, renewals, waits, best shows) against the whole catalog |

- **Shows by name:** every show argument takes a title ("landman") or an id. A title goes through catalog search
  and takes the exact match, else the most popular.
- **Nothing personal:** it signs in to Analytics as its own Keycloak client (`tvtracker-mcp`, client credentials), so
  there is no user, and only figures that already follow the 5-user rule come back. Image URLs are left out.
- **Access:** development only, with no sign-in. Set `Mcp:ApiKey` to run it anywhere else; callers then send it as
  `Authorization: Bearer <key>`.

**MCP gateway** (`mcp-gateway`, http://localhost:5113/mcp): one endpoint in front of the app's MCP servers, which is
what Open WebUI uses.
- **Open WebUI:** the AppHost adds the gateway as an MCP tool server called after the app ("TV Tracker"), on for
  everyone. In a chat, turn it on from the tools button, then ask "landman insights". A chat model has to be pulled
  first (the "Pull …" command on `model-runner`); small models call tools less reliably.
- **Key:** every caller sends `Authorization: Bearer <Gateway:ApiKey>` (`tvtracker-gateway-dev-key` in development):
  `claude mcp add --transport http tvtracker-gateway http://localhost:5113/mcp --header "Authorization: Bearer tvtracker-gateway-dev-key"`.
- **More servers:** add `Gateway:Servers:{name}:Url` (and optionally `BearerToken`, and `Tools` to expose only some)
  to the AppHost's configuration; their tools join the same list. Tools keep their names unless two servers share one,
  which then become `{server}_{tool}`.
- **Logs:** every call is logged with its server, outcome and time (the `mcp-gateway` resource on the dashboard). A
  server that's down is left out of the list until it answers, and a failed call reconnects and retries once.

### Ops dashboard

http://localhost:5102/ops (Catalog.API) is a one-page developer dashboard, refreshed every 10 seconds:
- **Catalog:** what it holds (shows by status, episodes, people and credits), and the semantic index (current, stale, missing).
- **Sync:** shows due for refresh and when the last sync ran.
- **Analytics:** insights computed and when, counts of users, ratings, watches and follows, top episodes, upcoming premieres
  and the shows with the most shared cast.
- **Backfills:** cast credits fetched this month (the reunion radar's data), shows with their makers, and careers.
- **Wikimedia:** Memorable lines (quotes stored, shows read within 60 days, Wikiquote pages found) and the TV atlas
  (shows looked up this month, with a setting, with a filming place, places). Wikimedia rate-limits shared addresses
  hard, so a count that stays put usually means a job is waiting out a 429.
- **Discovered structure:** nanogenres (clusters and pairings, shows covered, the largest), universes (the largest),
  and characters the same actor plays across catalog shows (character trails).
- **Computed insights:** shows with DNA, creator fingerprints, forecasts, "when it gets good" turns, season verdicts out
  now, and the top of Worth the wait.
- **Research and curation:** shows with a trusted record, versions, sources and drafts running (Research.API); catalog
  overrides by field (accepted corrections and saved research); and episode notes (essential, skippable).
- **Actors, shows and genres:** the most-credited actors, recently added shows (and whether they're indexed yet), and genres.
- **Bulk import:** the latest bulk import job.
- **Data quality:** how much of TVmaze's data is missing (unrated episodes, runtimes, placeholder titles, numbering,
  genres), whether Analytics' copy of the catalog is in step, and whether its copy of the episode notes matches
  Research's (with any note events still waiting in Research's outbox).
- **Feature flags:** every flag, on or off, as flagd answers a signed-out caller.

Catalog.API gathers the other services' sections server-side (`/api/v1/ops/stats` on Analytics.API and Research.API,
`/api/v1/imports/bulk/current` on the sync worker); a service that's down just leaves its section empty. Since it
isn't signed in, it and the stats endpoints are only mapped in Development.

### Feature flags

Blend, Universes, Essential cut, Tonight's plan (and planning it for two), Reunion radar, the Show DNA panel, DNA twins, When it gets good, Creator fingerprint, the TV time machine, Does it stick the landing?, Previously on…, the catch-up planner, Is it worth going back?, Backlog triage, Fits your time off, the spoiler shield, Where do I know them from?, Holiday episodes, Research, episode notes, Ask, the season verdict, the character trail, the watch-now button, crossover alerts, the weekly digest, taste twins, the awards shelf, network report cards, Year in TV, mixtapes, the worth-it score, Guess the show, Call it, Six degrees, Your TV life, Where the cast went next, Spoiler watch, Memorable lines, Who said it?, Your TV clock, the TV atlas, Episode lengths, Between seasons, Cast ages, Characters' names, Title formulas, episode duels, the waiting room, Social (following and the feed), the forecasts and nanogenres sit behind [OpenFeature](https://openfeature.dev) feature flags,
served by [flagd](https://flagd.dev) (the `flagd` resource on the Aspire dashboard). Flags live in
`src/TvTracker.AppHost/Flags/flagd.json`, and an edit applies while everything runs, with no restart.

| Flag | Gates |
| --- | --- |
| `blend` | `/blend`, its Discover card (and nav link when signed out), the "Blend … with another show" link on show pages |
| `universes` | `/universes` and universe pages, its Discover card (and nav link when signed out), and the "Shared universe" section on show pages |
| `essential-cut` | `/shows/{id}/essential-cut` and its teaser on show pages |
| `tonight-plan` | `/tonight` and its links on Home and Up Next |
| `tonight-together` | Tonight for two: the "Watching" choice on `/tonight`, the link on people's pages, and its endpoint |
| `reunion-radar` | `/reunions` and its link on For you |
| `show-dna` | The Show DNA panel on show pages (recommendations use DNA either way) |
| `dna-twins` | "Made like this, about something else" on show pages, and its endpoint |
| `endings` | "Does it stick the landing?" on ended shows' pages, and the Great endings list on Discover |
| `time-machine` | `/time-machine`, its teaser on Home and link on Insights, and its endpoint |
| `creator-fingerprint` | The "Creator fingerprint" section on person pages, and its endpoint |
| `gets-good` | "When it gets good" on show pages, its line in "Will you finish it?", and the Worth the wait list on Discover |
| `nanogenres` | Nanogenre chips on show pages, `/nanogenres`, the search filter and their endpoints (recommendations use them either way) |
| `forecasts` | "What's next" on show pages, "Will you finish them?" on Stats, and the finish-outlook endpoints |
| `known-faces` | "You know them from" on show pages' cast, and its endpoint |
| `holiday-episodes` | `/holidays`, its line on Home when a holiday is near and its link on Insights, and its endpoints |
| `season-verdict` | The season verdict on show pages, Insights' "Seasons that just ended", its endpoint and its notifications |
| `research` | The Research section on show pages, `/research` and its pages, the Settings link, and Research.API's endpoints |
| `ask` | `/ask`, its Insights card, and Assistant.API's endpoints |
| `character-trail` | Character links on show pages' cast, the trail page, and its endpoint |
| `episode-notes` | Episode notes on the research page and the essential cut, and Research.API's notes endpoints |
| `watch-now` | The "▶ Watch on Netflix" button on show pages |
| `crossover-alerts` | Crossover notifications, "Crossover ahead" on Up Next, and the crossovers endpoints |
| `weekly-digest` | The weekly digest email, its Settings section and preview |
| `taste-twins` | `/taste-twins`, its For you link, and its endpoint |
| `awards-shelf` | Awards on show pages, `/prestige` and its Discover link, and the awards endpoints |
| `network-reports` | `/networks`, its Discover link, and its endpoint |
| `year-in-tv` | `/year`, its Stats link, and its endpoint |
| `mixtape` | `/mixtape`, its Discover card and Blend link, and its endpoint |
| `your-services` | Logos and prices on network report cards, `/services`, and its endpoints |
| `family-plan` | The Family plan's invites and members: `/settings/family`, `/family/join/{token}`, and their endpoints |
| `list-links` | A list's link (`/l/{token}`), its section on the list page, and its endpoints |
| `plans` | Plans: Free's limits on lists (3, no sharing), `/settings/plan` and the admins' `/settings/plans`; off, nobody is limited |
| `worth-it` | "Is it worth it?" on show pages, its highlight, and the Worth your time list on Discover |
| `episode-duels` | `/shows/{id}/duels`, its show page link, and Ratings.API's duels endpoints |
| `waiting-room` | `/waiting`, its Up Next link, and its endpoint |

- **Turning one off:** set its `defaultVariant` to `"off"`.
  - The UI then hides its links and sections, and its pages say "Not available".
  - Its API endpoints answer 404.
  - The UI keeps an answer for up to 30 seconds.
- **For some users only:** add flagd `targeting`. The context has `targetingKey` (the user's Keycloak id) and `email`. For example, to keep Universes from one user:

  ```json
  "targeting": { "if": [ { "==": [ { "var": "email" }, "demo@example.com" ] }, "off" ] }
  ```

  flagd's `fractional` operator does percentage rollouts on `targetingKey`.
- **If flagd is down:** every flag is on, so a flag server outage never takes features away.
- **From the app:** admins get Settings → Admin → Feature flags (`/settings/flags`). Each flag can follow
  flagd.json, or be turned on or off for everyone.
  - **Admins:** the Keycloak usernames in `Admin:Users` in the AppHost's `appsettings.json`: the `admin` account
    (admin / admin) by default. For anyone else, demo included, the page and its API answer 404.
  - **The admin account** is in the realm file; Identity.API also creates it at startup when it's missing (a realm
    imported before it was added). To make someone else an admin, add their username to `Admin:Users`.
  - **Where changes go:** `Flags/overrides.json`, which flagd reads after flagd.json, so an override wins. It isn't
    checked in, and flagd.json is never edited. An override applies to everyone, ignoring flagd.json's targeting;
    "flagd.json" removes it.
  - **How fast:** services within seconds; the UI keeps answers for 30 seconds (the admin's own at once).
  - **Only in development:** Identity.API writes the file in the AppHost's Flags folder, which is bind-mounted into
    flagd, so it needs the two on one disk.
  - **API:** `GET /api/v1/admin/flags` and `PUT /api/v1/admin/flags/{key}` (`{ "on": true | false | null }`) on
    Identity.API.

### Plans

Free, Plus and Family, behind the `plans` flag. The plumbing for paid features is in place; payment isn't yet (BACKLOG F1).

- **Free:** up to 3 lists. **Plus:** as many lists as you like (up to the service's cap of 100), and Your services.
  Every plan can share lists, on its profile and by link; sharing was Plus-only at first and opened to everyone, since
  shared lists bring people in.
- **Family** (`family-plan` flag): Plus for the owner and up to 5 people they invite, on one subscription.
  - **Inviting:** `/settings/family` gives the owner an invite link (`/family/join/{token}`), which they can share or
    turn off. Whoever opens it signs in (or signs up) and joins while there's room.
  - **Members** get Plus's benefits while the owner pays for Family, with their own lists, ratings and watches; the
    owner sees only their names. A person is in one family at most; an owner with members can't join another.
  - **Leaving:** a member can leave, the owner can remove them, and a family ends when its owner stops paying for it or
    deletes their account. Anything made on the plan stays, as with any downgrade.
  - **API** (Identity): `GET /api/v1/me/family`, `POST`/`DELETE /api/v1/me/family/invite`,
    `DELETE /api/v1/me/family/members/{userId}`, `DELETE /api/v1/me/family/membership`,
    `GET /api/v1/family/invites/{token}` and `POST /api/v1/family/join` (`{"token":…}`; 409 with the reason when the
    family is full, ended, or you're in another).
- **Where it shows:** the lists page says "2 of 3 lists on Free" and turns Create off at the limit, and `/settings/plan`
  compares the plans and shows yours. Sharing still goes through the plan's `ShareLists`, so a plan can take it away
  again without new plumbing.
- **Nothing is taken away:** lists made before the limits, or while on Plus, stay, shared ones stay shared, and a free
  user can still edit, unshare or delete them. Only a new list or a new share is refused.
- **Setting a plan:** admins (`Admin:Users`) search people and pick a plan on `/settings/plans`. A change reaches
  Tracking within seconds.
- **API:**
  - `GET /api/v1/me/plan` (Identity) gives the caller's plan and its limits.
  - `GET /api/v1/admin/plans/users?q=` and `PUT /api/v1/admin/plans/users/{userId}` (`{"plan":"plus"}`) are for
    admins (404 for anyone else).
  - `GET /api/v1/lists/limits` (Tracking) gives what the lists endpoints enforce.
  - Past a limit, Tracking answers 403 with a problem of type `plan-limit` naming the plan that allows it.

### List links

A list's page has **Get a link** (behind the `list-links` flag): anyone with the link (`/l/{token}`) sees the list,
read-only, without signing in or having an account.

- **What it shows:** the list's name, description and shows (each linking to its show page), not who made it.
- **Separate from profile sharing:** a private profile or a block doesn't stop a link; sending it is the owner's choice.
- **Turn off link** stops it working at once; a new link has a new address. Deleting the list or the account ends it too.
- **Share link** opens the share sheet on a phone and copies the link elsewhere.
- **The address** is the web app's own on the web; the phone uses `MobileSettings.WebAddress` (this machine in
  development, so set it to the public address for a release).
- **API:** `POST` / `DELETE /api/v1/lists/{id}/link` (owner), and `GET /api/v1/linked-lists/{token}` (anyone) on Tracking.
  The token is 128 random bits, so links can't be guessed.

### Navigation

- **Main sections only:** signed in, the top navigation is Home, Up Next, My shows, Calendar, Discover, For you,
  Insights, Feed, Search and Inbox. Each section's tools are cards at the top of its page:
  - Up Next: backlog, waiting room, Tonight's plan and time off.
  - My shows: lists.
  - Discover: Prestige picks, Blend, Mixtape, Guess the show, Six degrees, Universes, nanogenres and network report cards.
  - For you: reunion radar and taste twins.
  - Insights: Ask, the time machine and holiday episodes.
  - Stats: Year in TV.
- **On a phone** the cards show only their names, two to a row, so the page's own content starts on the first screen.
- **Signed out,** the navigation is Home, Calendar, Search, Discover, Blend and Universes.

### White label and theme

- **App name:** `Branding:AppName` in the AppHost's `appsettings.json` (default "TV Tracker"), or the
  `Branding__AppName` environment variable when starting the AppHost. It names the header, page and browser titles,
  "… users" in insights and Discover, the ops page, and the title of a push notification without one. The AppHost
  passes it to the web app, Catalog.API, Analytics.Worker and Social.API.
  - **Phone:** `MobileSettings.AppName` in `src/Clients/MobileApp/Services/MobileSettings.cs`, and the home-screen name
    in `ApplicationTitle` in `MobileApp.csproj`.
  - **Not covered:** the Keycloak sign-in page, whose title is the realm's `displayName` in
    `Realms/tvtracker-realm.json` (imported once, so change an existing realm in the Keycloak admin console).
- **Theme:** the ☀/☾ button in the header switches between light and dark. Settings → Theme also offers Same as
  device (the default). On a phone-width screen the header drops its Sign out button to fit one row; Sign out is in
  Settings. Kept per device or browser, and applied before the page
  first paints, so a saved choice doesn't flash the other theme. Available signed out too.

### Mobile app

The mobile app isn't started by the AppHost. With the AppHost running, run `src/Clients/MobileApp` from Visual Studio,
VS Code (C# Dev Kit + .NET MAUI extension) or Rider on an Android emulator, iOS simulator, Mac or Windows.

- It talks to the Mobile BFF and Keycloak on `localhost` (`10.0.2.2` from the Android emulator); see `Services/MobileSettings.cs`.
- Sign-in opens Keycloak in the system browser and returns via `tvtracker://callback`.
- Deep links: `tvtracker://shows/{id}` opens a show.
- **Building on Linux** (Android only; iOS and Mac Catalyst need a Mac):
  ```bash
  dotnet workload install maui-android
  USER=$(whoami) dotnet build src/Clients/MobileApp -t:InstallAndroidDependencies -f net10.0-android \
    -p:AndroidSdkDirectory=$HOME/android-sdk -p:AcceptAndroidSDKLicenses=True
  dotnet build src/Clients/MobileApp -f net10.0-android -p:AndroidSdkDirectory=$HOME/android-sdk
  ```
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
| TvTracker.LoadGen | `src/Tools` | `tvtracker-load`: load-test users browsing (and tracking) through the Mobile BFF, with a per-endpoint report |
| Catalog.Embeddings | `src/Services/Catalog` | Local text embeddings (all-MiniLM-L6-v2 via ONNX Runtime) for semantic search |
| Catalog.SyncWorker | `src/Services/Catalog` | Imports/refreshes shows from the provider (including search matches for Catalog.API), embeds shows for semantic search, detects changes, publishes events, detects aired episodes |
| Tracking.API | `src/Services/Tracking` | Follows, statuses, watched episodes, progress, Up Next, calendar, lists, stats |
| Ratings.API | `src/Services/Ratings` | Ratings, summaries, spoiler-safe comments, reactions, viewers' tips, flags, corrections and moderation |
| Recommendations.API | `src/Services/Recommendations` | Similar shows, personalised picks (content + show DNA + collaborative), dismissals |
| Social.API | `src/Services/Social` | Following people, the activity feed, people search and pages, the "share my activity" setting |
| Research.API | `src/Services/Research` | Moderators' research: sources (Wikidata, Wikipedia, TMDB, pages), facts, a model-drafted insight, saved versions; saved facts override the catalog |
| Assistant.API | `src/Services/Assistant` | "Ask the app": Model Runner's chat model answers questions by calling the MCP tools through the gateway |
| Notifications.API | `src/Services/Notifications` | Preferences, quiet hours, devices, inbox, push / web push / email delivery |
| Analytics.Worker / Analytics.API | `src/Services/Analytics` | Insights for signed-in users: best and worst seasons, top episodes, season trends, episode patterns, run facts, premiere build-up, shared cast, discovery lists, actor insights, your taste |
| Mcp.Server | `src/Services/Mcp/Mcp.Server` | Read-only MCP tools over Catalog, Analytics and Research for internal queries |
| Mcp.Gateway | `src/Services/Mcp/Mcp.Gateway` | One MCP endpoint with an API key in front of the MCP servers; Open WebUI's tool server |
| Mobile.Bff | `src/ApiGateways/Mobile.Bff` | YARP gateway for the mobile app (`/catalog/...`, `/tracking/...`, ...) |
| TvTracker.UI | `src/Clients/TvTracker.UI` | Every page and component, shared by web and mobile |
| WebApp | `src/Clients/WebApp` | Blazor web app; OIDC sign-in, tokens kept server-side in Redis, browser push |
| MobileApp | `src/Clients/MobileApp` | MAUI Blazor Hybrid app; OIDC + PKCE, SQLite offline store, deep links |

How events flow:

- **Catalog.SyncWorker** publishes `ShowAdded`, `ShowUpdated`, `EpisodesScheduled` (one per show sync, up to 500 episodes each), `EpisodeAired`, `SeasonAnnounced`, `ShowStatusChanged`.
- **Tracking.API** publishes `ShowFollowed`, `ShowUnfollowed`, `EpisodeWatched`, `EpisodeUnwatched`, `ShowCompleted`.
- **Ratings.API** publishes `ShowRated`, `EpisodeRated`, `ShowRatingRemoved`, `EpisodeRatingRemoved`. **Identity.API** publishes `UserRegistered`, `UserProfileUpdated`, `UserDeleted`, and a daily `UserProfileSnapshot` per user.
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
  - **Discover page** (`/discover`, its own menu item) — discovery lists, overall or by genre, 30 shows each. The
    list and genre are in the address (`/discover?list=bingeable&genre=Drama`), so a list can be shared:
    - hidden gems: rated 7.5+, outside the catalog's most popular 40% on TVmaze, and few of our users follow them;
    - most consistent and biggest rollercoasters, among well-rated shows;
    - best season finales;
    - great endings: well-rated (7.5+) ended shows that stick the landing, most popular first;
    - worth the wait: well-rated shows (7.5+) with a slow start that rate at least 0.4 higher from the episode where
      they get good, most popular first (Person of Interest, Dark, Slow Horses, Severance).
    - worth your time: shows whose worth-it score says worth it (75+), best first.
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
  - **Essential cut** (`/shows/{id}/essential-cut`, linked from long shows' pages): a shorter way through a long
    show, from its episode ratings.
    - **What it keeps:** each season keeps its premiere, finale, best episode and both halves of two-parters.
    - **What it skips:** the season's lowest-rated episodes that sit clearly below its average, so a weak season
      is thinned rather than skipped whole, and an evenly rated one loses little.
    - **Levels:**
      - **Light:** up to 15% of a season, rated a full spread below its average.
      - **Lean:** up to 30%, half a spread below.
      - **Essentials:** up to 50%, a quarter below.
    - **On One Piece** (1,180 rated episodes) it cuts 510 hours to 443, 376 or 324. Without any filler data, it skips
      53%, 70% or 78% of the canonical filler. At "light" a skipped episode is filler 3.6× as often as an average one.
      Breaking Bad loses 10% at "light".
    - **The page:** hours and episodes saved, the biggest runs to skip ("S2007 E35–E44, rated 7.2 against the
      season's 8.6") and a chip per episode.
    - **Episode notes** (`episode-notes` flag): moderators mark episodes essential or skippable on the research page,
      with a reason viewers see and a source. The cut follows them first, at every level: an essential episode is
      always kept, and a season with any skippable episode is cut by its marks alone. "From our moderators" lists them,
      and a show with notes can be cut even when too few of its episodes are rated. Backlog triage and the catch-up
      planner use the same cut.
    - **From where you are:** for a show you've started, it shows the next episode the cut keeps and what's left in full and
      in the cut.
    - **Spoiler-safe:** episode titles stay hidden unless you ask for them.
    - **Ratings:** TVmaze's, averaged with our users' once 5 rate an episode.
    - **Which shows:** it needs 20 or more aired episodes, 80% of them rated.
    - **API:** `GET /api/v1/shows/{id}/essential-cut?level=lean` on Analytics.API, signed in, computed on request.
  - **Previously on…** (show page, signed in; "Back after a break" on Up Next): a short rewatch for coming back to
    a show after a break of 4 months or more, when there's more to watch or a new season or episode is scheduled.
    - **What it picks:** only episodes you've watched, so nothing new is given away. Where you left off, that season's
      premiere and the episode before you stopped, then the season's best, last season's finale and best.
    - **Your ratings first:** an episode you rated counts by your stars (×2), others by TVmaze's rating.
    - **Time:** 1, 2 or 4 hours (a chip each). Picks are taken in that order while they fit, with Tonight's plan's
      small overrun; where you left off is always in. Two-parters come together.
    - **Context:** when you last watched and what, and the season premiere it's for ("Season 3 starts in 16 days"), or
      what's still to watch.
    - **Up Next:** followed shows due one, the ones coming back soonest first, each linking to its refresher.
    - **API:** `GET /api/v1/shows/{id}/refresher?minutes=120` (204 when there's nothing to go back to) and
      `GET /api/v1/me/refreshers` on Analytics.API, signed in, computed on request from your own watches.
  - **Catch-up planner** (show page, signed in; "Before the premiere" on Up Next): for a followed show with a new
    season in the next 120 days, what's left to watch first and a week-by-week plan to get there.
    - **What's left:** everything aired after the furthest episode you've watched (all of it for a show you haven't
      started), with its hours.
    - **The pace:** "About 3 episodes (2 h) a week", against your own: what you've watched across all shows over the
      last 8 weeks. Verdicts: comfortable (half your pace or less), doable, tight (up to 1.5×) or behind, with how
      late you'd finish at your pace. Marking old seasons all at once doesn't count as a binge: a day counts for at
      most 4 hours.
    - **Essential cut:** when the cut ("lean") saves 10% or more of the time, it's offered as the other path, and the
      weeks follow it.
    - **The weeks:** split by running time, so long episodes spread out ("This week: S05E11–E16 · 6 h"). Two show;
      the rest open with "All N weeks".
    - **API:** `GET /api/v1/shows/{id}/catch-up?cut=false` (204 when nothing's due or you're caught up) and
      `GET /api/v1/me/catch-up` on Analytics.API, signed in, computed on request.
  - **Is it worth going back?** (show page, signed in; dropped shows on the backlog): for a show you dropped (episodes
    left, nothing watched for 30 days), whether what's left is better than where you stopped.
    - **Compared with where you stopped:** the last 10 rated episodes you watched, not the whole show, since that
      stretch is likely why you stopped.
    - **Verdicts:** "It gets good soon" (a slow start's turn is still ahead: The Office at S02E03, 4 episodes on), "It
      gets better from season 2" or "picks up later this season" (a season's remainder 0.3+ higher: Foundation), "More
      of the same", "It doesn't recover" (0.3+ lower: Dexter after season 4), or "Hard to say" (too few ratings).
    - **Spoiler-free:** the turn ahead is named by its number only.
    - **API:** `GET /api/v1/shows/{id}/comeback` (204 unless you dropped it) on Analytics.API, signed in.
  - **Backlog triage** (`/backlog`, signed in; linked from Up Next): the shows you follow but haven't started,
    ranked by how much they're likely worth your time, plus the ones you dropped.
    - **Worth your time:** TVmaze's rating, ±0.3 for whether its genres are ones you like (from shows you rated 4+ or
      watched 80% of), and how it ends (+0.3 sticks the landing, −0.5 fizzles). Each show lists its reasons.
    - **Verdicts:** Start next (the best 3), Worth it, Long but worth it (40 h or more; the essential cut is offered),
      Ends badly, and Let it go (under 7).
    - **Totals:** the hours on your list, how long it takes at your pace, and what letting go and essential cuts save.
    - **Dropped shows:** each with its "worth going back?" verdict.
    - **The backlog** is followed shows with nothing watched: Analytics doesn't see Tracking's "plan to watch" status.
    - **API:** `GET /api/v1/me/backlog` on Analytics.API, signed in.
  - **Fits your time off** (`/time-off`, signed in; linked from Tonight's plan): finished shows you can watch start to
    end in the time you have: a day, a weekend, a long weekend, a week off, or any number of hours.
    - **Which shows:** ended, not started, rated to at least 7.3 for you, and not ones that fizzle out. A single show
      fills at least a quarter of the time.
    - **A mix:** two or three shows that fill the time better than one, when there are (Firefly and The Haunting of Hill
      House in 20 hours).
    - **API:** `GET /api/v1/me/time-off?hours=20` on Analytics.API, signed in, about 50 ms.
  - **Community** (show and episode pages, signed in):
    - **Flag** a comment or tip: spoiler, abusive, spam, off-topic, or wrong. Three people flagging hides it until a
      moderator looks; two flagging it as a spoiler marks it a spoiler instead. The author still sees a hidden post,
      with why.
    - **From viewers:** tips about a show or an episode ("the first two episodes are slow"), 10-1000 characters, with
      a spoiler box. Others mark them helpful, and the most helpful lead. Behind the `community-insights` flag.
    - **Suggest a correction** (under a show's summary, or an episode's details): the summary, an episode's title,
      air date, runtime or summary, or something else. A moderator accepts it, and it replaces TVmaze's value in the
      catalog on every refresh from then on; "something else" is only recorded. Your suggestions and their status are
      at `/corrections`. Behind the `corrections` flag.
    - **Light moderation:** words in `Moderation:BlockedTerms` are refused; posts with a link are held for review (the
      author sees them meanwhile); each person may post 20 comments, tips and corrections and flag 30 times an hour.
    - **Moderators** (admins and `Moderation:Users` in the AppHost's appsettings) get Settings → Admin → Moderation
      (`/moderation`): flagged and held posts (restore or remove), suggested corrections (accept, with an edit if
      needed, or reject), recently accepted ones (revert), and a log.
    - **API** (Ratings.API): `POST /api/v1/comments/{id}/flag`, `POST /api/v1/community-insights/{id}/flag`,
      `GET`/`POST /api/v1/shows/{id}/community-insights[?episodeId=]`, `PUT`/`DELETE /api/v1/community-insights/{id}/helpful`,
      `POST /api/v1/corrections`, `GET /api/v1/me/corrections`, and `/api/v1/moderation/...`. Included in the data
      export and deleted with the account.
  - **Spoiler shield** (Settings): hides titles and summaries of episodes you haven't watched, on the show page (its
    episodes, next and latest, top episodes, season bests, when it gets good), the episode page, Up Next and the
    calendar. A hidden title shows when you select it. It's a setting of the device or browser, not the account.
  - **Theme** (Settings): light, dark, or the device's, per device or browser.
  - **Where do I know them from?** (show pages' cast, signed in): under each actor you've seen before, the shows you
    saw them in and who they played ("You know them from Breaking Bad as Saul Goodman"). "N you know" filters the cast
    to them.
    - **Seen:** a show with a watched episode, or rated. Followed alone doesn't count.
    - **Spoilers:** casts are today's regulars, with no seasons, so an actor in a show you're partway through may only
      arrive after where you are. Those lines say how far you are ("26 of 62 seen"), and with the spoiler shield on
      they read "a show you're partway through" until selected.
    - **Main casts only:** TVmaze doesn't list guest roles, so a guest spot isn't found.
    - **API:** `GET /api/v1/shows/{id}/known-faces` on Analytics.API, computed on request from your own data.
  - **Season verdict** (show pages, Insights, and the inbox of the show's followers): how a season that just ended did.
    - **What it says:** the season's TVmaze rating against the show's other seasons ("its best season yet", "one of its
      better seasons", "as good as the others", "one of its weaker", "its weakest yet", "first season", or just its
      rating when its earlier seasons have too few ratings to compare), whether
      the finale was a high point or a let-down, and how many viewers here who started the season have finished it
      (with 5 or more who started).
    - **When:** from 3 days after the finale (ratings come in) until 45 days after. A season counts as over when the show
      has ended, a later season is listed or announced, or the whole season dropped at once; otherwise once it's been
      quiet for twice its usual gap between episodes (14 days for a weekly show).
    - **Where:** the first highlight on the show page; "Seasons that just ended" on Insights; and once, as a
      notification to the show's followers, under the "new seasons" preference.
    - **API:** the show document's `seasonVerdict`, and `GET /api/v1/insights/season-verdicts?limit=` (Analytics.API).
      Behind the `season-verdict` flag, which also stops the notifications.
  - **Holiday episodes** (`/holidays`; on Home within 3 weeks of a holiday, and linked from Insights): Halloween,
    Thanksgiving, Christmas and the holidays, New Year's Eve and Valentine's Day.
    - **From your shows** (signed in): every holiday episode of the shows you follow, placed against how far you are:
      up next, skipped (behind where you are), watched, not started, or ahead of you. Comedies, animated, reality and
      game shows are marked "stands alone, usually"; ahead in anything else is "ahead of you in the story". Ones you
      can watch now come first. Titles you haven't watched follow the spoiler shield.
    - **Best known:** the most popular shows' holiday episodes, two per show. **Made for the season:** shows named after
      the holiday (Halloween Wars), whose every episode would match.
    - **How they're found:** the holiday's words (Halloween, trick or treat, Treehouse of Horror, Thanksgiving, Christmas,
      Hanukkah, Festivus, Valentine, …) in an episode's title, or in its summary if it aired around the holiday. Out of
      season a summary mostly mentions one in passing. Daily shows (more than 60 episodes in a season) are left out.
    - **API** (Catalog.API, public): `GET /api/v1/holidays`, `GET /api/v1/holidays/{key}` (best known), and
      `POST /api/v1/holidays/{key}/shows` with `{ "showIds": [...] }` (all of those shows' episodes; a POST, since a
      follow list doesn't fit in a URL).
  - **Research** (show pages; moderators research, everyone reads): a show's trusted record, checked by a person.
    - **On the show page:** an insight with numbered citations ([1] links to its source), the checked facts (ended,
      creators, seasons and episodes, country, awards, tagline, TMDB rating, IMDb/TMDB/Wikipedia/Wikidata links), its
      sources, and who checked it when.
    - **Researching** (moderators: "Research this show" on the show page, or Settings → Admin → Research):
      1. **Gather:** Wikidata (found by the show's TVmaze id), the Wikipedia article's summary, and TMDB when
         `Research:TmdbApiKey` is set. Gathering again replaces those and keeps pages added by hand.
      2. **Add pages:** any public web page (a review, an interview); its text is read and kept as a source. Private
         and local addresses are refused.
      3. **Pick the facts:** for each fact, the catalog's value, a source's, or your own. Summary, network and premiere
         date follow TVmaze until you pick a value for them.
      4. **Write the insight:** "Draft with the model" asks the local model (Model Runner's chat model,
         `ModelRunner:ChatModel`) for a draft from the included sources, in the background (a minute or two on a
         CPU). Edit it; `[n]` cites source n.
      5. **Save:** a new version. Earlier versions stay as history.
    - **Trusted source of truth, field by field:** a saved summary, network or premiere date replaces TVmaze's in the
      catalog (like an accepted correction), and survives every refresh. Dropping one in a later version gives the
      field back to TVmaze on the show's next refresh. The other facts live with the research.
    - **Queue** (`/research`, moderators): what to research next.
      - **Wikidata disagrees:** popular shows where Wikidata's network, premiere date or (ended shows) episode count
        differs from the catalog's, with both values and a link to the Wikidata item. Saving research with the right
        value overrides the catalog's, and the show drops off after the next check.
      - **Popular, not researched yet:** the catalog's most popular shows with no saved research, marked when started.
      - **How:** Research.API checks the 300 most popular shows against Wikidata after start and daily (one SPARQL query
        per 100 shows, by TVmaze id); a network matches when one name contains the other ("HBO" and "HBO Max") or by
        initials ("American Broadcasting Company" and ABC), a premiere when within a day at Wikidata's precision (a
        month-only date by month), and an episode count when within 2 or 10%. When Wikidata answers 429 it waits as
        asked, and a failed run is tried again in an hour.
      - **API:** `GET /api/v1/research/queue` (moderators), and `GET /api/v1/shows/facts?limit=` on Catalog.API (the
        popular shows' network, premiere date and aired episodes; behind the `research` flag).
    - **MCP:** `show_research` returns the saved record.
    - **API** (Research.API, port 5114): `GET /api/v1/shows/{id}/research` (public), and for moderators
      `GET /api/v1/research`, `GET /api/v1/research/{id}`, `POST /api/v1/research/{id}/gather`,
      `POST /api/v1/research/{id}/sources` (`{ "url" }`), `PUT|DELETE /api/v1/research/{id}/sources/{sourceId}`,
      `POST|PUT /api/v1/research/{id}/draft`, `POST /api/v1/research/{id}/save`, and episode notes:
      `GET /api/v1/research/{id}/episode-notes`, `PUT|DELETE /api/v1/research/{id}/episode-notes/{episodeId}`
      (`{ "mark": "essential" | "skippable", "reason", "sourceUrl" }`). Anyone else gets 404. Behind the `research`
      flag (and `episode-notes` for the notes).
  - **Ask** (`/ask`, signed in; linked from Insights): questions about shows in plain words ("Landman
    insights", "hidden gems in drama", "who created Severance?"), answered by Model Runner's chat model, which looks
    things up with the app's MCP tools (catalog, insights, Discover lists, premieres, saved research).
    - **Grounded, and shows its work:** the model is told to answer only from what the tools returned. Each answer lists
      what it looked up ("How I got this"), and links the shows it names that a tool returned.
    - **Follow-ups:** "which of those is shortest?" carries the last two questions and answers.
    - **A small model:** the default, qwen3 0.6B (`ModelRunner:ChatModel`), runs on a CPU and takes 10 s to 2 minutes
      an answer. It picks the right tools, but often misreads what they return: on Severance it named the cast as
      creators, and on Breaking Bad it read "ends as it ran" as a great finale. A larger chat model answers better
      where the hardware allows; none was tried here, since only the 0.6B one could be pulled.
    - **Limits:** questions are answered one at a time; each person may have one waiting and ask 20 an hour.
      Questions are kept in memory for 2 hours and never stored; a restart forgets them.
    - **Nothing personal:** the tools have no user, so it knows shows, not your watches or ratings.
    - **API** (Assistant.API, port 5115): `POST /api/v1/ask` (`{ "question", "previousId" }`, 202 with the question's
      state), `GET /api/v1/ask/{id}` (poll until `answered` or `failed`), `GET /api/v1/ask/about`. Behind the `ask` flag.
  - **Character trail** (`/people/{personId}/characters/{showId}`, from a show page's cast, where a character name
    reads "Karen Page · 4 shows"): every show where the same actor plays the same character, in the order they first
    aired.
    - **Matching:** Universes' name rules ("Dr. Frasier W. Crane" is "Dr. Frasier Crane"), over the catalog's casts and
      the actor's TVmaze credits, so Cheers is on Frasier's trail though it isn't in the catalog (it links to search,
      which imports it). Roles as themselves have no trail.
    - **Signed in:** your progress in each show you follow, and "Add all to your watchlist" for the rest.
    - **Limits:** TVmaze lists main casts only, so guest appearances are missing, and a character who joins in a
      later season counts for the whole show. Same actor only: a recast character isn't followed.
    - **API:** `GET /api/v1/people/{personId}/characters/{showId}` on Catalog.API (public); show detail's cast has
      `trailShows`. Behind the `character-trail` flag.
  - **Taste twins** (`/taste-twins`, linked from For you; `taste-twins` flag): people here whose show ratings track
    yours, and what they loved that you haven't rated, followed or watched.
    - **Twins:** agreement is 1 − the average star difference / 4 on shows you've both rated (3 or more), weighted
      towards bigger overlaps; the best 25 with 75% or more agreement count. You need 5 ratings.
    - **Anonymous:** nobody is named; nothing shows with fewer than 5 twins, and a pick or a shared favourite needs 5 twins behind it.
    - **The page:** how many twins and how closely you agree, the shows you agree on, and their picks ("Loved by 13 of
      your twins · ★ 4.7").
    - **API:** `GET /api/v1/me/taste-twins` on Analytics.API, signed in.
  - **Awards shelf** (`awards-shelf` flag): what a show has won, on its page ("🏆 57 awards: Primetime Emmy Award × 23,
    TCA Award × 11, …"), and **Prestige picks** (`/prestige`, linked from Discover): the most-awarded popular shows
    you haven't started (public; signed out, simply the most awarded).
    - **Where awards come from:** Wikidata's award wins (one statement per win) for the 300 most popular shows,
      read by the research queue's daily check. Where a moderator saved research with an awards fact, its checked
      wording is shown instead ("checked by our moderators"), and Prestige picks says so rather than give Wikidata's
      count. If Wikidata's awards query fails, the previous day's awards stay.
    - **API** (Research.API): `GET /api/v1/shows/{id}/awards` and `GET /api/v1/awards?limit=` (most wins first), public.
  - **Network report cards** (`/networks`, linked from Discover; `network-reports` flag): for every network or streamer
    with 5 or more shows here, how its shows rate, how its ended shows land (sticks the landing, fizzles), the share of
    seasons that come back, the typical wait between seasons, how often it drops whole seasons at once, and its best
    shows and great endings. Each figure is set against the whole catalog (green when clearly better, red when clearly
    worse), and the cards sort by any of them.
    - **From the provider's data,** recomputed hourly, so nothing about our users is in it.
    - **Logos and prices** (`your-services` flag): each card shows the logo of the service the network's shows are on
      and its estimated price ("On Paramount+ · from $8.99 a month · free over the air in the US"), and "You have it"
      for services you've picked. Prices are the cheapest US plan, checked by hand (October 2026, from
      [Stream-Wiser](https://stream-wiser.com/prices)); MGM+ and discovery+ are shown without one, since it couldn't
      be confirmed. Directory: `NetworkServices` in IntegrationEvents.
    - **API:** `GET /api/v1/insights/networks` on Analytics.API, signed in (`baseline` is the whole catalog).
  - **Your services** (`/services`, from the report cards; Plus and Family; `your-services` flag): pick the streaming
    services you pay for.
    - **The report:** your monthly total, and per service the hours you watched on it in the last 30 days, its cost per
      hour, the shows behind them, and the shows you follow with episodes on it in the next 30 days.
    - **Verdicts:** good value (up to $2 an hour, a rental's price), pricey (more), quiet (nothing watched, but your
      shows are coming) and could pause (nothing watched, nothing coming). Free and unpriced services are listed without a
      verdict on cost.
    - **Not on your services:** services you haven't picked that have your followed shows coming.
    - **Bulk marks** (8+ of a show's episodes at once) aren't counted as watching, as in Year in TV.
    - **API** (Analytics.API): `GET /api/v1/networks/services` (directory, prices and logos), `GET`/`PUT
      /api/v1/me/services`, `GET /api/v1/me/services/report`. Free gets 403 `plan-limit` on saving and the report.
  - **Year in TV** (`/year`, linked from Stats; `year-in-tv` flag): your year of viewing, any year you watched in.
    - **What's in it:** hours, episodes, shows (and how many were new to you), days with TV and your longest run of
      days, your busiest month and biggest day, most watched shows, time by genre, the network you watched most,
      the best rated episode you watched, and what you finished, caught up on and left behind.
    - **Taste shift:** how the DNA of what you watched moved since the year before ("more short episodes").
    - **Called it:** a show rated 7.5+ that you followed before at least half of its followers here (with 5 or more).
    - **Bulk marks:** 8 or more of a show's episodes marked at once ("watched up to S05E10") aren't counted, since when
      you watched them isn't known; the page says how many were left out.
    - **API:** `GET /api/v1/me/year?year=&tz=` on Analytics.API, signed in.
  - **Episode duels** (`/shows/{id}/duels`, linked from show pages; `episode-duels` flag): rank a show's episodes you've
    watched by picking the better of two, or skipping a pair you can't call. Your ranking builds as you go, with each
    episode's wins and losses; "Start over" forgets them.
    - **Pairs:** the episode in the fewest duels against one close to it in your ranking that it hasn't met, since
      close calls are what the ranking still needs to learn.
    - **Ranking:** a Bradley-Terry model of your answers, so the order doesn't depend on when you answered, and one
      lucky win doesn't put an episode on top.
    - **Only watched episodes;** unwatching one takes it out of the ranking.
    - **API** (Ratings.API, signed in): `GET`, `POST {winnerId, loserId}` and `DELETE` on `/api/v1/shows/{id}/duels`.
  - **Waiting room** (`/waiting`, linked from Up Next; `waiting-room` flag): running shows you're caught up on, soonest
    back first: the next episode or season premiere when it's dated, else the forecast from the show's own past waits
    (with "overdue" past its range), with how often comparable shows came back as a weak signal.
    - **API:** `GET /api/v1/me/waiting` on Analytics.API, signed in.
  - **Reunion radar** (`/reunions`, linked from For you): shows where two or more actors from shows you love work
    together again.
    - **Shows you love:** rated 4 or 5 stars, or followed or watched and not rated lower.
    - **Where it looks:** every show on TVmaze the actors are in the main cast of, not just the catalog. A show outside
      the catalog links to search, which imports it.
    - **Ranking:** favourites count more (5 stars over 4 over unrated), and so do bigger roles in them (the leads over
      the rest of the billing). "New and coming soon" lists shows premiering soon or in the last year.
    - **Not reunions:** spin-offs and revivals (any of them playing the same character: those are Universes),
      shows you already follow, rated or watched, and roles as themselves.
    - **Data:** the sync worker fetches each catalog actor's roles from `/people/{id}/castcredits?embed=show`, 30
      people a minute, leads of popular shows first, refreshed monthly. Until everyone is fetched, the page says how
      many of your shows' actors are still to come.
    - **API:** `GET /api/v1/me/reunions` on Analytics.API, signed in, computed on request from your own data.
  - **Does it stick the landing?** (ended shows' pages, signed in; spoiler-free): the finale and final season against
    the rest of the show, from TVmaze's ratings, without naming the finale.
    - **Verdicts:** "Sticks the landing" (The Americans, Succession, Better Call Saul), "Great finale, weaker last
      season" (The Office), "The finale lets it down" (Seinfeld), "Fizzles out" (Game of Thrones, Dexter, How I Met Your
      Mother), "Weaker last season" (a finale in line, at the end of a weak season), and "Ends as it ran" (Breaking
      Bad, Friends).
    - **How:** the finale (the last regular episode to air) 0.3+ above or below the median episode is strong or
      weak; the final season 0.3+ below the median season is weak, even with two seasons; its rank ("2nd best of 6")
      needs three. Ties read as ties ("ties for its best"). A show whose final season has too few rated episodes to
      check gets no verdict: 232 of 289 ended shows get one.
    - **Great endings:** a Discover list of well-rated (7.5+) ended shows that stick the landing, most popular first.
  - **Is it worth it?** (show pages, signed in; `worth-it` flag): a 0-100 score for whether the hours pay off, with
    every part behind it, and the first of the page's highlights.
    - **How:** TVmaze's rating, moved by what the other insights found:
      - a slow start: −0.15, or −0.3 when it gets good 10 h or more in; good from the first episode +0.1;
      - a decline: −0.6 × the share of the running time from the season it sets in;
      - the ending: sticks or great finale +0.3, weaker last season −0.1, finale lets down −0.3, fizzles −0.5;
      - the spread of episode ratings: a rollercoaster −0.15, steady +0.1.
    - **Score:** 6.0 on that scale is 0 and 9.0 is 100, so a typical show scores 50. The verdicts are worth it (75+),
      mostly worth it (50+), mixed (30+) and hard to recommend.
    - **Examples:** Breaking Bad 100, Better Call Saul 97, The Wire 88, Game of Thrones 66, Dexter 56, Severance 52,
      How I Met Your Mother 22. A running show's ending isn't in it yet, and the panel says so.
    - **Who gets one:** shows with a TVmaze rating and 5 or more rated episodes (595 of 846).
    - **Worth your time:** a Discover list of the shows rated worth it, best scores first, most popular among equals.
  - **Spoiler watch** (`/spoilers`, signed in; linked from Up Next; `spoiler-watch` flag): your followed and started
    shows with episodes from the last two weeks you haven't seen, riskiest first: high, some or low risk, which
    episode makes it so (a finale, a premiere, or just the latest; numbers only, never a title), how many episodes
    it takes to be safe, and roughly when the risk calms down if nothing more airs.
    - **The score:** each unseen episode counts 1 (a finale 2, a premiere 1.5), fading by e^(−days/5), all times
      the show's popularity (0-1). High from 1.0, some from 0.4; under 0.1 a show isn't listed.
    - **API:** `GET /api/v1/me/spoiler-watch` on Analytics.API.
  - **Your TV clock** (`/tv-clock`, signed in; linked from Insights; `tv-clock` flag): when you watch, from your last
    year of watches in your own time zone. A weekday-by-hour heatmap with your peak hour; what kind of viewer that makes
    you (night owl, early bird, lunch-break viewer, prime-timer, weekend binger, weeknight regular), each with the share
    behind it; your sittings (episodes in a row, how often they're binges of three or more, and your longest); and when
    you watch each of your top shows. Bulk marks (eight or more at once) are left out and counted.
    - **API:** `GET /api/v1/me/tv-clock?tz=…` on Analytics.API.
  - **Your TV life** (`/tv-life`, signed in; linked from Insights; `tv-life` flag): type the year you were born and get
    every year of your life down a timeline, your age as the marker: the catalog's biggest premieres and finales that
    year (the ones you've watched in bold), and your own year where you watched anything (hours, shows started and
    finished, the show you watched most). On top: how many of your shows are older than you, the oldest, the show that
    premiered the year you were born, and your biggest year. Your birth year stays on the device; nothing is stored.
    - **API:** `GET /api/v1/me/tv-life?born=1985&tz=…` on Analytics.API.
  - **TV time machine** (`/time-machine`, signed in; a teaser on Home and a link on Insights): a week of TV history.
    - **This week in TV history** (the default): what premiered and what ended this week in past years, and the
      best-rated episode of each year that week. Home shows four of the best-known milestones ("Once Upon a Time
      premiered 15 years ago").
    - **Travel to a date** (a birthday, say; the date is in the address): that week's premieres, finales, best
      episodes, and the shows on air then with their season. The week of 22 September 1994: Friends and ER premiered;
      Seinfeld, Frasier and The X-Files were on.
    - **Marks** the episodes you've watched. Episodes rated 1.5+ above their show are left out of the best ones (a
      few fans' 10s on thinly rated shows).
    - **Your calendar:** weeks and dates are in your profile's time zone.
    - **API:** `GET /api/v1/insights/time-machine[?date=yyyy-MM-dd][&tz=IANA id]` on Analytics.API, computed on request
      and cached for the day.
  - **Creator fingerprint** (person pages, signed in): a creator's style, from the DNA of the shows they created or
    developed (two or more with DNA; 110 people in the development catalog).
    - **Signature:** distinctive traits most of their shows share. Mike Flanagan: "Drops all at once · 3 of 3, A short
      watch · 3 of 3"; Dick Wolf: "Long seasons, A long commitment, Back every year"; Vince Gilligan: "Acclaimed, Peaks
      late". Creators with no shared trait say so ("No single style"), and every fingerprint lists its range: the
      traits where their shows sit at opposite ends.
    - **Made the X way:** other people's shows with the same fingerprint (DNA similarity 0.85+, sharing the signature):
      Mike Flanagan → 3 Body Problem, Time; Chuck Lorre → The Goldbergs, Superstore.
    - **Creators with a similar style:** Mike Flanagan → Guy Ritchie, Neil Gaiman; Dick Wolf → Julie Plec, Bruno Heller.
    - **API:** `GET /api/v1/people/{id}/fingerprint` on Analytics.API (404 for anyone without one).
  - **When it gets good** (show pages, signed in): the episode where a slow-starting show turns, how long it takes to
    get there, and how much better it rates from there. Severance: "Gets good at S01E06 “Hide and Seek”, 4 h in (5
    episodes). The episodes before it average 7.2 on TVmaze; from there, 7.8." Shows that don't need warming up say
    "Good from the first episode".
    - **How:** TVmaze's episode ratings over the opening run (the first 40 rated episodes). A slow start opens 0.3+
      below that run's median; it turns at the first episode where the next three reach the median and the next ten
      hold it, within the first 25 episodes, and only when that's 0.3+ better than before.
    - **Checked:** The Office turns at S02E03, Succession at S02E01, The Wire at S01E07, Community and The Good Place
      at S01E07. 102 of 625 shows have a turn and 206 are good from the start; the rest say nothing.
    - **Personal:** "Will you finish it?" says "Hang on: it gets good in 2 episodes (1 h 29 min), at S01E06 …" when
      you've not reached the turn. If our viewers most often stop before the turn, the show page says so.
  - **Show DNA** (show pages, signed in): how a show is made and how it plays out, as 12 traits on a scale between two
    plain ends. Examples: "Short episodes" ↔ "Hour-long episodes", "Weekly episodes" ↔ "Drops all at once",
    "Rollercoaster" ↔ "Steady", "Fades over time" ↔ "Gets better", "Quiet finales" ↔ "Big finales".
    - **Tags:** the page tags the traits that set the show apart from a typical one. Most shows are hour-long, weekly and
      recent, so those don't count. Breaking Bad is "Acclaimed, Peaks late".
    - **Where it comes from:** episode runtimes, season sizes and air dates, TVmaze's show and episode ratings, and
      our users' completion rate once 5 or more have watched.
    - **Recommendations:**
      - Similar shows and For you blend DNA into their ranking. DNA reorders shows about similar things, and never makes
        a show about something else similar.
      - A different kind of show (reality against scripted) or language counts for much less.
      - The reason names the shared distinctive traits: "Same DNA: short episodes, long seasons", or "Same DNA as Severance:
        long waits between seasons, big finales".
    - **DNA twins** (show pages, public): "Made like this, about something else". Shows of the same kind whose DNA is
      close (0.85 or more) and that share at least two distinctive traits, but share few genres, no nanogenre, and
      aren't on each other's similar-shows lists. Each says what they share and what it's about instead: Ted Lasso →
      Silo ("Peaks early, long waits between seasons · but science-fiction and mystery"); The Office → Scrubs; Stranger
      Things → MINDHUNTER. `GET /api/v1/shows/{id}/dna-twins` on Recommendations.API.
  - **Nanogenres** (chips on show pages, `/nanogenres`, a search filter; public): fine-grained genres found in the
    catalog itself, with no list written down. Ted Lasso is a "Sports comedy"; Wednesday both a "Supernatural comedy"
    and a "Mystery comedy"; The Simpsons an "Animated family comedy".
    - **Clusters:** the sync worker clusters shows by their text embeddings (what they're about) and their genres and
      type (what kind of show), 60% the latter. Groups are kept from 3 shows; ones over 30 are split again.
    - **More than one per show:** a show also joins any group it's as close to as that group's typical member.
    - **Names:** from the specific genres most of a group shares ("Medical drama", "Cooking reality"). A group with
      nothing specific in common is dropped rather than given a vague name. That's why pure comedies like The Office
      have none.
    - **Small genre pairings:** an uncommon pairing shared by 2 to 15 shows counts too ("Sports comedy": Ted Lasso and
      The League).
    - **Refresh:** every 6 hours. On the development catalog: 50 nanogenres, 393 of 625 shows in at least one.
    - **Recommendations:** shared nanogenres count towards similarity, and give the plainest reason: "Also a sports
      comedy", or "Another sports comedy, like Ted Lasso".
    - **API:** `GET /api/v1/nanogenres`, `GET /api/v1/nanogenres/{slug}`, and `nanogenre=` on search (Catalog.API).
  - **What's next** (show pages, signed in): forecasts for running shows, each with how sure it is.
    - **Comes back?** For a show between seasons with nothing announced: the share of comparable seasons (same
      rating band, point in the run and era) that got another one, against the catalog's overall 92%. It's labelled
      a weak signal, because the catalog is mostly popular shows, which usually return.
    - **When:** the next season's likely start, from the median of the show's last three waits between seasons, with
      an 80% range. Past the range, it says the show is overdue.
    - **How it'll rate:** 0.7 × the latest season's rating + 0.3 × the show's average, with an 80% range that's wider for
      shows whose seasons vary more.
    - **Backtested every run:** each model replays the catalog's history (what it would have said after every past
      season) and keeps the misses as its range. On the development catalog, ratings are off by 0.33 on average
      (1,440 seasons) and start dates by 22 days at the median (2,291 waits).
  - **Call it** (`/calls`, linked from Insights, and a card on show pages; `call-it` flag): predict what a running show's
    next season will rate on TVmaze, before it premieres, and see whether you beat the forecast above.
    - **Making a call:** a slider from 5 to 10 on the show page; change or take it back until the season's first episode
      airs, when it locks.
    - **No peeking:** the card shows the model's forecast and everyone's average call (from 5 callers) only after
      you've called. The forecast also sits in "What's next" on the same page; the card doesn't hide that.
    - **Scored** once the season is over (the season verdict calls it, a later season airs, or the show ends): the
      season's average episode rating, how far off you were, and whether you or the model came closer.
    - **Your record** (`/calls`): calls scored, your average miss against the model's, calls won against it, and your
      followed shows' next seasons to call, soonest premiere first.
    - **API:** `GET`/`PUT`/`DELETE /api/v1/shows/{id}/call` and `GET /api/v1/me/calls` on Analytics.API (signed in).
  - **Will you finish it?** (show pages, and "Will you finish them?" on Stats): for shows you're part-way through,
    from your own watches. It shows facts, not a probability: there isn't enough history per person for an honest one.
    - **Pace:** your pace over the last four weeks, and when it gets you to the end of what's aired.
    - **Status:** on track (watched in the last 14 days), slowing, or stalled (nothing for 30 days).
    - **Your record:** "You finished 2 of the 3 other shows you got this far into", from 3 comparable shows.
    - **Drop-off:** the episode many of our viewers stop at, when it's still ahead of you (from 5 viewers).
    - **API:** `GET /api/v1/me/finish-outlook` and `GET /api/v1/shows/{id}/finish-outlook` on Analytics.API.
  - **Stats page — your taste:** computed on request from your own data only. Months follow your profile's time zone,
    and "finished" counts regular episodes (a special never keeps a show from being finished).
    - How your ratings compare with TVmaze's (stars doubled to its 0-10).
    - The genres you rate highest.
    - Time watched over the last 12 months.
    - Shows you're caught up on, watching, or dropped (nothing watched for 30 days).

  Watched events carry the time the user watched, so episodes the mobile app marks while offline count in the right month.
- **Social.API** builds the feed from Tracking's and Ratings' events (watched, finished, followed a show, rated a show),
  keeps show titles and episode numbers from the catalog (asking the sync worker for a replay on its first start, like
  Analytics), and names from Identity's events and daily snapshots.
  - **Following people:** one way, no approval. Search by name at `/feed/people`; each person has a page at `/users/{id}`.
  - **The feed** (`/feed`, in the menu): what the people you follow watched, finished, started following and rated,
    newest first. Episodes of one show watched within 6 hours are one item ("S02E01–S02E04 · 4 episodes"). Episode
    titles are never shown: a follower may not have seen them.
  - **Taken back:** unwatching, unfollowing a show or removing a rating takes it out of the feed. A follow that comes
    with a watch of the same show (watching follows it) is shown as the watch only.
  - **Privacy:** Settings → Social.
    - **Share what I watch…** (on by default): turned off, nobody sees any of your activity, including what's already
      in their feed; your page says you don't share.
    - **Private profile:** people ask to follow you, and only those you approve see your activity, lists and followers;
      others see just your name. Requests are under Feed → People. Making the profile public approves every request.
    - **Block** (on a person's page): neither of you sees the other anywhere (search, pages, feed, lists, show pages),
      and any follows between you end. Unblock under Feed → People.
  - **People you follow, on a show page:** who follows, watches or rated it, and how far they are ("Up to S03E04 · 28
    of 62", "Finished", ★★★★☆), furthest along first.
  - **Watching together** (on a person's page): shows you both follow or have started, both your progress, and who's
    ahead. Shows you're both part-way through come first.
  - **Shared lists:** tick "Share on your profile" on one of your lists. It appears on your page, at
    `/users/{id}/lists/{listId}`, for whoever can see your profile; changes show straight away, and unticking takes it
    back.
  - **History is included** for the show page and watching together: they use your whole watch history (Tracking's
    and Ratings' daily snapshots fill Social's copy), while the feed starts from when Social was added.
  - **Data:** included in the data export and deleted with the account. Behind the `social` flag.
  - **API:** `GET /api/v1/feed?before=…`, `GET /api/v1/people?q=…`, `GET /api/v1/people/{id}`,
    `POST|DELETE /api/v1/people/{id}/follow`, `GET /api/v1/people/{id}/followers|following`, `GET /api/v1/me/social`,
    `PUT /api/v1/me/social/settings` (`shareActivity`, `private`), `GET /api/v1/me/follow-requests`,
    `POST /api/v1/me/follow-requests/{id}/approve`, `DELETE /api/v1/me/follow-requests/{id}`,
    `POST|DELETE /api/v1/people/{id}/block`, `GET /api/v1/me/blocks`, `GET /api/v1/people/{id}/lists/{listId}`,
    `GET /api/v1/shows/{id}/friends`, `GET /api/v1/people/{id}/together` on Social.API (port 5111), all signed in.
- The sync worker starts only after the consuming services report healthy, which includes "queue bound and consuming", so no early events are lost.

## Database schema

Catalog, Analytics, Identity, Notifications, Recommendations, Research, Social and Tracking use EF Core migrations
(`Catalog.Data/Migrations`, `Analytics.Data/Migrations`, `Identity.API/Data/Migrations`, `Notifications.API/Data/Migrations`,
`Recommendations.API/Data/Migrations`, `Research.API/Data/Migrations`, `Social.API/Data/Migrations`,
`Tracking.API/Data/Migrations`); each service's API applies its own at startup. Identity's, Notifications',
Recommendations' and Tracking's first migrations (`InitialCreate`) are their `EnsureCreated` schemas, so a database made before they moved to migrations is
marked as having it and upgraded in place.

After changing either model, add a migration (the `dotnet-ef` version is pinned in `dotnet-tools.json`):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Services/Catalog/Catalog.Data --output-dir Migrations
# or, for Analytics:
dotnet ef migrations add <Name> --project src/Services/Analytics/Analytics.Data --output-dir Migrations
# or, for Recommendations:
dotnet ef migrations add <Name> --project src/Services/Recommendations/Recommendations.API --output-dir Data/Migrations
# or, for Research:
dotnet ef migrations add <Name> --project src/Services/Research/Research.API --output-dir Data/Migrations
# or, for Notifications:
dotnet ef migrations add <Name> --project src/Services/Notifications/Notifications.API --output-dir Data/Migrations
# or, for Social:
dotnet ef migrations add <Name> --project src/Services/Social/Social.API --output-dir Data/Migrations
# or, for Tracking:
dotnet ef migrations add <Name> --project src/Services/Tracking/Tracking.API --output-dir Data/Migrations
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

**Weekly digest** (`weekly-digest` flag; Settings → Notifications): one email a week instead of an email per alert, on
the day and from the hour you pick, in your time zone. Phone and browser alerts carry on.
- **What's in it:** "Coming up this week", the next 7 days of episodes of the shows you follow and haven't muted, with
  premieres marked; and what you were told since the last digest: crossovers, how seasons did, new seasons and endings,
  and new episodes counted per show.
- **Preview:** "Preview this week's digest" in Settings shows it as it would go out now.
- **Sending:** checked every 15 minutes; at most one in six days, and a week with nothing in it sends nothing. Links
  in the email go to `Digest:BaseUrl` (the web app, `https://localhost:7120` by default). Like other email, it logs
  until `Email:*` is configured.
- **Not in it yet:** Up Next and what people you follow finished, which live in Tracking and Social; Notifications
  can't read them for a user without the user's token.
- **API:** `GET /api/v1/digest/preview` on Notifications.API (signed in), and the `weeklyDigest`, `digestDay` and
  `digestHour` preference fields.

## Before production

- Change the development secrets in `AppHost.cs` and `Realms/tvtracker-realm.json`, and turn on HTTPS for Keycloak.
- Remove `usesCleartextTraffic` (Android) and `NSAllowsLocalNetworking` (iOS), and point the mobile app at real hosts.
- Add a dead-letter queue for events that fail twice, and tests.

## Package versions

Versions live in `Directory.Packages.props` (the MAUI app pins its own in its project file). Aspire is on 13.6.4
(`AspireVersion` there, plus the `Aspire.AppHost.Sdk` version in the AppHost's project file; keep the two equal).
Run `aspire update` (or `dotnet list package --outdated`) to bring them up to date.

## Not built yet

**Qwen3-Embedding-8B as a built-in embedding model** (backlog; started and set aside). What the first attempt found:
- **The model:** `ai/qwen3-embedding:8B-Q4_K_M` on Docker Hub (4.7 GB; also `hf.co/Qwen/Qwen3-Embedding-8B-GGUF:Q4_K_M`,
  which Model Runner can pull from Hugging Face). 4096 dimensions, trained to be shortened (MRL), so it can be cut to
  the column's 384 like nomic. Queries take an instruction (`Instruct: …\nQuery:`), documents none.
- **It needs two per-model settings first:**
  - **A longer request timeout:** on 4 CPU cores a search takes ~2 s and a show ~6 s, so the sync worker's 32-show
    batches take minutes, past the server client's 60 s.
  - **A smaller context in Model Runner** (`POST /engines/_configure` with `context-size`, before the model loads):
    with Model Runner's default 40,960 tokens, llama.cpp didn't load within Model Runner's 5 minutes on a CPU;
    2,048 loaded in about 3.5 minutes. Inputs are cut at 512 tokens anyway.
- **Still to do:** tune its `MaxDistance` (and compare MRR with the others) on the 42 test searches, about an hour of
  embedding on a CPU, and decide whether it's only worth offering with a GPU (Docker Desktop's Model Runner via
  `ModelRunner:Url`).
- **Docker Hub's anonymous pull limit** (429) blocked the `ai/` pull in the sandbox, which is why the Hugging Face copy
  was used there.
