# Taste Match: spec

Status: built, behind the `taste-match` flag, which is off. Section 15 says what exists, where it differs from the
spec above, and the three things left out on purpose. Section 6.1's numbers were superseded by what the code does,
described there.

## 1. Summary

A member sends a friend a link. The friend opens it, picks a few shows they love (or signs in, if they are a member),
and both get a result: how well their TV tastes match, what they have in common, where they differ, and a short list of
shows they would both like. The result is a page with its own address and a card made to be posted.

It exists to bring people in. Every use needs a second person, so every use is an invitation. It is aimed at 18 to 28
year olds, who already compare and post taste elsewhere.

## 2. Goals and non-goals

Goals:

- G1. The friend gets a real result **without an account**, in under a minute, on a phone.
- G2. The result is worth posting: one number, a line that says something, and posters.
- G3. The result gives both a reason to stay: shows to watch together, one tap from following them.
- G4. Nothing about either person is shown that they did not choose to put in.
- G5. Reuse what exists: the mix engine, the taste rules, the tokened public link, the invite flow.

Non-goals:

- A social network. A match is between two people who already know each other; there is no browsing of strangers.
- Group matches (three or more). Worth doing later; it is Mixtape's shape, not Blend's.
- A precise measure of compatibility. It is a conversation starter with honest inputs, not a science.
- Matching on anything but TV: no age, location or free text.

## 3. Terms

| Term | Meaning |
|---|---|
| Inviter | The member who starts a match. |
| Friend | Whoever opens the link: a guest (no account) or another member. |
| Side | One person's input to a match: a weighted list of shows they love. |
| Loved show | For a member: rated 4 or 5 stars, or followed or watched and not rated lower, weighted as Reunion radar does (`Reunions.LoveWeight`: 5 stars 1, 4 stars 0.8, followed or watched 0.5). For a guest: each pick, weight 1. |
| Picks | The shows a guest chooses, 3 to 8. |
| Match | The result for two sides. |

## 4. How it goes

### 4.1 The inviter

1. From their profile, For you, or the end of any Blend: "Match your taste with a friend".
2. They see what will be used: "your top-rated and most-watched shows" with a count, and a preview of the up to 8 shows
   that could be named in a result. They can remove any show from that list before sending (R-P3).
3. They get a link (`/match/{token}`) and the share sheet. One link is one match with one friend.
4. They are told when the friend finishes (inbox, and push if on), and the link then opens the result for them.

A member needs at least 5 loved shows to start one. Below that: "Rate or follow a few more shows first", with a link
to search.

### 4.2 The friend, as a guest

1. The link opens "*Name* wants to match TV taste with you" with the inviter's first name and avatar. Nothing else
   about the inviter is shown yet.
2. "Pick shows you love": a search box (title suggestions, as Blend's pickers) over a grid of popular shows to tap.
   3 picks unlock the result; up to 8 are taken. Optional: a first name, shown on the result.
3. "See your match". The result page (section 5).
4. Below the result: "Keep your picks: make an account" (their picks become follows), and "Match with someone else".

No account, email or sign-in is asked for before the result. This is the rule the feature depends on (G1).

### 4.3 The friend, as a member

Signed in, the same link shows "Match with *Name* using your shows?" with the same preview and removal as 4.1 step 2,
and one button. A signed-out member can sign in from the picker and come back to the same link.

### 4.4 States of a link

| State | Opening it shows |
|---|---|
| Open | The friend's flow (4.2 or 4.3). The inviter sees "waiting for your friend" and can copy the link again or cancel. |
| Done | The result, to anyone with the link (R-P1). |
| Cancelled or expired | "This match is no longer open", and a way to start one. |

An open link expires after 30 days. A done match is kept for 12 months, then deleted.

## 5. The result

One page, top to bottom:

1. **The number and the line.** "82% match" and one sentence chosen by rule (section 6.3): "You'd fight over the
   remote for about four minutes."
2. **Both love.** Up to 4 shows on both sides, as posters. Left out when there are none.
3. **You're both into.** Up to 3 genres or nanogenres strong on both sides.
4. **Where you differ.** One thing each: "*A* is the one who watches reality; *B* brings the sci-fi." From the largest
   gaps between the two sides' genre weights.
5. **Watch together.** Up to 6 shows neither side has, that sit between the two tastes (section 6.2), each with one
   line of why ("Crime like *A*'s, comedy like *B*'s") and a follow button.
6. **Share.** The card (5.1), "Copy link", and for the inviter "Match with someone else".

For two members who follow each other and share activity, a seventh block links to Tonight for two.

### 5.1 The card

An image for posting: both first names (or "You" and "A friend" when none was given), the number, the line, and up to
4 posters of shows both love, or of the top "watch together" shows when there are none. Two sizes: 1080×1920 (stories)
and 1200×630 (link previews). The 1200×630 one is the page's link-preview image, so a pasted link shows the result.

The card never shows anything the page does not.

## 6. Rules

All of section 6 is pure maths over show ids, weights and catalog facts. It belongs in the engine with unit tests
(section 9).

### 6.1 The score

Three parts, each 0 to 1:

| Part | What | How | Weight (start at) |
|---|---|---|---|
| Alike | How close the two sets of shows are in meaning | For each loved show on one side, its similarity to the nearest loved show on the other (1 − cosine distance of the search vectors, rescaled so the model's "similar" cutoff is 0 and identical is 1), weighted by love; averaged both ways | 0.5 |
| Kinds | Whether they watch the same kinds of thing | Cosine similarity of the two sides' genre weights (each genre's share of that side's love) | 0.3 |
| Shared | Shows both love | Sum of the smaller weight for each show on both sides, over the smaller side's total | 0.2 |

`score = round(100 × (0.5·alike + 0.3·kinds + 0.2·shared))`.

- R-S1. A guest with 3 picks and a member with 300 loved shows must get a sensible score: every part is a share or an
  average, never a count, and the member's side is cut to their 50 heaviest shows before scoring.
- R-S2. Order must not matter: A with B scores the same as B with A.
- R-S3. The same inputs give the same score, and a match once done never changes (it is a record of that day).
- R-S4. No score is shown below 15 or above 99: the floor avoids an insult from thin input, and nobody is 100%.
- R-S5. When either side has under 3 usable shows (not in the catalog, or not embedded yet), there is no match:
  the friend is asked for one more pick.

### 6.2 Watch together

The mix engine (`Catalog.Mixes`), which Blend and Mixtape already use: up to 3 of each side's heaviest loved shows,
mixed with each side counting half, whatever its size. Candidates either side already loves, follows or has watched
are left out. The reason line is Blend's genre split, by person instead of by show.

- R-W1. A loose mix (the engine's own flag) still returns shows, with the line "Not much overlap, so these are the
  middle ground."
- R-W2. Fewer than 3 results after exclusions: the block is left out, not padded.

### 6.3 The line

Chosen by score band, then by the first rule that applies, so it is deterministic and testable. Bands start at:
90+, 75 to 89, 55 to 74, 35 to 54, under 35. Each band has a few lines, picked by a hash of the token so a pair always
sees the same one. Lines are friendly at every band: a low score reads as "you'd broaden each other's lists", never as
a put-down. The copy is a list in the engine, reviewed as copy.

## 7. Privacy and safety

- R-P1. **A done match is readable by anyone with the link.** That is what makes it shareable, and it is why what it
  shows is limited. The token is long and random, as shared lists' are, and the page is marked not to be indexed.
- R-P2. **What a result may name:** shows both love; up to 3 genres each; the suggestions. It never lists one side's
  shows wholesale, and never shows ratings, watch counts, dates, or anything from the other insights.
- R-P3. **Consent before use.** Each member sees the shows that could be named and can remove any before their side is
  used (4.1, 4.3). Removed shows are still scored, but never named.
- R-P4. **A member who doesn't share activity** can still match (they started it, or accepted it), and R-P3 is their
  control. The "Tonight for two" block follows the existing visibility rule and is simply absent.
- R-P5. **Guests.** A guest's picks and optional first name are stored only on that match and deleted with it. No
  cookie is needed to see the result: the link is the key. If the guest signs up from the result, the picks are offered
  as follows; nothing is carried over silently.
- R-P6. **Deleting.** Either side can delete a match from the result page (the inviter always; the friend if signed in,
  or from the device that made the picks). A deleted member's matches are deleted, both open and done: the other
  person loses the result, which is the right way round.
- R-P7. **Erasure and export.** The match table holds member ids, so it must be covered by the owning service's
  `UserDeletedIntegrationEvent` handler and its data export; `Privacy.IntegrationTests` will fail until it is.
- R-P8. **Blocks.** A member can't open a match started by someone they have blocked, or who blocked them; it reads as
  expired.
- R-P9. **Names.** First names only. A guest's name is plain text from a stranger: length-limited, shown escaped, never
  in a link preview title, and replaceable by the inviter with "A friend".
- R-P10. **Age.** The picker and the suggestions leave out adult-rated shows when the viewer is signed out.

## 8. Abuse and limits

- Starting a match: signed in only; at most 20 open links per member, and a per-member rate limit.
- Opening a link and submitting picks: anonymous, rate limited by client address as shared lists are
  (`ClientRateLimiting`), with a tighter limit on submit.
- One submission per link. A second device opening a done link sees the result, not the picker.
- Tokens are not guessable and not sequential; an unknown token and an expired one answer the same.
- The suggestions call the mix engine once per match, at submit. The result is stored, so viewing costs one read.

## 9. Where it goes

| Part | Where | Why |
|---|---|---|
| Score, bands, lines, what may be named | `src/TvTracker.InsightsEngine/Viewer/TasteMatch.cs`, tests in `tests/Analytics.UnitTests` | The repo's rule: rules and their maths are the engine's. |
| Match table, start / open / submit / delete endpoints | Analytics (`Analytics.Data`, `Analytics.API/Apis/TasteMatchApi.cs`) | It already holds copies of every member's ratings, follows and watches, which is exactly a side. |
| Alike and the suggestions | Catalog: one endpoint taking two weighted lists of show ids, answered from `Catalog.Mixes` and the vectors | The vectors are Catalog's. It receives ids and weights only: no member data. |
| Analytics calling Catalog | A typed client with a service token, as Catalog.API calls the sync worker | The page must not be handed either side's list to pass along. |
| Pages | `TvTracker.UI`: `/match` (start), `/match/{token}` (friend's flow and result) | Shared by web and the mobile app. |
| Card images | Rendered on the server from the stored result | One renderer for both sizes and the link preview. |
| Flag | `taste-match`, off by default until launch; free | It is the acquisition loop; a plan gate would defeat it. |

Stored per match: token, state, inviter id, friend id (null for a guest), guest name and picks (guest only), each
side's removed-from-naming show ids, the result as a document, created / done / expires. The sides themselves are not
stored for members: they are read at submit and only the result is kept.

## 10. API (shape, not final)

| Call | Who | Does |
|---|---|---|
| `POST /api/v1/me/matches` | member | Starts one; returns the token and the preview of nameable shows. |
| `GET /api/v1/me/matches` | member | Their open and done matches. |
| `GET /api/v1/matches/{token}` | anyone | The state, and either the invitation (inviter's first name, avatar) or the result. |
| `POST /api/v1/matches/{token}/picks` | anyone | A guest's picks and optional name; computes and stores the result. |
| `POST /api/v1/matches/{token}/accept` | member | A member friend's side, with their removals; computes and stores the result. |
| `DELETE /api/v1/matches/{token}` | inviter, or the friend | Deletes it. |
| `GET /api/v1/matches/{token}/card.png?size=` | anyone | The card. |
| `POST /api/v1/shows/match` (Catalog) | services only | Two weighted id lists in; alike, kinds, shared and the suggestions out. |

## 11. Measuring it

The feature is judged on whether it brings people in, in this order:

1. Links completed ÷ links started (does the friend finish?). Under half means the guest flow is too heavy.
2. Guests who make an account within a day of their result.
3. New members from a match who are still tracking a month later, against other new members.
4. Matches started per member who has started one (does anyone do it twice?).
5. Follows from "watch together" (the existing `follows.started` metric, with a new source `match`).

Counts only, as elsewhere; no per-person funnel is stored.

## 12. Tests

- Engine: each part of the score at its extremes; symmetry (R-S2); a 3-pick side against a 300-show side (R-S1); the
  floor and ceiling; band edges; a line is stable per token; removed shows are scored and never named; nothing is named
  beyond R-P2's list.
- Catalog: the match endpoint against seeded vectors (alike for identical, disjoint and overlapping sets; exclusions in
  the suggestions).
- Analytics, against a real database: the states of a link; one submission per link; expiry; delete by each side; a
  blocked pair; a deleted member's matches gone (the privacy tests); the export.
- UI: the guest flow end to end signed out, on a phone-width viewport; the result page; a done link opened by a third
  person.

## 13. Slices

Each is shippable behind the flag and useful to test on its own.

1. The engine: score, bands and lines, with tests. No storage, no UI.
2. Catalog's match endpoint over the mix engine.
3. The match table, start / open / submit / delete, privacy handlers and export. Guest friends only.
4. The pages: start, the guest picker, the result. No card yet (share the link).
5. Member friends (accept with removals), blocks, inbox notice.
6. The card, both sizes, and the link preview.
7. Entry points (profile, For you, end of Blend), the metrics, and the sign-up hand-off that turns picks into follows.

A usable test with real friends exists after slice 4.

## 14. Open questions

- **Weights and bands.** Section 6's numbers are guesses. Before launch, run the score over a few hundred real member
  pairs and look at the spread: if most pairs land between 60 and 70, the number says nothing and the weights need
  changing.
- **How good is a 3-pick side?** If results from 3 picks feel random, raise the minimum to 5 and accept the lost
  completions. Slice 4 answers this.
- **Is a public result acceptable** (R-P1), or should a done match need the link *and* be hidden from search only? The
  spec assumes the link alone, as shared lists work today.
- **Where does a guest's result live after they sign up?** Attaching it to the new account is friendlier; it also turns
  a guest record into member data on sign-up, which needs saying at that moment.
- **Card rendering.** Server-rendered images are new to this codebase. Whether to draw them in process or with a
  headless browser is a decision with operational cost either way.
- **Moderation of guest names.** A block list, or no free-text name at all for guests?

## 15. Built (27 November 2026)

Everything below was tried in the running app with the flag on.

- **The rules** (`TasteMatch` in the engine): sides, the three parts, the score with its bounds, the lines, what may be
  named, the suggestions' reasons. 16 unit tests.
- **The catalog half** (`TasteRules`, `TasteMatcher` in `Catalog.Mixes`; `POST /api/v1/shows/taste-match`, services
  only): alike by meaning, and the suggestions from the mix engine. 6 unit tests.
- **Matches** in Analytics (`taste_matches`, `TasteMatchApi`): start, list, set the shows not to name, open by link,
  answer as a guest, accept as a member, claim (a guest who made an account), delete. Erasure and the data export
  cover the table. 18 tests against a real database (`TasteMatchApiTests`).
- **The card** (5.1): `MatchCard` draws it in process with SkiaSharp and an embedded typeface (Inter, SIL Open Font
  License), in both sizes, with up to four posters of what the two both love, or else of what they could watch
  together. `GET /api/v1/matches/{token}/card.png?size=story|link` on Analytics, passed on by the web app at
  `/match/{token}/card.png`. The drawing is kept in memory for a day; the match is read on every request, so a deleted
  match's card stops at once (browsers and preview services may keep theirs for ten minutes). A `/match/{token}`
  page's head carries the link-preview tags and asks search engines not to index it. 6 unit tests.
- **The inviter is told** (4.1): answering stores `TasteMatchAnsweredIntegrationEvent` with the result, in the same
  save. Analytics.API has no event bus, so the worker's outbox publisher sends it, within a minute; Notifications
  turns it into an inbox item linking to the match. Inbox only: no push or email.
- **Blocks** (R-P8): before a signed-in member opens, answers or claims a match, Analytics asks Social (services-only
  `internal/blocks/between`) whether there is a block between them and either of the match's two people; if so the
  match reads as gone, to the inviter too once a member friend has blocked them. A signed-in member can't get round
  it by picking shows as a guest would.
- **A guest who signs up** (4.2, R-P5): the sign-up link carries their key back to the match; signed in, they are asked
  whether to keep it (a link with a key in it never puts a match on the account of whoever opens it), and on saying
  yes attach the match to their account (it then shows in their matches, and they can delete it), and are offered their picks as
  follows. Nothing is followed unless they press the button.
- **Tonight for two** (5): each of two members is told the other's id (nobody else is), and the result links to
  planning an evening together when that feature is on. The page it opens applies its own visibility rule.
- **Pages:** `/match` and `/match/{token}`; entries on For you and under a Blend's results.
- **Measures** (11): counters `matches.started`, `matches.answered` (guest or member) and `matches.claimed` (meter
  `TvTracker.Analytics.TasteMatch`), and the follow source `match` on the suggestions and on followed picks.

Where the code differs from the spec above:

- **The score (6.1).** The spec's "alike" scaled closeness to search's cutoff. On the 48 local test members that put
  the median pair on the floor. Closeness is now measured against how far apart two unrelated shows are in the
  catalog (the median distance between pairs of its 200 most popular shows, so it follows the model), and the
  weighted parts are raised to 0.6 before becoming a percentage. On those members, pairs who watch the same kinds of
  show score 59 to 89 (median 76) and pairs who don't, 31 or under. Tuned on that sample only.
- **No avatar** on the invitation: first name only. Showing it would put the inviter's user id in a public answer.
- **Expiry** is enforced on read, and expired matches are deleted whenever anyone starts a match, not by a timer.
- **Rate limiting** reuses the shared-list link policy for every anonymous call; there is no tighter limit on submit
  beyond the global limit on writes.
- **A guest's key** lives in the page's memory and in the sign-up link: closing the tab without signing up loses the
  ability to delete or claim the match.
- **The card's names** are the stored first names ("A friend" when none was given), never "You": the picture is the
  same for everyone.
- **The link preview's title and description are generic**: the page is drawn after the browser connects, so only the
  picture is specific to the match.
- **When Social can't be asked about blocks**, a signed-in member can still read a match, but neither of the two is
  told who the other is, and answering or claiming is refused with "try again". Guests are unaffected.
- **Two answers at once:** the row carries a version (Postgres's xmin), so only the first to save wins; the other
  finds the link gone and the inviter is told once.
- **A name the card's typeface can't draw** (another script, an emoji) is replaced by "A friend" on the card; the
  page still shows it.

Left out on purpose:

- **Adult-rated shows left out for guests** (R-P10): the catalog has no such flag to go by.
- **An entry on the profile** (4.1): there is no page of one's own profile to put it on; For you and Blend have it.
- **The funnel's later steps** (11, items 2 to 4: accounts made within a day, still tracking a month on, repeat
  use): they need joining counts over time, which is a dashboard's job, not the app's. The counters above are what
  it would read.

Not tested automatically: the pages (walked through by hand and with a browser script), and how real services draw
the link preview, which needs a public address.
