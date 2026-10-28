using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Research.API.Data;
using Research.API.Services;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Research.API.Apis;

public record EpisodeNoteDto(Guid EpisodeId, int SeasonNumber, int EpisodeNumber, string Mark, string Reason, string SourceUrl,
    DateTime NotedAtUtc, string NotedBy);

public record EpisodeNoteRequest(string Mark, string Reason, string SourceUrl);

/// <summary>
/// Episode notes: moderators mark episodes essential or skippable, with why and a source (One Piece's filler, a
/// two-part story's set-up). The essential cut in Analytics follows them before ratings, so every change sends the
/// show's whole list through the outbox, in the same save. Moderators only, like the rest of research; anyone else
/// gets 404.
/// </summary>
public static class EpisodeNotesApi
{
    public static void MapEpisodeNotesApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/research/{showId:guid}/episode-notes")
            .RequireAuthorization().RequireFeature(FeatureFlags.Research).RequireFeature(FeatureFlags.EpisodeNotes);
        api.MapGet("", ListAsync);
        api.MapPut("/{episodeId:guid}", PutAsync);
        api.MapDelete("/{episodeId:guid}", DeleteAsync);
    }

    private static async Task<Results<Ok<List<EpisodeNoteDto>>, NotFound>> ListAsync(
        Guid showId, ClaimsPrincipal user, Researchers researchers, ResearchContext db, CancellationToken ct)
    {
        if (!researchers.Allowed(user))
        {
            return TypedResults.NotFound();
        }
        var notes = await db.EpisodeNotes.AsNoTracking().Where(n => n.ShowId == showId)
            .OrderBy(n => n.SeasonNumber).ThenBy(n => n.EpisodeNumber).ToListAsync(ct);
        return TypedResults.Ok(notes.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<EpisodeNoteDto>, NotFound, ValidationProblem>> PutAsync(
        Guid showId, Guid episodeId, EpisodeNoteRequest request, ClaimsPrincipal user, Researchers researchers, ResearchContext db,
        CatalogReader catalog, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user) || await catalog.GetEpisodeAsync(episodeId, ct) is not { } episode || episode.ShowId != showId)
        {
            return TypedResults.NotFound();
        }

        var reason = request.Reason?.Trim() ?? "";
        var source = request.SourceUrl?.Trim() ?? "";
        var problems = new Dictionary<string, string[]>();
        if (!EpisodeMarks.IsValid(request.Mark))
        {
            problems["mark"] = [$"Use {EpisodeMarks.Essential} or {EpisodeMarks.Skippable}."];
        }
        if (reason.Length is < 3 or > EpisodeNote.MaxReason)
        {
            problems["reason"] = [$"Say why in 3 to {EpisodeNote.MaxReason} characters."];
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || source.Length > 2048)
        {
            problems["sourceUrl"] = ["Give the address of the page it comes from (http or https)."];
        }
        if (problems.Count > 0)
        {
            return TypedResults.ValidationProblem(problems);
        }

        var note = await db.EpisodeNotes.SingleOrDefaultAsync(n => n.EpisodeId == episodeId, ct);
        if (note is null)
        {
            note = new EpisodeNote { EpisodeId = episodeId, Mark = request.Mark, Reason = reason, SourceUrl = source, NotedByName = "" };
            db.EpisodeNotes.Add(note);
        }
        note.ShowId = showId;
        note.SeasonNumber = episode.SeasonNumber;
        note.EpisodeNumber = episode.EpisodeNumber;
        note.Mark = request.Mark;
        note.Reason = reason;
        note.SourceUrl = source;
        note.NotedAtUtc = time.GetUtcNow().UtcDateTime;
        note.NotedById = user.FindFirstValue("sub");
        note.NotedByName = user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? "a moderator";

        await SaveAndPublishAsync(db, outbox, showId, time, ct);
        return TypedResults.Ok(ToDto(note));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid showId, Guid episodeId, ClaimsPrincipal user, Researchers researchers, ResearchContext db, IIntegrationEventOutbox outbox,
        TimeProvider time, CancellationToken ct)
    {
        if (!researchers.Allowed(user)
            || await db.EpisodeNotes.SingleOrDefaultAsync(n => n.EpisodeId == episodeId && n.ShowId == showId, ct) is not { } note)
        {
            return TypedResults.NotFound();
        }
        db.EpisodeNotes.Remove(note);
        await SaveAndPublishAsync(db, outbox, showId, time, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Saves the change with an event carrying the show's notes as they'll be after it.</summary>
    private static async Task SaveAndPublishAsync(ResearchContext db, IIntegrationEventOutbox outbox, Guid showId, TimeProvider time,
        CancellationToken ct)
    {
        var stored = await db.EpisodeNotes.AsNoTracking().Where(n => n.ShowId == showId).ToListAsync(ct);
        var pending = db.ChangeTracker.Entries<EpisodeNote>().ToList();
        var after = stored.Where(n => pending.All(p => p.Entity.EpisodeId != n.EpisodeId))
            .Concat(pending.Where(p => p.State != EntityState.Deleted).Select(p => p.Entity));
        outbox.Add(new EpisodeNotesChangedIntegrationEvent(showId, after.Select(ToItem).ToList(), time.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
    }

    internal static EpisodeNoteItem ToItem(EpisodeNote n) => new(n.ShowId, n.EpisodeId, n.Mark, n.Reason, n.SourceUrl);

    private static EpisodeNoteDto ToDto(EpisodeNote n) =>
        new(n.EpisodeId, n.SeasonNumber, n.EpisodeNumber, n.Mark, n.Reason, n.SourceUrl, n.NotedAtUtc, n.NotedByName);
}
