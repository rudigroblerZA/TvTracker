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
    Unknown
}

/// <summary>
/// Applies a provider's events to the app's sessions, subscriptions and receipts, and moves the plan.
/// <list type="bullet">
/// <item><b>Once:</b> each event id is recorded (<see cref="ProcessedPaymentEvent"/>) in the same save as its changes, so a
/// resent event, or two deliveries at once, apply one payment.</item>
/// <item><b>Credited to who checked out:</b> a first payment must name a checkout session the app made, and the
/// subscription is the session's user's; a webhook can't choose whose plan it moves.</item>
/// <item><b>Ending downgrades only what it paid for:</b> the plan falls back to Free only while it's still this
/// subscription's plan and no other active subscription pays for one; an admin's later change stands.</item>
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
                CheckoutSession? session = null;
                if (subscription is null)
                {
                    if (e.SessionId is not { } id || await db.CheckoutSessions.FindAsync([id], ct) is not { } found || found.Provider != provider)
                    {
                        return LedgerResult.Unknown;
                    }
                    session = found;
                    subscription = Start(provider, e, session, now);
                }
                await PaidAsync(subscription, e, session, now, ct);
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same event delivered twice at once: the other delivery applied it.
            return LedgerResult.Duplicate;
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
        var stillPaid = await db.Subscriptions.AnyAsync(s => s.UserId == subscription.UserId && s.Id != subscription.Id && s.Status == SubscriptionStatus.Active, ct);
        if (!stillPaid && await db.Profiles.FindAsync([subscription.UserId], ct) is { } profile && profile.Plan == subscription.Plan)
        {
            await PlanState.SetPaidPlanAsync(db, outbox, profile, Plans.Free, now, ct);
        }
    }

    private Subscription Start(string provider, PaymentEvent e, CheckoutSession session, DateTimeOffset now)
    {
        session.CompletedAtUtc ??= now.UtcDateTime;
        var subscription = new Subscription
        {
            Id = Guid.CreateVersion7(),
            UserId = session.UserId,
            Provider = provider,
            ProviderSubscriptionId = e.SubscriptionId,
            Plan = session.Plan,
            Interval = session.Interval,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEndUtc = e.PeriodEndUtc ?? PlanPrices.PeriodEnd(now.UtcDateTime, session.Interval),
            CreatedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime
        };
        db.Subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>
    /// Extends the paid period, keeps a receipt (the amount the provider reports, else the checkout's price) and moves
    /// the plan to the subscription's.
    /// </summary>
    private async Task PaidAsync(Subscription subscription, PaymentEvent e, CheckoutSession? session, DateTimeOffset now, CancellationToken ct)
    {
        var periodEnd = e.PeriodEndUtc ?? PlanPrices.PeriodEnd(Max(subscription.CurrentPeriodEndUtc, now.UtcDateTime), subscription.Interval);
        subscription.CurrentPeriodEndUtc = Max(subscription.CurrentPeriodEndUtc, periodEnd);
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
                Amount = e.Amount ?? session?.Amount ?? 0,
                Currency = e.Currency ?? session?.Currency ?? PlanPrices.Default.Currency,
                PaidAtUtc = now.UtcDateTime,
                PeriodEndUtc = subscription.CurrentPeriodEndUtc
            });
        }

        if (await db.Profiles.FindAsync([subscription.UserId], ct) is { } profile)
        {
            await PlanState.SetPaidPlanAsync(db, outbox, profile, subscription.Plan, now, ct);
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
