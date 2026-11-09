using System.Security.Claims;
using Identity.API.Data;
using Identity.API.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Apis;

/// <param name="Provider">The provider selling now; null while plans can't be bought.</param>
public record PlanPricesDto(string Currency, decimal PlusMonth, decimal PlusYear, decimal FamilyMonth, decimal FamilyYear, string? Provider);

/// <param name="ManageUrl">The provider's page for this subscription, when it has one (the test provider's does).</param>
public record SubscriptionDto(string Plan, string Interval, bool Active, bool CancelAtPeriodEnd, DateTime CurrentPeriodEndUtc, DateTime? EndedAtUtc,
    string Provider, string? ManageUrl);

public record CheckoutRequest(string Plan, string Interval);

public record CheckoutDto(string Url);

public record ReceiptDto(string Plan, string Interval, decimal Amount, string Currency, DateTime PaidAtUtc, DateTime PeriodEndUtc, string Provider);

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

        // Not behind the flags: a payment already taken must still be recorded while they're off.
        app.MapPost("/api/v1/payments/webhooks/{provider}", WebhookAsync).AllowAnonymous();
    }

    private static async Task<Ok<PlanPricesDto>> GetPricesAsync(
        ClaimsPrincipal user, IdentityContext db, IOptions<PaymentOptions> options, PaymentProviders providers, CancellationToken ct)
    {
        var region = await db.Profiles.Where(p => p.UserId == user.GetUserId()).Select(p => p.Region).SingleOrDefaultAsync(ct);
        var prices = PlanPrices.For(region, options.Value.Prices);
        return TypedResults.Ok(new PlanPricesDto(prices.Currency, prices.PlusMonth, prices.PlusYear, prices.FamilyMonth, prices.FamilyYear,
            providers.Selling?.Name));
    }

    private static async Task<Results<Ok<SubscriptionDto>, NoContent>> GetSubscriptionAsync(
        ClaimsPrincipal user, IdentityContext db, PaymentProviders providers, CancellationToken ct) =>
        await LatestAsync(db, user.GetUserId(), ct) is { } subscription
            ? TypedResults.Ok(ToDto(subscription, providers))
            : TypedResults.NoContent();

    private static async Task<Results<Ok<CheckoutDto>, ValidationProblem, Conflict<string>, ProblemHttpResult>> CheckoutAsync(
        CheckoutRequest request, ClaimsPrincipal user, IdentityContext db, IIntegrationEventOutbox outbox, IOptions<PaymentOptions> options,
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
        var profile = await ProfileApi.GetOrCreateProfileAsync(user, db, outbox, ct);
        if (await db.Subscriptions.AnyAsync(s => s.UserId == profile.UserId && s.Status == SubscriptionStatus.Active, ct))
        {
            return TypedResults.Conflict("You already have a subscription; cancel it before buying another plan.");
        }

        var prices = PlanPrices.For(profile.Region, options.Value.Prices);
        var session = new CheckoutSession
        {
            Id = Guid.CreateVersion7(),
            UserId = profile.UserId,
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
    /// A provider call that failed, for any reason short of the request itself being given up: an error answer, no
    /// answer in time, a reply that couldn't be read, or the provider not being configured.
    /// </summary>
    private static bool ProviderFailed(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    private static Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> CancelAsync(
        ClaimsPrincipal user, IdentityContext db, PaymentProviders providers, TimeProvider time, ILogger<Subscription> logger, CancellationToken ct) =>
        ChangeAsync(user, db, providers, time, logger, cancel: true, ct);

    private static Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> ResumeAsync(
        ClaimsPrincipal user, IdentityContext db, PaymentProviders providers, TimeProvider time, ILogger<Subscription> logger, CancellationToken ct) =>
        ChangeAsync(user, db, providers, time, logger, cancel: false, ct);

    private static async Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> ChangeAsync(
        ClaimsPrincipal user, IdentityContext db, PaymentProviders providers, TimeProvider time, ILogger logger, bool cancel, CancellationToken ct)
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

    private static async Task<Results<Ok, BadRequest, NotFound, UnprocessableEntity, Conflict>> WebhookAsync(
        string provider, HttpRequest request, PaymentProviders providers, SubscriptionLedger ledger, TimeProvider time,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (providers.Named(provider) is not { } named)
        {
            return TypedResults.NotFound();
        }
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (named.ReadWebhook(body, request.Headers, time.GetUtcNow()) is not { } e)
        {
            loggers.CreateLogger(typeof(PaymentsApi)).LogWarning("A {Provider} webhook was refused: bad or stale signature, or not an event the app acts on", provider);
            return TypedResults.BadRequest();
        }
        if (e.Kind == PaymentEventKind.Ignored)
        {
            return TypedResults.Ok();
        }
        // An unknown subscription is 422 so the provider retries (a first payment's session may not be saved yet); a
        // refused payment is 409, and logged to be refunded.
        // A change to a subscription the app doesn't have (its account deleted) is acknowledged: a retry changes nothing.
        return await ledger.ApplyAsync(named.Name, e, ct) switch
        {
            LedgerResult.Unknown => TypedResults.UnprocessableEntity(),
            LedgerResult.Refused => TypedResults.Conflict(),
            _ => TypedResults.Ok()
        };
    }

    /// <summary>
    /// The active subscription, else the most recent; for the plan page and the export.
    /// </summary>
    internal static Task<Subscription?> LatestAsync(IdentityContext db, string userId, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Status)
            .ThenByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    internal static async Task<List<ReceiptDto>> ReceiptsAsync(IdentityContext db, string userId, CancellationToken ct) =>
        await db.Receipts.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.PaidAtUtc)
            .Select(r => new ReceiptDto(r.Plan, r.Interval, r.Amount, r.Currency, r.PaidAtUtc, r.PeriodEndUtc, r.Provider))
            .ToListAsync(ct);

    /// <summary>
    /// The first step of an account deletion, before the login goes: stops every running subscription at its provider at
    /// once, so nobody is charged for an account that's gone. A provider that fails (down, timing out) is logged to be
    /// stopped by hand and doesn't stop the deletion.
    /// </summary>
    internal static async Task StopSubscriptionsAsync(IdentityContext db, PaymentProviders providers, string userId, ILogger logger, CancellationToken ct)
    {
        foreach (var subscription in await db.Subscriptions.Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active).ToListAsync(ct))
        {
            try
            {
                await (providers.Named(subscription.Provider)?.CancelAsync(subscription, now: true, ct) ?? Task.CompletedTask);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Subscription {Subscription} couldn't be stopped at {Provider}; stop it there by hand", subscription.ProviderSubscriptionId, subscription.Provider);
            }
        }
    }

    /// <summary>
    /// Marks the user's payment records for deletion, in the same save as the profile (the provider keeps its own for its
    /// books).
    /// </summary>
    internal static async Task RemoveRecordsAsync(IdentityContext db, string userId, CancellationToken ct)
    {
        db.Receipts.RemoveRange(await db.Receipts.Where(r => r.UserId == userId).ToListAsync(ct));
        db.Subscriptions.RemoveRange(await db.Subscriptions.Where(s => s.UserId == userId).ToListAsync(ct));
        db.CheckoutSessions.RemoveRange(await db.CheckoutSessions.Where(c => c.UserId == userId).ToListAsync(ct));
    }

    internal static SubscriptionDto ToDto(Subscription s, PaymentProviders providers) =>
        new(s.Plan, s.Interval, s.Status == SubscriptionStatus.Active, s.CancelAtPeriodEnd, s.CurrentPeriodEndUtc, s.EndedAtUtc, s.Provider,
            s.Status == SubscriptionStatus.Active ? providers.Named(s.Provider)?.ManageUrl(s) : null);
}
