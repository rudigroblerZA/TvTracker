# Sample history exports

Made-up viewing histories in each format the import page (`/settings/import`) reads, for trying it out. Real exports
hold people's account data, so these were built to the layouts the parser expects.
The shows use real TheTVDB ids, checked against TVmaze's `lookup/shows?thetvdb=`, so they resolve to the right shows; the
IMDb files use real IMDb ids.
Times are made up, mostly in 2024.

| File | Format | What's in it |
| --- | --- | --- |
| `tv-time-gdpr-export.zip` | TV Time's GDPR archive | `tracking-prod-records-v2.csv` (watches, a rewatch, `user-series-*` follows: watching, a planned Silo, a stopped The Wire, and Friends season 1 bulk-marked at one moment), `followed_tv_show.csv`, the older `seen_episode.csv` (The Office), and two account files (`user_devices.csv`, `login_tokens.csv`) that the importer must never open |
| `tv-time-out-episodes.csv` | TV Time Out / Liberator, episodes | Game of Thrones and Barry season 1 by `series_tvdb_id`, with an unwatched row that's skipped |
| `tv-time-out-series.csv` | TV Time Out / Liberator, series | Statuses: up to date, continuing, not started yet (planned), stopped |
| `trakt-export.json` | Trakt (traktexport layout) | `history` (Better Call Saul season 1), `watched` (Lost, last-watched times) and `watchlist` (Silo, planned) |
| `generic-by-title.csv` | Any CSV, titles only | Succession and The Bear matched by title and year, and "Ghosts (2021)", whose year in the title picks the US show |
| `imdb-ratings.csv` | IMDb's ratings export | Five series rated 5 to 10 (Chernobyl a mini series), an episode (Ozymandias) and a movie, both skipped |
| `imdb-watchlist.csv` | IMDb's watchlist export | Silo and Shōgun (planned), The Bear with a rating of 9, and a movie |

What the parser reads from each (checked with `HistoryImportParser`):

- **TV Time archive:** 8 shows, 83 watches, 7 follows.
- **TV Time Out episodes:** 2 shows, 18 watches.
- **TV Time Out series:** 4 follows.
- **Trakt:** 3 shows, 22 watches, 1 follow.
- **By title:** 3 shows, 19 watches.
- **IMDb ratings:** 5 shows, 5 ratings (5, 4, 5, 4 and 3 stars), 2 rows skipped.
- **IMDb watchlist:** 3 shows, 2 follows (planned), 1 rating.

Importing is a no-op the second time: already-watched episodes are left alone.
