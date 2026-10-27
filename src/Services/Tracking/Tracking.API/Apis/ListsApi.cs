using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tracking.API.Data;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Tracking.API.Apis;

/// <summary>
/// Custom lists, e.g. "Weekend binge". A shared list is sent to Social (<see cref="ListSharedIntegrationEvent"/>) on every
/// change, through the outbox with the change, and taken back with <see cref="ListUnsharedIntegrationEvent"/>.
/// </summary>
public static class ListsApi
{
    private const int MaxListsPerUser = 100;
    private const int MaxItemsPerList = 500;

    public static RouteGroupBuilder MapListsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/lists").RequireAuthorization();

        api.MapGet("/", GetListsAsync);
        api.MapPost("/", CreateListAsync);
        api.MapGet("/{id:guid}", GetListAsync);
        api.MapPut("/{id:guid}", UpdateListAsync);
        api.MapDelete("/{id:guid}", DeleteListAsync);
        api.MapPost("/{id:guid}/items", AddItemAsync);
        api.MapDelete("/{id:guid}/items/{showId:guid}", RemoveItemAsync);

        return api;
    }

    private static async Task<Ok<List<ListSummaryDto>>> GetListsAsync(ClaimsPrincipal user, TrackingContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var lists = await db.Lists.AsNoTracking()
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.Name)
            .Select(l => new ListSummaryDto(l.Id, l.Name, l.Description, l.Items.Count, l.UpdatedAtUtc, l.Shared))
            .ToListAsync(ct);

        return TypedResults.Ok(lists);
    }

    private static async Task<Results<Created<ListSummaryDto>, ValidationProblem>> CreateListAsync(
        CreateListRequest request, ClaimsPrincipal user, TrackingContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(request.Name, request.Description) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var userId = user.GetUserId();
        if (await db.Lists.CountAsync(l => l.UserId == userId, ct) >= MaxListsPerUser)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"You can have at most {MaxListsPerUser} lists."] });
        }

        var now = time.GetUtcNow().UtcDateTime;
        var list = new UserList
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Lists.Add(list);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/lists/{list.Id}", new ListSummaryDto(list.Id, list.Name, list.Description, 0, list.UpdatedAtUtc));
    }

    private static async Task<Results<Ok<ListDetailDto>, NotFound>> GetListAsync(Guid id, ClaimsPrincipal user, TrackingContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var list = await db.Lists.AsNoTracking().Include(l => l.Items).SingleOrDefaultAsync(l => l.Id == id && l.UserId == userId, ct);
        if (list is null)
        {
            return TypedResults.NotFound();
        }

        var showIds = list.Items.Select(i => i.ShowId).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.ShowId)).ToDictionaryAsync(s => s.ShowId, ct);

        var items = list.Items
            .OrderBy(i => i.Position)
            .Select(i =>
            {
                shows.TryGetValue(i.ShowId, out var show);
                return new ListItemDto(i.ShowId, show?.Title, show?.PosterUrl, i.Position, i.AddedAtUtc);
            })
            .ToList();

        return TypedResults.Ok(new ListDetailDto(list.Id, list.Name, list.Description, items, list.UpdatedAtUtc, list.Shared));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> UpdateListAsync(
        Guid id, UpdateListRequest request, ClaimsPrincipal user, TrackingContext db, IIntegrationEventOutbox outbox, TimeProvider time,
        CancellationToken ct)
    {
        if (Validate(request.Name, request.Description) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var list = await FindOwnedAsync(id, user, db, ct, includeItems: true);
        if (list is null)
        {
            return TypedResults.NotFound();
        }

        var wasShared = list.Shared;
        list.Name = request.Name.Trim();
        list.Description = request.Description?.Trim();
        list.Shared = request.Shared ?? list.Shared;
        list.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        Announce(list, outbox, wasShared);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteListAsync(
        Guid id, ClaimsPrincipal user, TrackingContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var list = await FindOwnedAsync(id, user, db, ct);
        if (list is null)
        {
            return TypedResults.NotFound();
        }

        if (list.Shared)
        {
            outbox.Add(new ListUnsharedIntegrationEvent(list.Id, list.UserId, time.GetUtcNow()));
        }
        db.Lists.Remove(list);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> AddItemAsync(
        Guid id, AddListItemRequest request, ClaimsPrincipal user, TrackingContext db, IIntegrationEventOutbox outbox, TimeProvider time,
        CancellationToken ct)
    {
        var list = await FindOwnedAsync(id, user, db, ct, includeItems: true);
        if (list is null)
        {
            return TypedResults.NotFound();
        }

        if (list.Items.Any(i => i.ShowId == request.ShowId))
        {
            return TypedResults.NoContent();
        }

        if (list.Items.Count >= MaxItemsPerList)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["showId"] = [$"A list can hold at most {MaxItemsPerList} shows."] });
        }

        var now = time.GetUtcNow().UtcDateTime;
        list.Items.Add(new UserListItem
        {
            ListId = list.Id,
            ShowId = request.ShowId,
            Position = list.Items.Count == 0 ? 0 : list.Items.Max(i => i.Position) + 1,
            AddedAtUtc = now
        });
        list.UpdatedAtUtc = now;
        Announce(list, outbox, list.Shared);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveItemAsync(
        Guid id, Guid showId, ClaimsPrincipal user, TrackingContext db, IIntegrationEventOutbox outbox, TimeProvider time, CancellationToken ct)
    {
        var list = await FindOwnedAsync(id, user, db, ct, includeItems: true);
        var item = list?.Items.FirstOrDefault(i => i.ShowId == showId);
        if (list is null || item is null)
        {
            return TypedResults.NotFound();
        }

        list.Items.Remove(item);
        list.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        Announce(list, outbox, list.Shared);
        await db.SaveChangesAsync(ct);
        await outbox.PublishAddedAsync(ct);

        return TypedResults.NoContent();
    }

    /// <summary>Tells Social about a shared list's new state, or that it stopped being shared. Needs the list's items loaded.</summary>
    private static void Announce(UserList list, IIntegrationEventOutbox outbox, bool wasShared)
    {
        var at = new DateTimeOffset(list.UpdatedAtUtc, TimeSpan.Zero);
        if (list.Shared)
        {
            outbox.Add(new ListSharedIntegrationEvent(list.Id, list.UserId, list.Name, list.Description,
                list.Items.OrderBy(i => i.Position).Select(i => i.ShowId).ToList(), at));
        }
        else if (wasShared)
        {
            outbox.Add(new ListUnsharedIntegrationEvent(list.Id, list.UserId, at));
        }
    }

    private static Task<UserList?> FindOwnedAsync(Guid id, ClaimsPrincipal user, TrackingContext db, CancellationToken ct, bool includeItems = false)
    {
        var userId = user.GetUserId();
        IQueryable<UserList> query = db.Lists;
        if (includeItems)
        {
            query = query.Include(l => l.Items);
        }
        return query.SingleOrDefaultAsync(l => l.Id == id && l.UserId == userId, ct);
    }

    private static Dictionary<string, string[]>? Validate(string name, string? description)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            errors["name"] = ["Name is required and must be at most 100 characters."];
        }
        if (description?.Length > 1000)
        {
            errors["description"] = ["Description must be at most 1000 characters."];
        }
        return errors.Count == 0 ? null : errors;
    }
}
