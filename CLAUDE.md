# TvTracker

## Agent skills

### Issue tracker

Issues and specs live as markdown files under `.scratch/<feature>/` in this repo, not in GitHub Issues. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default roles, recorded as a `Status:` line in each issue file: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` at the repo root and decisions under `docs/adr/`, both created when first needed. See `docs/agents/domain.md`.

## Where things live

- A service's model, `DbContext`, design-time factory and migrations are in `src/Services/X/X.Data` (namespace `X.Data`), not in `X.API`. Add a migration with `dotnet ef migrations add <Name> --project src/Services/X/X.Data --output-dir Migrations`.
- The per-show insight rules (worth it, gets good, endings, forecasts, DNA, tags), their maths, the facts they read and the `ShowInsights` result types are in `src/TvTracker.ShowInsightsEngine` (namespace `TvTracker.ShowInsightsEngine`). It has no database, HTTP, flag or messaging code and must not gain any: `Analytics.Worker/Insights` loads the facts (`InsightSourcesLoader`), runs the rules (`InsightsPipeline`), and writes the lists and publications. The per-viewer insights computed on request are still in `Analytics.API/Apis`.
