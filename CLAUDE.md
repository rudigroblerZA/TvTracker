# CLAUDE.md

Guidance for Claude Code in this repository. Architecture, domain terms and the reasons behind non-obvious code are in
CONTEXT.md; how to run it and what each feature does for users is in README.md.

@CONTEXT.md

## Commands

```bash
# Build: the solution includes the MAUI app, which needs the MAUI workload. Without it, build the AppHost (it
# references every service and the web app) and the CLI.
dotnet build src/TvTracker.AppHost
dotnet build src/Tools/Catalog.ImportCli

# Run everything (Docker or Podman must be running). Web app https://localhost:7120, demo / demo.
dotnet run --project src/TvTracker.AppHost

# Migrations (Catalog and Analytics only; dotnet-ef is pinned in dotnet-tools.json)
dotnet tool restore
dotnet ef migrations add <Name> --project src/Services/Catalog/Catalog.Data --output-dir Migrations
dotnet ef migrations add <Name> --project src/Services/Analytics/Analytics.Data --output-dir Migrations

# Bulk import while the AppHost runs
dotnet run --project src/Tools/Catalog.ImportCli -- ids 82 431
```

There are no tests or CI. Verify changes by building, then running the AppHost and checking endpoints, the
database or the UI. `http://localhost:5102/ops` shows catalog, sync and analytics state at a glance.
Set `Catalog:Provider=mock` in `Catalog.SyncWorker` when TVmaze is down or not needed.

## Conventions

- **Code style:**
  - Minimal APIs under `/api/v1/...`, with `TypedResults` and `Results<...>` return types.
  - Endpoints are grouped in static `XxxApi` classes in an `Apis/` folder.
  - Records for DTOs and events, primary constructors for services, file-scoped namespaces.
  - Package versions are central, in `Directory.Packages.props`.
- **Comments:** XML doc comments explain behaviour and the why, in plain sentences (thresholds, privacy rules, provider
  quirks). Match that density; don't narrate the obvious.
- **Events:**
  - Change a contract only by appending optional parameters.
  - Publish through the outbox in the same transaction as the change.
  - Handlers start with `HasProcessedAsync` and end with `MarkProcessed`.
- **Ids the app sets** need `ValueGeneratedNever()` in the model.
- **TVmaze calls** share one 20-per-10 s limiter. Background work must be throttled, and a failure for one show must not
  stop a loop (catch per item, retry later).
- **Privacy:** anything Analytics shows about our users needs 5 or more users or ratings behind it.
- **Signed-in features:** endpoints that need sign-in return 401 without a valid token, and the UI hides them when
  signed out (tags, the tag filter).
- **Docs:** update README.md for user-visible behaviour, and CONTEXT.md for architecture or a decision.

## Gotchas

- Blazor drops whitespace-only text between elements; put spaces inside spans or expressions.
- System.CommandLine 2.0.13 (ImportCli) has no `Uri` option type. Use a string, and keep `EnableDefaultExceptionHandler = false`.
- Postgres runs from the pgvector trixie image, with the volume mounted explicitly in `AppHost.cs`. Don't switch to
  `WithDataVolume` or a bookworm tag.
- Leftover `dcp` / service processes from a previous AppHost run hold ports. Kill them before starting again.

## Git

- Merge commits; no squash or rebase of shared history.
- PR titles and descriptions describe the user-visible change first.
