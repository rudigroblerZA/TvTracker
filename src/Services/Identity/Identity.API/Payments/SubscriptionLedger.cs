using Identity.API.Data;
using Identity.API.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;

namespace Identity.API.Payments;

public enum LedgerResult
{
    Applied,

    /// <summary>
    /// Already applied (providers resend).
    /// </summary>
    Duplicate,

    /// <summary>
    /// About a subscription or checkout the app doesn't know.
    /// </summary>
    Unknown,

    /// <summary>
    /// A payment the app won't apply: a checkout already used, or a second subscription for someone who has one running.
    /// The provider took the money, so it's logged for a refund.
    /// </summary>
    Refused
}

/// <summary>
/// Applies a provider's events to the app's sessions, subscriptions and receipts, and moves the plan.
/// <list type="bullet">
/// <item><b>Once:</b> each event id is recorded (<see cref="ProcessedPaymentEvent"/>) in the same save as its changes, so a
/// resent event, or two deliveries at once, apply one payment.</item>
/// <item><b>Credited to who checked out:</b> a first payment must name a checkout session the app made, and the
/// subscription is the session's user's; a webhook can't choose whose plan it moves.</item>
/// <item><b>One running at a time, one per checkout:</b> a checkout session starts one subscription, and a payment that
/// would start or revive one beside another that's running is refused (<see cref="LedgerResult.Refused"/>), so a double
/// click on Pay, two checkouts paid at once or a late renewal of an ended one can't leave two running.</item>
/// <item><b>The plan moves when a subscription starts or comes back,</b> not at each renewal, and an ending moves it back
/// only while it's still that subscription's plan and nothing else pays: an admin's change in between stands.</item>
/// <item><b>Receipts carry the price paid:</b> the provider's amount, else the subscription's own (kept from its checkout),
/// so a renewal that doesn't state it still records the right amount and currency.</item>
/// </list>
/// </summary>
public sealed class SubscriptionLedger(IdentityContext db, IIntegrationEventOutbox outbox, TimeProvider time, ILogger<SubscriptionLedger> logger)
{
    public async Task<LedgerResult> ApplyAsync(string provider, PaymentEvent e, CancellationToken ct)
    {
        if (await db.ProcessedPaymentEvents.AnyAsync(p => p.Provider == provider && p.EventId == e.Id, ct))
        {
            return LedgerResult.Duplicate;
        }
        var now = time.GetUtcNow();
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Provider == provider && s.ProviderSubscriptionId == e.SubscriptionId, ct);

        switch (e.Kind)
        {
            case PaymentEventKind.Paid:
                // A new subscription, or an ended one paid again: either moves the plan; a renewal doesn't.
                var starting = subscription is null || subscription.Status == SubscriptionStatus.Ended;
                if (subscription is null)
                {
                    if (e.SessionId is not { } id || await db.CheckoutSessions.FindAsync([id], ct) is not { } session || session.Provider != provider)
                    {
                        return LedgerResult.Unknown;
                    }
                    if (session.CompletedAtUtc is not null || await RunningAsync(session.UserId, except: null, ct))
                    {
                        return Refuse(e, session.UserId, session.CompletedAtUtc is not null ? "its checkout was already paid" : "another subscription is running");
                    }
                    session.CompletedAtUtc = now.UtcDateTime;
                    subscription = Start(provider, e, session, now);
                    db.Subscriptions.Add(subscription);
                }
                else if (subscription.Status == SubscriptionStatus.Ended && await RunningAsync(subscription.UserId, subscription.Id, ct))
                {
                    return Refuse(e, subscription.UserId, "it ended and another subscription is running");
                }
                await PaidAsync(subscription, e, starting, now, ct);
                break;
            case PaymentEventKind.CancelScheduled or PaymentEventKind.Resumed when subscription is not null:
                subscription.CancelAtPeriodEnd = e.Kind == PaymentEventKind.CancelScheduled;
                subscription.UpdatedAtUtc = now.UtcDateTime;
                break;
            case PaymentEventKind.Ended when subscription is not null:
                await EndAsync(subscription, now, ct);
                break;
            default:
                return LedgerResult.Unknown;
        }

        db.ProcessedPaymentEvents.Add(new ProcessedPaymentEvent { Provider = provider, EventId = e.Id, ProcessedAtUtc = now.UtcDateTime });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // Two payments starting subscriptions for one user at once: the database's one-running index caught the
            // second, past RunningAsync's check. Anything else is the same event delivered twice at once.
            return pg.ConstraintName == IdentityContext.OneRunningSubscription
                ? Refuse(e, subscription?.UserId ?? "", "another subscription started at the same moment")
                : LedgerResult.Duplicate;
        }
        await outbox.PublishAddedAsync(ct);
        logger.LogInformation("Payment event {Event} ({Kind}) applied to subscription {Subscription}", e.Id, e.Kind, e.SubscriptionId);
        return LedgerResult.Applied;
    }

    /// <summary>
    /// Ends a subscription and moves the plan back when nothing else pays for it; the caller saves and publishes.
    /// </summary>
    public async Task EndAsync(Subscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        if (subscription.Status == SubscriptionStatus.Ended)
        {
            return;
        }
        subscription.Status = SubscriptionStatus.Ended;
        subscription.EndedAtUtc = now.UtcDateTime;
        subscription.UpdatedAtUtc = now.UtcDateTime;
        if (!await RunningAsync(subscription.UserId, subscription.Id, ct) && await db.Profiles.FindAsync([subscription.UserId], ct) is { } profile && profile.Plan == subscription.Plan)
        {
            await PlanState.SetPaidPlanAsync(db, outbox, profile, Plans.Free, now, ct);
        }
    }

    private Task<bool> RunningAsync(string userId, Guid? except, CancellationToken ct) =>
        db.Subscriptions.AnyAsync(s => s.UserId == userId && s.Id != except && s.Status == SubscriptionStatus.Active, ct);

    private LedgerResult Refuse(PaymentEvent e, string userId, string why)
    {
        logger.LogError("Payment event {Event} for {UserId} refused ({Why}); the provider took the payment, so refund it there", e.Id, userId, why);
        return LedgerResult.Refused;
    }

    /// <summary>
    /// The subscription a checkout's first payment starts, at the checkout's plan, interval and price, with nothing paid
    /// yet: that payment's <see cref="PaidThrough"/> gives it its first period.
    /// </summary>
    internal static Subscription Start(string provider, PaymentEvent e, CheckoutSession session, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = session.UserId,
            Provider = provider,
            ProviderSubscriptionId = e.SubscriptionId,
            Plan = session.Plan,
            Interval = session.Interval,
            Amount = session.Amount,
            Currency = session.Currency,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEndUtc = now.UtcDateTime,
            CreatedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime
        };

    /// <summary>
    /// Where a payment leaves the paid period: the provider's date when it gives one, else one interval on from the
    /// period's end (from now, once it's past: a lapsed subscription paid again starts afresh). A payment never shortens
    /// what was already paid.
    /// </summary>
    internal static DateTime PaidThrough(DateTime periodEndUtc, DateTime? providerEndUtc, string interval, DateTime nowUtc) =>
        Max(periodEndUtc, providerEndUtc ?? PlanPrices.PeriodEnd(Max(periodEndUtc, nowUtc), interval));

    /// <summary>
    /// Extends the paid period and keeps a receipt; a subscription <paramref name="starting"/> (new, or back after ending)
    /// also moves the plan to its own.
    /// </summary>
    private async Task PaidAsync(Subscription subscription, PaymentEvent e, bool starting, DateTimeOffset now, CancellationToken ct)
    {
        subscription.CurrentPeriodEndUtc = PaidThrough(subscription.CurrentPeriodEndUtc, e.PeriodEndUtc, subscription.Interval, now.UtcDateTime);
        subscription.Status = SubscriptionStatus.Active;
        subscription.EndedAtUtc = null;
        subscription.UpdatedAtUtc = now.UtcDateTime;

        var paymentId = e.PaymentId ?? e.Id;
        if (!await db.Receipts.AnyAsync(r => r.Provider == subscription.Provider && r.ProviderPaymentId == paymentId, ct))
        {
            db.Receipts.Add(new PaymentReceipt
            {
                Id = Guid.CreateVersion7(),
                UserId = subscription.UserId,
                SubscriptionId = subscription.Id,
                Provider = subscription.Provider,
                ProviderPaymentId = paymentId,
                Plan = subscription.Plan,
                Interval = subscription.Interval,
                Amount = e.Amount ?? subscription.Amount,
                Currency = e.Currency ?? subscription.Currency,
                PaidAtUtc = now.UtcDateTime,
                PeriodEndUtc = subscription.CurrentPeriodEndUtc
            });
        }

        if (starting && await db.Profiles.FindAsync([subscription.UserId], ct) is { } profile)
        {
            await PlanState.SetPaidPlanAsync(db, outbox, profile, subscription.Plan, now, ct);
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
