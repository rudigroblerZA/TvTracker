# What makes TvTracker different

Most trackers stop at "what's next" and "what have I watched". TvTracker adds features that work out how shows relate
to each other, how good their episodes are, and how much of them you actually need to watch. Each one is built from
data the app already holds: TVmaze's catalog, episode ratings, cast and crew, show-text embeddings, and our users'
own watches and ratings.

The README covers how each one works and how to run it; this page is the tour. Numbers and examples come from the
625-show development catalog (TVmaze's top 500 plus the hourly imports).

## Built

### 1. Universes: how a franchise fits together

`/universes`, and "Shared universe" on show pages.

Finds spin-offs, sequels, revivals, crossovers and franchises on its own, then shows them three ways.

- **How it finds them:** the same actor playing the same character in two shows ("Matt Murdock / Daredevil" across
  Marvel's Daredevil, The Defenders and Daredevil: Born Again), or a shared franchise name. "NCIS: Sydney" goes with
  "NCIS". "Chicago Fire", "Chicago P.D." and "Chicago Med" go together because they share seven makers.
- **Timeline:** each show is a bar across the years, split into its seasons. It notes how each show started relative
  to the first ("Chicago Med · 3 years 1 month after Chicago Fire") and how many ran at once ("All 3 on air together
  since November 2015").
- **Watch order:** every episode of the franchise in air order, with your place tracked across all the shows.
  - Crossover events are named when two shows' episode titles say so: "Infection" runs Fire → Med → P.D., and
    "Sister City" runs NCIS → NCIS: New Orleans.
  - "Up to here" catches you up in one click.
- **Links:** an arc map coloured by kind of link: continues the story, crossover, revival, or same franchise.
- **Coverage:** 35 universes, all of them real when checked by hand. They include Marvel (10 shows), NCIS (7),
  Star Trek (7), Dexter, Yellowstone, One Chicago, Bosch, The Walking Dead, Breaking Bad and Better Call Saul.

### 2. Essential cut: the shorter way through a long show

`/shows/{id}/essential-cut`, linked from long shows' pages.

- **What it does:** uses episode ratings to drop each season's clear misses while keeping its premiere, finale,
  best episode and two-parters. A weak season is thinned rather than skipped whole, so the story still holds.
- **Levels:** Light, Lean and Essentials. One Piece goes from 510 hours to 443, 376 or 324; Breaking Bad loses only
  10% at Light.
- **Accuracy:** it has no filler data, yet it skips 53–78% of One Piece's known filler. At Light, a skipped episode is
  filler 3.6× as often as a random one.
- **Spoiler-safe:** only numbers and ratings show unless you ask for titles. For a show you've started, it shows
  the next episode the cut keeps and the hours left.

### 3. Blend: shows that sit between two others

`/blend`, and "Blend … with another show" on every show page.

- **Examples:** Breaking Bad × The Office gives The Gentlemen, Brooklyn Nine-Nine and BEEF. NCIS × Friends gives
  Scrubs.
- **How it works:** a slider leans the mix towards either show. Each match shows where it sits between the two, and
  which genres it takes from each.
- **Ranking:** shows are ranked by their distance to the *farther* of the two, so a show close to only one of them
  doesn't win.
- **Honest about bad pairs:** it says so when the two have little in common (Stranger Things × The Great British
  Bake Off).

### 4. Search by meaning

Search on every page.

- **Searches by description:** "naval crime investigators" finds NCIS, "cooking competition" finds the baking
  shows, and "time travel mystery in a small german town" finds Dark.
- **The model:** runs locally or on Docker Model Runner, with no API key. It's configurable, and three models are
  built in, each with a match cutoff tuned on 42 test searches.
- **Imports as you search:** shows not yet in the catalog are pulled from TVmaze, so anything on TVmaze can be found.

### 5. Show tags, computed rather than typed

On cards, show pages and the search filter.

- **Tags:** drops all at once (or in parts), bingeable, weekend binge, fan favorite, limited series, quick episodes,
  long-running, and prolific creator/producer.
- **Computed from data:** each comes from the episode schedule, ratings or crew, not hand-tagging. "Bingeable"
  means about ten episodes a season or fewer, under 50 hours in all, with nothing left to air.
- **Filtering:** search filters by them ("bingeable" + "Drama"), and the filtered tag shows first on cards.

### 6. Who made it: prolific creators and producers

On show and person pages.

- **Recognises prolific makers:** people credited on 10 or more shows behind the camera, such as Dick Wolf (57) and
  Chuck Lorre (19).
- **Credits by role:** separates "created by" from "executive produced by".
- **Their other work:** lists their best-known shows and the rest of their shows in the catalog.

### 7. Episode patterns

On show pages.

- **Consistency:** from the spread of episode ratings, from steady to rollercoaster.
- **The show's arc:** where it peaked, and whether it declined after.
- **Finales:** whether they beat the rest of their season.
- **Season bests:** each season's best episode.

### 8. Discover lists

`/discover`, shareable with a list and genre in the address.

- **Hidden gems:** rated 7.5+, outside the most popular 40%, and few of our users follow them.
- **Most consistent and biggest rollercoasters:** among well-rated shows.
- **Best season finales.**
- **Bingeable, weekend binges and fan favorites.**

### 9. Your taste

On the Stats page, computed from your own data only.

- **Rating bias:** how your ratings compare with TVmaze's, kinder or harsher, and on which shows most.
- **Your genres:** the genres you rate highest.
- **Time watched:** over the last 12 months.
- **Progress:** which shows you're caught up on, still watching, or have quietly dropped.

### 10. Tonight's plan

`/tonight`, linked from Home and Up Next.

- **You give it:** the time and a mood ("2 hours, light & funny", or in your own words).
- **It picks:** the shows and episodes from what you're watching and your watchlist, landing close to the time.
  Each pick says why: "Fits “Light & funny”", "You're in the middle of it", "On your watchlist".
- **How the mood is matched:** with the same meaning vectors as search. It ranks your own shows: Ted Lasso first for
  "Light & funny"; The Expanse and Severance for "Mind-bending".
- **Mix it up or one show:** swap a show out, start watching, or mark the whole plan watched afterwards.

### 11. Smaller touches

- **Premieres:** returning seasons come with the catch-up time (episodes and hours to watch first), and how many of
  our users followed the show recently.
- **Actor pages:** a person's shows' average rating, their best show, the years they worked, and co-stars they share
  two or more shows with.
- **Privacy:** anything computed from our users' activity needs at least 5 users or ratings behind it.

## Ideas for next

Ranked by value for the effort. Each builds on data and code that already exist.

| # | Idea | What it does | Builds on | Effort |
| --- | --- | --- | --- | --- |
| 1 | **Crossover alerts** | Tell you when a crossover event is scheduled in a universe you're watching ("Reckoning airs across Fire, Med and P.D. on Wednesday"), so you're caught up on all three shows first. | Watch order's crossover detection, follows, Notifications | Small |
| 2 | **Catch-up planner** | Before a season premiere, work out a pace that gets you there: "3 episodes a week from now catches you up before Season 5 on 12 January". Optionally use the essential cut to make it fit. | Premieres with catch-up time, your watches, essential cut | Small |
| 3 | **Is it worth going back?** | For a show you dropped, compare the ratings after where you stopped with the ones you watched: "it gets better from season 3", or "it doesn't recover". | Episode patterns, your progress, the "dropped" list in Your taste | Small |
| 4 | **Character trail** | Follow a character, not a show: everywhere Karen Page or Frasier Crane appears, in air order, with your progress. | Universes' character matching, watch order | Medium |
| 5 | **Previously on…** | Before you resume a show after a break, a spoiler-safe recap built from the summaries of the last few episodes you watched, and nothing after them. | TVmaze episode summaries, your progress | Medium |
| 6 | **Reunion radar** | From shows you love, find shows where two or more of their actors work together again, ranked by how big the roles are. | Cast and people, your ratings | Medium |
| 7 | **Spoiler shield** | Hide titles, summaries and images of episodes you haven't watched across the whole app, with a tap to reveal. | Your progress, the essential cut's title hiding | Medium |
| 8 | **Taste twins** | Find users whose ratings track yours, and what they loved that you haven't seen. Shown only with enough overlap to keep it anonymous. | Ratings, the 5-user privacy rule, Recommendations | Large |
