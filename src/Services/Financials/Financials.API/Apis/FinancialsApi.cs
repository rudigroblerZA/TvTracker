using Financials.API.Data;
using Financials.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker;

namespace Financials.API.Apis;

/// <param name="Recurrence">"monthly", "yearly" or "once".</param>
public record CostItemDto(Guid Id, string Name, string Category, decimal Amount, string Currency, string Recurrence, DateOnly StartsOn,
    DateOnly? EndsOn, string? Notes);

/// <param name="Set">True when an admin set the rate here; false when it is the configured one.</param>
public record FeeSettingsDto(decimal Percent, decimal Fixed, bool Set);

public record FeeSettingsRequest(decimal Percent, decimal Fixed);

public record CostItemRequest(string? Name, string? Category, decimal Amount, string? Currency, string? Recurrence, DateOnly StartsOn,
    DateOnly? EndsOn, string? Notes);

/// <summary>
/// The admin's financials: the statement, and the costs it's made with. Admins only; anyone else gets 404, as with
/// the other admin pages.
/// </summary>
public static class FinancialsApi
{
    public static void MapFinancialsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/financials").RequireAuthorization().RequireAppAdmin();
        api.MapGet("/", StatementAsync);
        api.MapGet("/costs", CostsAsync);
        api.MapPost("/costs", AddCostAsync);
        api.MapPut("/costs/{id:guid}", SaveCostAsync);
        api.MapDelete("/costs/{id:guid}", DeleteCostAsync);
        api.MapGet("/fees", FeesAsync);
        api.MapPut("/fees", SaveFeesAsync);
        api.MapDelete("/fees", ClearFeesAsync);
    }

    /// <summary>
    /// The fee rate in force: the one an admin set, else the configured one.
    /// </summary>
    private static async Task<FinancialOptions> FeesInForceAsync(FinancialsContext db, FinancialOptions configured, CancellationToken ct) =>
        await db.FeeSettings.AsNoTracking().SingleOrDefaultAsync(ct) is { } set
            ? new FinancialOptions { PaymentFeePercent = set.Percent, PaymentFeeFixed = set.Fixed }
            : configured;

    private static async Task<Ok<FeeSettingsDto>> FeesAsync(FinancialsContext db, IOptions<FinancialOptions> options, CancellationToken ct)
    {
        var set = await db.FeeSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return TypedResults.Ok(set is null
            ? new FeeSettingsDto(options.Value.PaymentFeePercent, options.Value.PaymentFeeFixed, false)
            : new FeeSettingsDto(set.Percent, set.Fixed, true));
    }

    private static async Task<Results<Ok<FeeSettingsDto>, ValidationProblem>> SaveFeesAsync(FeeSettingsRequest request, FinancialsContext db,
        TimeProvider time, CancellationToken ct)
    {
        if (FinancialStatement.Problem(request) is { } problem)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["fees"] = [problem] });
        }
        var set = await db.FeeSettings.SingleOrDefaultAsync(ct);
        if (set is null)
        {
            set = new FeeSettings { Id = FeeSettings.OnlyId };
            db.FeeSettings.Add(set);
        }
        (set.Percent, set.Fixed, set.UpdatedAtUtc) = (request.Percent, request.Fixed, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new FeeSettingsDto(set.Percent, set.Fixed, true));
    }

    /// <summary>
    /// Back to the configured rate.
    /// </summary>
    private static async Task<Ok<FeeSettingsDto>> ClearFeesAsync(FinancialsContext db, IOptions<FinancialOptions> options, CancellationToken ct)
    {
        await db.FeeSettings.ExecuteDeleteAsync(ct);
        return TypedResults.Ok(new FeeSettingsDto(options.Value.PaymentFeePercent, options.Value.PaymentFeeFixed, false));
    }

    private static async Task<Ok<FinancialStatementDto>> StatementAsync(HttpContext http, FinancialsContext db, IncomeSources income,
        IOptions<FinancialOptions> options, TimeProvider time, int months = 12, CancellationToken ct = default)
    {
        var window = FinancialStatement.Clamp(months);
        var authorization = http.Request.Headers.Authorization.ToString();
        // Both services at once, and the costs meanwhile.
        var subscriptionsTask = income.SubscriptionsAsync(authorization, window, ct);
        var commissionTask = income.CommissionAsync(authorization, window, ct);
        var costs = await db.CostItems.AsNoTracking().ToListAsync(ct);

        return TypedResults.Ok(FinancialStatement.Build(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), window, await subscriptionsTask,
            await commissionTask, costs, await FeesInForceAsync(db, options.Value, ct)));
    }

    private static async Task<Ok<List<CostItemDto>>> CostsAsync(FinancialsContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.CostItems.AsNoTracking().OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync(ct)).Select(ToDto).ToList());

    private static async Task<Results<Ok<CostItemDto>, ValidationProblem>> AddCostAsync(CostItemRequest request, FinancialsContext db,
        TimeProvider time, CancellationToken ct)
    {
        if (FinancialStatement.Problem(request) is { } problem)
        {
            return Invalid(problem);
        }
        var item = new CostItem { Id = Guid.CreateVersion7(), Name = "", Category = "", Currency = "" };
        FinancialStatement.Apply(request, item, time.GetUtcNow().UtcDateTime);
        db.CostItems.Add(item);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Results<Ok<CostItemDto>, NotFound, ValidationProblem>> SaveCostAsync(Guid id, CostItemRequest request,
        FinancialsContext db, TimeProvider time, CancellationToken ct)
    {
        if (await db.CostItems.FindAsync([id], ct) is not { } item)
        {
            return TypedResults.NotFound();
        }
        if (FinancialStatement.Problem(request) is { } problem)
        {
            return Invalid(problem);
        }
        FinancialStatement.Apply(request, item, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<NoContent> DeleteCostAsync(Guid id, FinancialsContext db, CancellationToken ct)
    {
        // Gone already is as good as deleted.
        await db.CostItems.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static ValidationProblem Invalid(string problem) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cost"] = [problem] });

    private static CostItemDto ToDto(CostItem c) =>
        new(c.Id, c.Name, c.Category, c.Amount, c.Currency, c.Recurrence.ToString().ToLowerInvariant(), c.StartsOn, c.EndsOn, c.Notes);
}
