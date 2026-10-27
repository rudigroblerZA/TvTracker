using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using Research.API.Services;
using TvTracker;

namespace Research.API.Apis;

public record SourceDto(Guid Id, int? Number, string Kind, string Url, string Title, string Excerpt, bool Included, DateTime RetrievedAtUtc);

/// <param name="Origin">"saved" (the trusted value now), "catalog" (TVmaze's, as the catalog has it) or "source".</param>
public record FactOptionDto(string Value, string Origin, Guid? SourceId, string? SourceTitle);

public record FactDto(string Key, string Label, string Kind, string? CatalogField, string? Saved, string? Catalog, IReadOnlyList<FactOptionDto> Options);

public record VersionDto(int Version, DateTime SavedAtUtc, string SavedBy);

/// <param name="Notes">What gathering couldn't do (no Wikidata item, no TMDB key, a source that didn't answer).</param>
/// <param name="DraftingSinceUtc">Set while the model drafts in the background; the page checks back until it clears.</param>
/// <param name="DraftProblem">Why the last draft failed.</param>
public record WorkspaceDto(Guid ShowId, string Title, DateTime? GatheredAtUtc, string? Draft, DateTime? DraftingSinceUtc, string? DraftProblem, IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<FactDto> Facts, SavedResearchDto? Saved, IReadOnlyList<VersionDto> Versions, IReadOnlyList<string> Notes,
    string Model, bool TmdbConfigured);

public record SavedFact(string Key, string Value, Guid? SourceId);

public record SavedSource(int Number, Guid Id, string Kind, string Title, string Url, DateTime RetrievedAtUtc);

/// <param name="Source">The number of the source the value came from, as the insight cites it; null when typed by hand.</param>
public record SavedFactDto(string Key, string Label, string Kind, string Value, int? Source);

public record SavedResearchDto(Guid ShowId, int Version, string Insight, IReadOnlyList<SavedFactDto> Facts, IReadOnlyList<SavedSource> Sources,
    DateTime SavedAtUtc, string SavedBy);

public record ResearchedShowDto(Guid ShowId, string Title, int? Version, DateTime? SavedAtUtc, DateTime UpdatedAtUtc);

public record SaveRequest(string Insight, IReadOnlyList<SavedFact> Facts);

/// <param name="Outcome">"applied", "removed" (back to the provider's value) or "unchanged".</param>
public record CatalogFieldOutcome(string Key, string Field, string Outcome);

public record SaveResultDto(SavedResearchDto Saved, IReadOnlyList<CatalogFieldOutcome> Catalog);

public record AddSourceRequest(string Url);

public record IncludeRequest(bool Included);

public record DraftRequest(string? Text);

/// <summary>
/// Research: moderators enrich a show from extra sources and save it as the trusted record.
/// <list type="number">
/// <item><b>Gather</b> reads Wikidata, Wikipedia and (with a key) TMDB, replacing what an earlier gather found; pages
/// added by address stay.</item>
/// <item><b>Facts:</b> each source offers values; the moderator keeps the catalog's, picks a source's or types one.</item>
/// <item><b>Draft</b> asks the local model, in the background (<see cref="DraftQueue"/>), for an insight from the
/// included sources, citing them [n]; the moderator edits it.</item>
/// <item><b>Save</b> writes a new version, the trusted record, and applies its catalog fields (summary, network, premiere
/// date) as overrides first: if the catalog refuses or is down, nothing is saved, so the record and the catalog agree.
/// A catalog field dropped from a later version goes back to the provider's value.</item>
/// </list>
/// Everything but the saved record's read is for moderators (<c>Admin:Users</c> and <c>Moderation:Users</c>); anyone
/// else gets 404, which the UI uses to know whether to show the way in.
/// </summary>
public static partial class ResearchApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] KindOrder = [SourceKinds.Wikidata, SourceKinds.Wikipedia, SourceKinds.Tmdb, SourceKinds.Url];

    public static void MapResearchApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/shows/{showId:guid}/research", GetSavedAsync).RequireFeature(FeatureFlags.Research);

        var api = app.MapGroup("/api/v1/research").RequireAuthorization().RequireFeature(FeatureFlags.Research);
        api.MapGet("", ListAsync);
        api.MapGet("/{showId:guid}", GetWorkspaceAsync);
        api.MapPost("/{showId:guid}/gather", GatherAsync);
        api.MapPost("/{showId:guid}/sources", AddSourceAsync);
        api.MapPut("/{showId:guid}/sources/{sourceId:guid}", IncludeAsync);
        api.MapDelete("/{showId:guid}/sources/{sourceId:guid}", RemoveSourceAsync);
        api.MapPost("/{showId:guid}/draft", DraftAsync);
        api.MapPut("/{showId:guid}/draft", KeepDraftAsync);
        api.MapPost("/{showId:guid}/save", SaveAsync);
    }

    /// <summary>The trusted record: the latest saved version. Public, like the show page it's on.</summary>
    private static async Task<Results<Ok<SavedResearchDto>, NotFound>> GetSavedAsync(Guid showId, ResearchContext db, CancellationToken ct) =>
        await LatestAsync(db, showId, ct) is { } saved ? TypedResults.Ok(ToDto(saved)) : TypedResults.NotFound();

    private static async Task<Results<Ok<List<ResearchedShowDto>>, NotFound>> ListAsync(
        ClaimsPrincipal user, Researchers researchers, ResearchContext db, CancellationToken ct)
    {
        if (!researchers.Allowed(user))
        {
            return TypedResults.NotFound();
        }
        var dossiers = await db.Dossiers.AsNoTracking().OrderByDescending(d => d.UpdatedAtUtc).Take(200).ToListAsync(ct);
        var ids = dossiers.Select(d => d.ShowId).ToList();
        var latest = await db.Saved.AsNoTracking().Where(s => ids.Contains(s.ShowId))
            .GroupBy(s => s.ShowId).Select(g => g.OrderByDescending(s => s.Version).Select(s => new { s.ShowId, s.Version, s.SavedAtUtc }).First())
            .ToDictionaryAsync(s => s.ShowId, ct);
        return TypedResults.Ok(dossiers.Select(d => new ResearchedShowDto(d.ShowId, d.ShowTitle,
            latest.TryGetValue(d.ShowId, out var s) ? s.Version : null, latest.TryGetValue(d.ShowId, out var t) ? t.SavedAtUtc : null, d.UpdatedAtUtc)).ToList());
    }

    private static async Task<Results<Ok<WorkspaceDto>, NotFound>> GetWorkspaceAsync(
        Guid showId, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CatalogReader catalog, InsightDrafter drafter,
        TmdbSource tmdb, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await catalog.GetShowAsync(showId, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(await WorkspaceAsync(db, show, [], drafter, tmdb, ct));
    }

    private static async Task<Results<Ok<WorkspaceDto>, NotFound>> GatherAsync(
        Guid showId, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CatalogReader catalog, WikimediaSource wikimedia,
        TmdbSource tmdb, InsightDrafter drafter, TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await catalog.GetShowAsync(showId, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }

        var (found, notes) = await wikimedia.GatherAsync(show, ct);
        if (!tmdb.Configured)
        {
            notes.Add("TMDB is off: set Research:TmdbApiKey to include it.");
        }
        else
        {
            try
            {
                if (await tmdb.GatherAsync(show, found, ct) is { } record)
                {
                    found.Add(record);
                }
                else
                {
                    notes.Add("TMDB has no match for this show.");
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                notes.Add($"TMDB didn't answer ({ex.Message}).");
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        var dossier = await EnsureDossierAsync(db, show, now, ct);
        dossier.GatheredAtUtc = now;
        var gatheredKinds = SourceKinds.Gathered.ToArray();
        var stale = await db.Sources.Where(s => s.ShowId == showId && gatheredKinds.Contains(s.Kind)).ToListAsync(ct);
        var staleIds = stale.Select(s => s.Id).ToList();
        db.Candidates.RemoveRange(await db.Candidates.Where(c => staleIds.Contains(c.SourceId)).ToListAsync(ct));
        db.Sources.RemoveRange(stale);
        foreach (var source in found)
        {
            AddSource(db, showId, source, now);
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await WorkspaceAsync(db, show, notes, drafter, tmdb, ct));
    }

    private static async Task<Results<Ok<WorkspaceDto>, NotFound, ValidationProblem>> AddSourceAsync(
        Guid showId, AddSourceRequest request, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CatalogReader catalog,
        WebPageSource pages, InsightDrafter drafter, TmdbSource tmdb, TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await catalog.GetShowAsync(showId, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }

        var (page, problem) = await pages.FetchAsync(request.Url ?? "", ct);
        if (page is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["url"] = [problem!] });
        }

        var now = time.GetUtcNow().UtcDateTime;
        await EnsureDossierAsync(db, show, now, ct);
        AddSource(db, showId, page, now);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await WorkspaceAsync(db, show, [], drafter, tmdb, ct));
    }

    private static async Task<Results<NoContent, NotFound>> IncludeAsync(
        Guid showId, Guid sourceId, IncludeRequest request, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await db.Sources.SingleOrDefaultAsync(s => s.Id == sourceId && s.ShowId == showId, ct) is not { } source)
        {
            return TypedResults.NotFound();
        }
        source.Included = request.Included;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveSourceAsync(
        Guid showId, Guid sourceId, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await db.Sources.SingleOrDefaultAsync(s => s.Id == sourceId && s.ShowId == showId, ct) is not { } source)
        {
            return TypedResults.NotFound();
        }
        db.Candidates.RemoveRange(await db.Candidates.Where(c => c.SourceId == sourceId).ToListAsync(ct));
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Starts a draft in the background; 202, and the workspace says it's drafting until it's done.</summary>
    private static async Task<Results<Accepted, NotFound, ValidationProblem>> DraftAsync(
        Guid showId, ClaimsPrincipal user, Researchers researchers, ResearchContext db, DraftQueue drafts, TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await db.Dossiers.FindAsync([showId], ct) is not { } dossier)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (dossier.DraftingSinceUtc is { } since && now - since < DraftQueue.Stale)
        {
            return TypedResults.Accepted((string?)null);
        }
        var numbered = Number(await db.Sources.AsNoTracking().Where(s => s.ShowId == showId).ToListAsync(ct));
        if (numbered.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["sources"] = ["Include at least one source to draft from."] });
        }

        dossier.DraftingSinceUtc = now;
        dossier.DraftProblem = null;
        await db.SaveChangesAsync(ct);
        drafts.Enqueue(showId, dossier.ShowTitle, numbered);
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Keeps the moderator's edits to the draft between sessions.</summary>
    private static async Task<Results<NoContent, NotFound>> KeepDraftAsync(
        Guid showId, DraftRequest request, ClaimsPrincipal user, Researchers researchers, ResearchContext db, TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await db.Dossiers.FindAsync([showId], ct) is not { } dossier)
        {
            return TypedResults.NotFound();
        }
        dossier.Draft = request.Text is { Length: > 0 } text ? Text.Clip(text, 8000) : null;
        dossier.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<SaveResultDto>, NotFound, ValidationProblem, ProblemHttpResult>> SaveAsync(
        Guid showId, SaveRequest request, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CatalogReader catalog,
        CatalogOverrides overrides, TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await catalog.GetShowAsync(showId, ct) is not { } show)
        {
            return TypedResults.NotFound();
        }

        var sources = Number(await db.Sources.AsNoTracking().Where(s => s.ShowId == showId).ToListAsync(ct));
        var insight = (request.Insight ?? "").Trim();
        var facts = (request.Facts ?? []).Select(f => f with { Value = (f.Value ?? "").Trim() }).Where(f => f.Value.Length > 0).ToList();
        var errors = new Dictionary<string, string[]>();
        if (insight.Length is 0 or > 4000)
        {
            errors["insight"] = ["The insight is 1 to 4000 characters."];
        }
        else if (Citations().Matches(insight).Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault(n => n < 1 || n > sources.Count) is > 0 and var bad)
        {
            errors["insight"] = [$"The insight cites [{bad}], but {sources.Count} sources are included."];
        }
        foreach (var group in facts.GroupBy(f => f.Key).Where(g => g.Count() > 1))
        {
            errors[group.Key] = ["Give each fact once."];
        }
        foreach (var fact in facts)
        {
            if (ResearchFacts.Validate(fact.Key, fact.Value) is { } problem)
            {
                errors[fact.Key] = [problem];
            }
            else if (fact.SourceId is { } id && sources.All(s => s.Source.Id != id))
            {
                errors[fact.Key] = ["That source isn't included."];
            }
        }
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Catalog fields first: the record is saved only once the catalog holds the same values.
        var previous = await LatestAsync(db, showId, ct);
        var previousFacts = previous is null ? [] : JsonSerializer.Deserialize<List<SavedFact>>(previous.Facts, Json) ?? [];
        var outcomes = new List<CatalogFieldOutcome>();
        foreach (var definition in ResearchFacts.All.Where(f => f.CatalogField is not null))
        {
            var fact = facts.FirstOrDefault(f => f.Key == definition.Key);
            if (fact is not null)
            {
                var (outcome, detail) = await overrides.ApplyAsync(showId, definition.CatalogField!, fact.Value, ct);
                if (outcome != OverrideOutcome.Applied)
                {
                    return CatalogProblem(definition, outcome, detail);
                }
                outcomes.Add(new CatalogFieldOutcome(definition.Key, definition.CatalogField!, "applied"));
            }
            else if (previousFacts.Any(f => f.Key == definition.Key))
            {
                if (await overrides.RemoveAsync(showId, definition.CatalogField!, ct) != OverrideOutcome.Removed)
                {
                    return CatalogProblem(definition, OverrideOutcome.Unavailable, null);
                }
                outcomes.Add(new CatalogFieldOutcome(definition.Key, definition.CatalogField!, "removed"));
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        var dossier = await EnsureDossierAsync(db, show, now, ct);
        dossier.Draft = insight;
        var saved = new SavedResearch
        {
            Id = Guid.CreateVersion7(),
            ShowId = showId,
            Version = (previous?.Version ?? 0) + 1,
            Insight = insight,
            Facts = JsonSerializer.Serialize(facts.OrderBy(f => ResearchFacts.All.ToList().FindIndex(d => d.Key == f.Key)), Json),
            Sources = JsonSerializer.Serialize(sources.Select(s => new SavedSource(s.Number, s.Source.Id, s.Source.Kind, s.Source.Title, s.Source.Url, s.Source.RetrievedAtUtc)), Json),
            SavedAtUtc = now,
            SavedById = user.FindFirstValue("sub"),
            SavedByName = user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? "a moderator"
        };
        db.Saved.Add(saved);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new SaveResultDto(ToDto(saved), outcomes));
    }

    private static ProblemHttpResult CatalogProblem(FactDefinition fact, OverrideOutcome outcome, string? detail) => TypedResults.Problem(
        outcome switch
        {
            OverrideOutcome.Invalid => $"The catalog refused the {fact.Label.ToLowerInvariant()}: {detail}. Nothing was saved.",
            OverrideOutcome.NotInCatalog => "The show is no longer in the catalog. Nothing was saved.",
            _ => "The catalog sync worker didn't answer, so its fields couldn't be applied. Nothing was saved; try again."
        },
        statusCode: outcome == OverrideOutcome.Invalid ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable);

    private static async Task<WorkspaceDto> WorkspaceAsync(ResearchContext db, CatalogShow show, List<string> notes, InsightDrafter drafter,
        TmdbSource tmdb, CancellationToken ct)
    {
        var dossier = await db.Dossiers.AsNoTracking().SingleOrDefaultAsync(d => d.ShowId == show.Id, ct);
        var sources = await db.Sources.AsNoTracking().Where(s => s.ShowId == show.Id).ToListAsync(ct);
        var numbers = Number(sources).ToDictionary(x => x.Source.Id, x => x.Number);
        var candidates = await db.Candidates.AsNoTracking().Where(c => c.ShowId == show.Id).ToListAsync(ct);
        var saved = await LatestAsync(db, show.Id, ct);
        var savedFacts = saved is null ? [] : JsonSerializer.Deserialize<List<SavedFact>>(saved.Facts, Json) ?? [];
        var versions = await db.Saved.AsNoTracking().Where(s => s.ShowId == show.Id).OrderByDescending(s => s.Version)
            .Select(s => new VersionDto(s.Version, s.SavedAtUtc, s.SavedByName)).ToListAsync(ct);
        var titles = sources.ToDictionary(s => s.Id, s => s.Title);

        var facts = ResearchFacts.All.Select(definition =>
        {
            var savedFact = savedFacts.FirstOrDefault(f => f.Key == definition.Key);
            var savedValue = savedFact?.Value;
            var catalogValue = ResearchFacts.FromCatalog(definition.Key, show);
            var options = new List<FactOptionDto>();
            if (savedValue is not null)
            {
                // Keeps its citation: the source it was saved from if that's still included, else one that says the
                // same now. A gather replaces sources, and a re-save would otherwise drop the citation.
                var included = sources.Where(s => s.Included).Select(s => s.Id).ToHashSet();
                var sourceId = savedFact!.SourceId is { } id && included.Contains(id) ? id
                    : candidates.FirstOrDefault(c => c.Key == definition.Key && c.Value == savedValue && included.Contains(c.SourceId))?.SourceId;
                options.Add(new FactOptionDto(savedValue, "saved", sourceId, sourceId is { } sid ? titles[sid] : null));
            }
            if (catalogValue is not null && catalogValue != savedValue)
            {
                options.Add(new FactOptionDto(catalogValue, "catalog", null, null));
            }
            foreach (var c in candidates.Where(c => c.Key == definition.Key && sources.Any(s => s.Id == c.SourceId && s.Included)))
            {
                if (options.All(o => o.Value != c.Value))
                {
                    options.Add(new FactOptionDto(c.Value, "source", c.SourceId, titles[c.SourceId]));
                }
            }
            return new FactDto(definition.Key, definition.Label, definition.Kind, definition.CatalogField, savedValue, catalogValue, options);
        }).ToList();

        return new WorkspaceDto(show.Id, show.Title, dossier?.GatheredAtUtc, dossier?.Draft, dossier?.DraftingSinceUtc, dossier?.DraftProblem,
            sources.OrderBy(s => numbers.GetValueOrDefault(s.Id, int.MaxValue)).ThenBy(s => s.RetrievedAtUtc)
                .Select(s => new SourceDto(s.Id, numbers.TryGetValue(s.Id, out var n) ? n : null, s.Kind, s.Url, s.Title, s.Excerpt, s.Included, s.RetrievedAtUtc))
                .ToList(),
            facts, saved is null ? null : ToDto(saved), versions, notes, drafter.Model, tmdb.Configured);
    }

    /// <summary>Included sources numbered as drafts and saves cite them: Wikidata, Wikipedia, TMDB, then pages by when added.</summary>
    private static List<(int Number, ResearchSource Source)> Number(IEnumerable<ResearchSource> sources) =>
        sources.Where(s => s.Included)
            .OrderBy(s => Array.IndexOf(KindOrder, s.Kind)).ThenBy(s => s.RetrievedAtUtc)
            .Select((s, i) => (i + 1, s))
            .ToList();

    private static void AddSource(ResearchContext db, Guid showId, FoundSource found, DateTime now)
    {
        var source = new ResearchSource
        {
            Id = Guid.CreateVersion7(),
            ShowId = showId,
            Kind = found.Kind,
            Url = Text.Clip(found.Url, 2048),
            Title = Text.Clip(found.Title, 500),
            Excerpt = Text.Clip(found.Excerpt, ResearchSource.MaxExcerpt),
            RetrievedAtUtc = now
        };
        db.Sources.Add(source);
        foreach (var (key, value) in found.Facts.Where(f => ResearchFacts.Validate(f.Key, f.Value) is null))
        {
            db.Candidates.Add(new FactCandidate { Id = Guid.CreateVersion7(), ShowId = showId, SourceId = source.Id, Key = key, Value = value });
        }
    }

    private static async Task<Dossier> EnsureDossierAsync(ResearchContext db, CatalogShow show, DateTime now, CancellationToken ct)
    {
        var dossier = await db.Dossiers.FindAsync([show.Id], ct);
        if (dossier is null)
        {
            dossier = new Dossier { ShowId = show.Id, ShowTitle = show.Title };
            db.Dossiers.Add(dossier);
        }
        dossier.ShowTitle = show.Title;
        dossier.UpdatedAtUtc = now;
        return dossier;
    }

    private static Task<SavedResearch?> LatestAsync(ResearchContext db, Guid showId, CancellationToken ct) =>
        db.Saved.AsNoTracking().Where(s => s.ShowId == showId).OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);

    private static SavedResearchDto ToDto(SavedResearch saved)
    {
        var sources = JsonSerializer.Deserialize<List<SavedSource>>(saved.Sources, Json) ?? [];
        var facts = (JsonSerializer.Deserialize<List<SavedFact>>(saved.Facts, Json) ?? [])
            .Where(f => ResearchFacts.ByKey.ContainsKey(f.Key))
            .Select(f => new SavedFactDto(f.Key, ResearchFacts.ByKey[f.Key].Label, ResearchFacts.ByKey[f.Key].Kind, f.Value,
                sources.FirstOrDefault(s => s.Id == f.SourceId)?.Number))
            .ToList();
        return new SavedResearchDto(saved.ShowId, saved.Version, saved.Insight, facts, sources, saved.SavedAtUtc, saved.SavedByName);
    }

    [GeneratedRegex(@"\[(\d{1,3})\]")]
    private static partial Regex Citations();
}
