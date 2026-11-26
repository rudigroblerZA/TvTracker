using System.Security.Claims;
using Financials.API.Payments;
using Financials.API.Services;
using Financials.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker;
using TvTracker.IntegrationEvents;

namespace Financials.API.Apis;

/// <param name="Provider">The provider selling now; null while plans can't be bought.</param>
public record PlanPricesDto(string Currency, decimal PlusMonth, decimal PlusYear, decimal FamilyMonth, decimal FamilyYear, string? Provider);

/// <param name="ManageUrl">The provider's page for this subscription, when it has one (the test provider's does).</param>
public record SubscriptionDto(string Plan, string Interval, bool Active, bool CancelAtPeriodEnd, DateTime CurrentPeriodEndUtc, DateTime? EndedAtUtc,
    string Provider, string? ManageUrl);

public record CheckoutRequest(string Plan, string Interval);

public record CheckoutDto(string Url);

public record ReceiptDto(string Plan, string Interval, decimal Amount, string Currency, DateTime PaidAtUtc, DateTime PeriodEndUtc, string Provider);

/// <summary>
/// A member's payment records, as their data export has them.
/// </summary>
public record PaymentsExportDto(SubscriptionDto? Subscription, IReadOnlyList<ReceiptDto> Receipts);

/// <summary>
/// Buying Plus and Family through a payment provider (<see cref="IPaymentProvider"/>; the <c>payments</c> flag).
/// <list type="bullet">
/// <item><b>Checkout:</b> the app makes a session at today's price for the caller's region, and the provider's checkout
/// names it; the return address is the server's own (<see cref="PaymentOptions.ReturnUrl"/>).</item>
/// <item><b>Webhooks:</b> anonymous, but each provider checks its own signature, and an unsigned or stale call is 400
/// with nothing applied. The plan moves through <see cref="SubscriptionLedger"/>, once per event.</item>
/// <item><b>One at a time:</b> a second checkout while a subscription is active is refused (409); switching between Plus
/// and Family is cancelling one and buying the other.</item>
/// <item><b>Cancel and resume</b> ask the provider, then record it, so the page reflects it at once.</item>
/// </list>
/// </summary>
public static class PaymentsApi
{
    public static void MapPaymentsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireFeature(FeatureFlags.Plans).RequireFeature(FeatureFlags.Payments);
        api.MapGet("/plans/prices", GetPricesAsync);
        api.MapGet("/me/subscription", GetSubscriptionAsync);
        api.MapPost("/me/checkout", CheckoutAsync);
        api.MapPost("/me/subscription/cancel", CancelAsync);
        api.MapPost("/me/subscription/resume", ResumeAsync);

        // The member's own payment records, for Identity.API's data export. Not behind the flags: what was kept is theirs
        // to have whether or not plans are still sold.
        app.MapGet("/api/v1/me/export", ExportAsync).RequireAuthorization();

        // Not behind the flags: a payment already taken must still be recorded while they're off.
        app.MapPost("/api/v1/payments/webhooks/{provider}", WebhookAsync).AllowAnonymous().RequireRateLimiting(ClientRateLimiting.Webhook);
    }

    private static async Task<Ok<PlanPricesDto>> GetPricesAsync(
        HttpContext http, Members members, IOptions<PaymentOptions> options, PaymentProviders providers, CancellationToken ct)
    {
        // Without Identity's answer the default region's prices are shown; a checkout asks again and won't guess.
        var (_, region) = await members.RegionAsync(http.Request.Headers.Authorization.ToString(), ct);
        var prices = PlanPrices.For(region, options.Value.Prices);
        return TypedResults.Ok(new PlanPricesDto(prices.Currency, prices.PlusMonth, prices.PlusYear, prices.FamilyMonth, prices.FamilyYear,
            providers.Selling?.Name));
    }

    private static async Task<Results<Ok<SubscriptionDto>, NoContent>> GetSubscriptionAsync(
        ClaimsPrincipal user, FinancialsContext db, PaymentProviders providers, CancellationToken ct) =>
        await LatestAsync(db, user.GetUserId(), ct) is { } subscription
            ? TypedResults.Ok(ToDto(subscription, providers))
            : TypedResults.NoContent();

    private static async Task<Results<Ok<CheckoutDto>, ValidationProblem, Conflict<string>, ProblemHttpResult>> CheckoutAsync(
        CheckoutRequest request, HttpContext http, ClaimsPrincipal user, FinancialsContext db, Members members, IOptions<PaymentOptions> options,
        PaymentProviders providers, TimeProvider time, ILogger<CheckoutSession> logger, CancellationToken ct)
    {
        if (!PlanPrices.Sold(request.Plan) || !PlanPrices.Intervals.Contains(request.Interval))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["plan"] = [$"Choose {Plans.Plus} or {Plans.Family}, by the {PlanPrices.Month} or the {PlanPrices.Year}."]
            });
        }
        if (providers.Selling is not { } provider || options.Value.ReturnUrl is not { Length: > 0 } returnUrl)
        {
            return TypedResults.Problem("Plans can't be bought here yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        var userId = user.GetUserId();
        // The price is by the member's region, which is Identity's to say; a checkout at a guessed price would be wrong.
        var (known, region) = await members.RegionAsync(http.Request.Headers.Authorization.ToString(), ct);
        if (!known)
        {
            return TypedResults.Problem("Your account couldn't be read just now; try again in a moment.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (await db.Subscriptions.AnyAsync(s => s.UserId == userId && s.Status == SubscriptionStatus.Active, ct))
        {
            return TypedResults.Conflict("You already have a subscription; cancel it before buying another plan.");
        }

        var prices = PlanPrices.For(region, options.Value.Prices);
        var session = new CheckoutSession
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Provider = provider.Name,
            Plan = request.Plan,
            Interval = request.Interval,
            Amount = prices.For(request.Plan, request.Interval),
            Currency = prices.Currency,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime
        };
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync(ct);
        try
        {
            var url = await provider.CreateCheckoutAsync(session, returnUrl, ct);
            return TypedResults.Ok(new CheckoutDto(url.ToString()));
        }
        catch (Exception ex) when (ProviderFailed(ex, ct))
        {
            // The session stays unused; nothing was charged.
            logger.LogError(ex, "{Provider} couldn't make a checkout for session {Session}", provider.Name, session.Id);
            return TypedResults.Problem("The payment provider didn't answer; try again in a moment.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// The caller's own payment records: their latest subscription and every receipt.
    /// </summary>
    internal static async Task<Ok<PaymentsExportDto>> ExportAsync(ClaimsPrincipal user, FinancialsContext db, PaymentProviders providers, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return TypedResults.Ok(new PaymentsExportDto(
            await LatestAsync(db, userId, ct) is { } subscription ? ToDto(subscription, providers) : null,
            await ReceiptsAsync(db, userId, ct)));
    }

    /// <summary>
    /// A provider call that failed, for any reason short of the request itself being given up: an error answer, no
    /// answer in time, a reply that couldn't be read, or the provider not being configured.
    /// </summary>
    private static bool ProviderFailed(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    internal static Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> CancelAsync(
        ClaimsPrincipal user, FinancialsContext db, PaymentProviders providers, TimeProvider time, ILogger<Subscription> logger, CancellationToken ct) =>
        ChangeAsync(user, db, providers, time, logger, cancel: true, ct);

    internal static Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> ResumeAsync(
        ClaimsPrincipal user, FinancialsContext db, PaymentProviders providers, TimeProvider time, ILogger<Subscription> logger, CancellationToken ct) =>
        ChangeAsync(user, db, providers, time, logger, cancel: false, ct);

    private static async Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> ChangeAsync(
        ClaimsPrincipal user, FinancialsContext db, PaymentProviders providers, TimeProvider time, ILogger logger, bool cancel, CancellationToken ct)
    {
        var subscription = await db.Subscriptions
            .Where(s => s.UserId == user.GetUserId() && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (subscription is null || providers.Named(subscription.Provider) is not { } provider)
        {
            return TypedResults.NotFound();
        }
        if (subscription.CancelAtPeriodEnd != cancel)
        {
            try
            {
                await (cancel ? provider.CancelAsync(subscription, now: false, ct) : provider.ResumeAsync(subscription, ct));
            }
            catch (Exception ex) when (ProviderFailed(ex, ct))
            {
                logger.LogError(ex, "{Provider} couldn't {Change} subscription {Subscription}", provider.Name, cancel ? "cancel" : "resume",
                    subscription.ProviderSubscriptionId);
                return TypedResults.Problem("The payment provider didn't answer; nothing changed. Try again in a moment.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            subscription.CancelAtPeriodEnd = cancel;
            subscription.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.Ok(ToDto(subscription, providers));
    }

    /// <summary>
    /// The largest webhook body read; provider events are a few KB, and the body is read before its signature is checked.
    /// </summary>
    internal const int MaxWebhookBodyBytes = 256 * 1024;

    private static async Task<Results<Ok, BadRequest, NotFound, StatusCodeHttpResult>> WebhookAsync(
        string provider, HttpRequest request, PaymentProviders providers, SubscriptionLedger ledger, TimeProvider time,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (providers.Named(provider) is not { } named)
        {
            return TypedResults.NotFound();
        }
        if (request.ContentLength > MaxWebhookBodyBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxWebhookBodyBytes;
        }
        string body;
        try
        {
            using var reader = new StreamReader(request.Body);
            body = await reader.ReadToEndAsync(ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        if (named.ReadWebhook(body, request.Headers, time.GetUtcNow()) is not { } e)
        {
            loggers.CreateLogger(typeof(PaymentsApi)).LogWarning("A {Provider} webhook was refused: bad or stale signature, or not an event the app acts on", provider);
            return TypedResults.BadRequest();
        }
        if (e.Kind == PaymentEventKind.Ignored)
        {
            return TypedResults.Ok();
        }
        // The answer decides whether the provider sends it again (LedgerResults.WebhookStatus).
        return TypedResults.StatusCode((await ledger.ApplyAsync(named.Name, e, ct)).WebhookStatus());
    }

    /// <summary>
    /// The active subscription, else the most recent; for the plan page and the export.
    /// </summary>
    private static Task<Subscription?> LatestAsync(FinancialsContext db, string userId, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Status)
            .ThenByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    private static async Task<List<ReceiptDto>> ReceiptsAsync(FinancialsContext db, string userId, CancellationToken ct) =>
        await db.Receipts.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.PaidAtUtc)
            .Select(r => new ReceiptDto(r.Plan, r.Interval, r.Amount, r.Currency, r.PaidAtUtc, r.PeriodEndUtc, r.Provider))
            .ToListAsync(ct);

    private static SubscriptionDto ToDto(Subscription s, PaymentProviders providers) =>
        new(s.Plan, s.Interval, s.Status == SubscriptionStatus.Active, s.CancelAtPeriodEnd, s.CurrentPeriodEndUtc, s.EndedAtUtc, s.Provider,
            s.Status == SubscriptionStatus.Active ? providers.Named(s.Provider)?.ManageUrl(s) : null);
}
