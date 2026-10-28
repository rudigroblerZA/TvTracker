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
- **For two:** plan an evening with someone you follow, from the shows you share. It picks up where whoever is
  behind left off, so nobody skips an episode, and prefers shows you're level on over ones where one of you rewatches.

### 11. Reunion radar

`/reunions`, linked from For you.

- **What it finds:** shows where two or more actors from a show you love work together again. NCIS leads to Chicago
  Hope (Mark Harmon, Rocky Carroll and Lauren Holly), The Wire to Oz, Treme and Bosch, and Ted Lasso to Drifters.
- **Across TVmaze:** it looks at everything the actors have been in, not just the catalog. A show we don't have yet
  is imported when you open it.
- **Ranked by how much it should matter to you:** your 5-star shows over your 4-star ones, and leads over supporting
  cast. "New and coming soon" comes first: that's the radar.
- **Only real reunions:** a spin-off or revival (anyone playing the same character) belongs to Universes, so it isn't
  counted. Neither are shows you already know.

### 12. Show DNA

On show pages, and behind every recommendation.

- **What it is:** 12 traits of how a show is made and how it plays out, each on a scale between two plain ends: episode
  and season length, how much there is to watch, weekly or all at once, the wait between seasons, era, acclaim,
  steady or rollercoaster, whether it gets better or fades, where it peaks, how big its finales are, and whether our
  users finish it.
- **Only what sets it apart:** the page tags the traits that set a show apart from a typical one. Breaking Bad is
  "Acclaimed, Peaks late"; Stranger Things "Drops all at once".
- **Better recommendations:** similar shows now rank by DNA among shows about similar things.
  - Breaking Bad finds Mr. Robot, The Wire, The Sopranos and Boardwalk Empire.
  - Ted Lasso finds Resident Alien, The Marvelous Mrs. Maisel, Hacks and Shrinking.
- **Reasons:** each says what's shared: "Same DNA: short episodes, long seasons" (The Office → Frasier, Seinfeld).
- **DNA twins:** "Made like this, about something else". Shows built the same way in other genres, each with what
  they share and what it's about instead. Ted Lasso finds Silo and Andor (long waits, big finales); The Office finds
  Scrubs and Brooklyn Nine-Nine (short episodes, long seasons); Game of Thrones finds Boardwalk Empire and Justified
  (acclaimed, fades over time); Stranger Things finds MINDHUNTER and Narcos (drops all at once, acclaimed).

### 13. What's next, with honest forecasts

On show pages and the Stats page.

- **For a running show:** whether it's likely to come back, when the next season starts, and how it'll rate. Ted
  Lasso: 86% of 135 comparable seasons got another one; if it does, season 5 is likely around March 2028; it's likely
  to rate about 8.1.
- **Backtested every hour:** each forecast is checked against what actually happened in the catalog's past seasons, and
  its range is where 8 in 10 of those checks landed. Where the data says little (renewal), it says so.
- **What it found:** a show's trend does *not* predict its next season (right only 45% of the time); seasons drift back
  towards the show's average instead, so that's how ratings are forecast.
- **Will you finish it?** For every show you're part-way through:
  - your pace, and the date it gets you to the end;
  - whether you've stalled;
  - how you did with other shows from as far in;
  - where other viewers give up.

### 14. Nanogenres

Chips on show pages, `/nanogenres`, and a search filter.

- **Not just comedy:** Ted Lasso is a sports comedy. Wednesday is a supernatural comedy and a mystery comedy. The
  Simpsons is an animated family comedy.
- **Found, not listed:** the catalog groups its own shows by what they're about and what kind of show they are, and
  names each group from the genres most of it shares: medical drama, cooking reality, home-renovation reality,
  western drama, spy thriller, action horror (The Walking Dead and The Last of Us).
- **Honest about gaps:** a group with nothing specific in common gets no name rather than a vague one.
- **Recommendations:** shows in the same nanogenre rank higher, with the plainest reason: "Also a sports comedy".

### 15. When it gets good

On show pages, in "Will you finish it?", and a Discover list.

- **The question everyone asks:** "how long until it gets good?" Answered from the episode ratings, not from forums:
  The Office gets good at S02E03 "Office Olympics" (4 hours in), Succession at S02E01, The Wire at S01E07 "One
  Arrest", Community and The Good Place at their seventh episodes, Severance at S01E06 "Hide and Seek".
- **Honest both ways:** Breaking Bad and Stranger Things are "Good from the first episode". A show whose start is only
  a little weaker, or that never clearly turns, gets nothing said about it.
- **Personal:** if you've stalled before the turn, "Hang on: it gets good in 2 episodes (1 h 29 min)".
- **Worth the wait:** a Discover list of well-rated slow starters and where they turn: Person of Interest (S01E04),
  Dark (S01E05), Slow Horses (S02E04), Severance (S01E06).

### 16. Creator fingerprint

On person pages.

- **A creator's style, measured:** the DNA of everything they created or developed, averaged into a fingerprint.
  Mike Flanagan's shows all drop at once and are a short watch; Dick Wolf's have long seasons, run for years and come
  back every year; Vince Gilligan's are acclaimed and peak late.
- **Honest about range:** Ryan Murphy has no single style; the page shows their range instead (a short watch to a long
  commitment, weekly to all at once, steady to rollercoaster).
- **Made the Mike Flanagan way:** shows by other people with the same fingerprint, like 3 Body Problem and Time. Made
  the Chuck Lorre way: The Goldbergs, Last Man Standing, Superstore.
- **Similar creators:** Mike Flanagan's style is closest to Guy Ritchie's and Neil Gaiman's; Steven Knight's to Jed
  Mercurio's and David Farr's.

### 17. TV time machine

`/time-machine`, with a teaser on Home.

- **This week in TV history:** what premiered and ended this week in past years ("Once Upon a Time premiered 15 years
  ago", "Doc Martin ended 4 years ago"), and each year's best episode from this week.
- **Travel to any date:** a birthday, an anniversary. The week of 22 September 1994: Friends and ER premiered, and
  Seinfeld, Frasier and The X-Files were on air. The date is in the link, so a trip can be shared.
- **Personal:** episodes you've already watched are marked.

### 18. Does it stick the landing?

On ended shows' pages, and a Discover list.

- **The question before starting a finished show:** is the ending worth it? Answered from the ratings, without
  spoilers: the finale is never named.
- **It agrees with the reputations:** Game of Thrones, Dexter and How I Met Your Mother fizzle out (their finales rate
  lower than any other episode, in their weakest seasons). The Americans, Succession and Better Call Saul stick the
  landing. The Office has a great finale after a weaker last season; Seinfeld's finale lets it down.
- **Great endings:** finished shows to start with confidence, on Discover.

### 19. Previously on…

On the show page and Up Next, after a break.

- **The moment it's for:** you stopped a show months ago and a new season starts soon, or you want to pick it up
  again. It says where you were ("S03E06 · Sunset, 7 months ago") and picks a rewatch that fits 1, 2 or 4 hours.
- **What it picks:** that season's premiere, the episode before you stopped, its best episodes and last season's
  finale, with your own 5-star episodes first. Breaking Bad at 4 hours: No Más, I.F.T., Más and Sunset.
- **Spoiler-proof:** it only picks from episodes you've watched.
- **Back after a break:** Up Next lists the shows due a refresher, the ones returning soonest first.

### 20. Catch-up planner

On the show page and Up Next, before a followed show's new season.

- **The question:** "can I be caught up before it starts?" It says what's left ("58 episodes, 58 h"), the pace that
  gets you there ("about 6 a week"), and how that compares with how much you actually watch.
- **Honest verdicts:** comfortable, doable, tight or behind, with how late you'd finish at your own pace.
- **A shorter path:** the essential cut, when it saves real time, with the weeks replanned along it.
- **Week by week:** "This week: S05E11–E16", split by running time.

### 21. Is it worth going back?

On the show page and the backlog, for shows you dropped.

- **The question nobody answers:** you stopped a show months ago; does what's left get better? It compares what's left
  with the stretch where you stopped, which is likely why you stopped.
- **Real answers:** The Office "gets good soon" (S02E03, 4 episodes after where you stopped at S01E04), Foundation "gets
  better from season 2", Dexter after season 4 "doesn't recover".

### 22. Backlog triage

At `/backlog`.

- **Lists only grow:** this ranks the shows you follow but haven't started by how much they're likely worth your time,
  and says plainly which to start next and which to let go.
- **Explained:** each show says why: its rating, whether it's your kind of show, how it ends, a slow start.
- **The honest total:** "766 h on your list, 7 years at your 2 h a week; letting go saves 95 h, essential cuts 125 h".

### 23. Fits your time off

At `/time-off`.

- **A holiday-sized pick:** finished shows you can watch start to end in the time you have, so you get the ending too.
- **Or a mix:** two or three that fill the time together.

### 24. Spoiler shield

In Settings.

- **Everywhere at once:** titles and summaries of episodes you haven't watched are hidden across the app, and one tap
  shows one.

### 25. Where do I know them from?

On every show page's cast.

- **The question people look actors up for:** under each actor you've seen before, the shows you saw them in and who
  they played: open Better Call Saul and Bob Odenkirk reads "You know them from Breaking Bad as Saul Goodman".
- **From your own history:** shows you watched or rated, not an actor's whole career. One tap filters the cast to the
  faces you know.
- **Spoiler-aware:** a show you're partway through says how far you are, since they may only arrive later, and the
  spoiler shield hides it until you ask.

### 26. Holiday episodes

At `/holidays`, and on Home when one is near.

- **Your shows' Halloween episodes, in time for Halloween:** and Thanksgiving, Christmas, New Year's Eve and
  Valentine's Day. Every holiday episode of the shows you follow, plus the best known (Treehouse of Horror, The Office's
  "Halloween") and shows made for the season.
- **Safe to watch tonight?** Each one says where it sits for you: up next, one you skipped, already watched, or ahead
  of you. A sitcom's usually stands alone; a drama's ahead of you is "ahead of you in the story", and its title stays
  behind the spoiler shield.
- **Nothing to maintain:** found from episode titles and summaries, so new seasons' episodes join by themselves.

### 27. Social: following, the feed and watching together

At `/feed`.

- **Follow people, see what they watch:** episodes (a binge as one line), finished shows, new follows and ratings,
  without episode titles, so nothing is spoiled.
- **Privacy that's simple:** stop sharing, and none of your activity shows anywhere, past items included. Or go private
  and approve followers yourself, and block anyone.
- **On every show page:** which people you follow watched it, how far they got and what they rated it.
- **Watching together:** with anyone you follow, the shows you share and who's ahead, from your whole history.
- **Shared lists:** tick one of your lists to show it on your profile.

### 28. Viewers help each other, and keep it civil

- **Tips from viewers:** on show and episode pages, what people who watched it want you to know ("slow start; it
  gets good at episode 3"), the most helpful first.
- **Fix the data:** suggest a correction to a summary, title, air date or runtime; once a moderator accepts it, it
  sticks, even when TVmaze refreshes the show.
- **Flags that do something:** enough flags hide a post until a moderator looks, and spoiler flags mark it a spoiler.
- **A light touch:** blocked words, links held for review, hourly limits, and a moderation page with a log.

### 29. Research: a show's trusted record

On show pages, written by moderators at `/research`.

- **Checked by a person, with its sources:** a short insight citing numbered sources ([1] Wikidata, [2] Wikipedia,
  a review someone added), and checked facts: creators, when it ended, seasons and episodes, awards, IMDb and TMDB
  links. "Checked by App Admin, 27 October 2026 · version 2".
- **Sources gathered for you:** Wikidata by the show's TVmaze id, the Wikipedia summary and TMDB, plus any page a
  researcher pastes.
- **A local model drafts, a person decides:** the model writes a first draft from the sources; the researcher edits it,
  and picks each fact from the catalog, a source, or their own.
- **The saved copy wins:** a checked summary, network or premiere date replaces TVmaze's in the catalog and stays
  through every refresh. Breaking Bad's network reads "AMC (United States)" until a later version drops it.
- **Also for questions:** the MCP tools answer from the saved research, so "what awards did Breaking Bad win?" comes
  from checked facts.

### 30. Ask the app

At `/ask`.

- **Ask in plain words:** "Landman insights", "hidden gems in drama", "which seasons premiere in the next month?". The
  answer comes from the app's own data: a model running on our servers looks things up with the same tools Claude Code
  and Open WebUI use, then answers, linking the shows it names.
- **Shows its work:** every answer lists what it looked up, so you can check it. Follow-up questions work ("and Better
  Call Saul?").
- **Honest about its size:** the default model is tiny (0.6B parameters, runs on a CPU). It finds the right data, but its
  reading of it can be off, and the page says so; a bigger model is one setting away.

### 31. Smaller touches

- **Premieres:** returning seasons come with the catch-up time (episodes and hours to watch first), and how many of
  our users followed the show recently.
- **Actor pages:** a person's shows' average rating, their best show, the years they worked, and co-stars they share
  two or more shows with.
- **Privacy:** anything computed from our users' activity needs at least 5 users or ratings behind it.
- **Light or dark:** one tap in the header, or follow the device (Settings).
- **Flags from the app:** admins turn features on or off for everyone from Settings, without editing files.
- **Your name on it:** the app's name is a setting, so it can be white-labelled without code changes.

## Ideas for next

Ranked by value for the effort. Each builds on data and code that already exist.

| # | Idea | What it does | Builds on | Effort |
| --- | --- | --- | --- | --- |
| 1 | **Crossover alerts** | Tell you when a crossover event is scheduled in a universe you're watching ("Reckoning airs across Fire, Med and P.D. on Wednesday"), so you're caught up on all three shows first. | Watch order's crossover detection, follows, Notifications | Small |
| 2 | **Character trail** | Follow a character, not a show: everywhere Karen Page or Frasier Crane appears, in air order, with your progress. | Universes' character matching, watch order | Medium |
| 3 | **Taste twins** | Find users whose ratings track yours, and what they loved that you haven't seen. Shown only with enough overlap to keep it anonymous. | Ratings, the 5-user privacy rule, Recommendations | Large |
