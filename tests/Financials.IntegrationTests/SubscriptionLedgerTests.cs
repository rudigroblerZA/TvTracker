using Financials.API.Payments;
using Financials.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TvTracker.IntegrationEvents;
using TvTracker.Persistence;
using TvTracker.Tests;

namespace Financials.IntegrationTests;

/// <summary>
/// The subscription ledger through its one interface, <see cref="SubscriptionLedger.ApplyAsync"/>: a provider's
/// events go in, and what's asserted is what the app is left with (subscriptions, receipts) and what it
/// told the other services, Identity above all, which moves the plan. Each rule in the ledger's own description has a test here.
/// </summary>
public sealed class SubscriptionLedgerTests(FinancialsDatabase database) : IClassFixture<FinancialsDatabase>
{
    private const string Provider = "test";
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Start);
    private readonly RecordingEventBus bus = new();
    private readonly string userId = $"user-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- A first payment ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_payment_starts_the_subscription_keeps_a_receipt_and_says_it_started()
    {
        var session = await CheckoutAsync(Plans.Plus, PlanPrices.Month, 4.99m, "EUR");

        var result = await ApplyAsync(Paid("evt-1", "sub-1", session));

        Assert.Equal(LedgerResult.Applied, result);
        await using var db = database.Context();
        var subscription = await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct);
        Assert.Equal((SubscriptionStatus.Active, Plans.Plus, PlanPrices.Month, 4.99m, "EUR"),
            (subscription.Status, subscription.Plan, subscription.Interval, subscription.Amount, subscription.Currency));
        Assert.Equal(Start.UtcDateTime.AddMonths(1), subscription.CurrentPeriodEndUtc);

        var receipt = await db.Receipts.SingleAsync(r => r.UserId == userId, Ct);
        Assert.Equal((subscription.Id, 4.99m, "EUR", subscription.CurrentPeriodEndUtc), (receipt.SubscriptionId, receipt.Amount, receipt.Currency, receipt.PeriodEndUtc));

        Assert.NotNull((await db.CheckoutSessions.SingleAsync(s => s.Id == session, Ct)).CompletedAtUtc);
        Assert.Equal((Plans.Plus, true), Assert.Single(PlanChanges()));
    }

    [Fact]
    public async Task The_same_event_again_changes_nothing()
    {
        var session = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", session));
        bus.Clear();

        var again = await ApplyAsync(Paid("evt-1", "sub-1", session));

        Assert.Equal(LedgerResult.Duplicate, again);
        await using var db = database.Context();
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.UserId == userId, Ct));
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task A_payment_for_a_checkout_the_app_doesnt_know_is_left_for_the_provider_to_send_again()
    {
        var notSavedYet = Guid.NewGuid();

        var early = await ApplyAsync(Paid("evt-1", "sub-1", notSavedYet));

        Assert.Equal(LedgerResult.Unknown, early);
        await using (var db = database.Context())
        {
            Assert.False(await db.Subscriptions.AnyAsync(s => s.ProviderSubscriptionId == Own("sub-1"), Ct));
            // Not recorded as handled: the retry has to be able to apply it.
            Assert.False(await db.ProcessedPaymentEvents.AnyAsync(p => p.EventId == Own("evt-1"), Ct));
        }

        await CheckoutAsync(id: notSavedYet);
        Assert.Equal(LedgerResult.Applied, await ApplyAsync(Paid("evt-1", "sub-1", notSavedYet)));
    }

    [Fact]
    public async Task A_payment_naming_another_providers_checkout_is_unknown()
    {
        var session = await CheckoutAsync(provider: "another");

        Assert.Equal(LedgerResult.Unknown, await ApplyAsync(Paid("evt-1", "sub-1", session)));
    }

    // --- One running at a time, one per checkout ----------------------------------------------------------------------

    [Fact]
    public async Task A_checkout_starts_one_subscription_only()
    {
        var session = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", session));

        var second = await ApplyAsync(Paid("evt-2", "sub-2", session));

        Assert.Equal(LedgerResult.Refused, second);
        await using var db = database.Context();
        Assert.Equal(Own("sub-1"), (await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).ProviderSubscriptionId);
    }

    [Fact]
    public async Task A_second_checkout_paid_while_one_subscription_runs_is_refused()
    {
        var first = await CheckoutAsync();
        var second = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", first));

        var result = await ApplyAsync(Paid("evt-2", "sub-2", second));

        Assert.Equal(LedgerResult.Refused, result);
        await using var db = database.Context();
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.UserId == userId, Ct));
        Assert.Null((await db.CheckoutSessions.SingleAsync(s => s.Id == second, Ct)).CompletedAtUtc);
    }

    [Fact]
    public async Task Two_checkouts_paid_at_the_same_moment_leave_one_subscription()
    {
        var first = await CheckoutAsync();
        var second = await CheckoutAsync();

        // The other payment lands after this one's own "is one running?" check and before its save: only the
        // database's one-running index is left to stop it.
        var result = await ApplyAsync(Paid("evt-2", "sub-2", second),
            new BeforeSave(async () => Assert.Equal(LedgerResult.Applied, await ApplyAsync(Paid("evt-1", "sub-1", first)))));

        Assert.Equal(LedgerResult.Refused, result);
        await using var db = database.Context();
        Assert.Equal(Own("sub-1"), (await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).ProviderSubscriptionId);
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.UserId == userId, Ct));
        Assert.Null((await db.CheckoutSessions.SingleAsync(s => s.Id == second, Ct)).CompletedAtUtc);
    }

    [Fact]
    public async Task One_event_delivered_twice_at_the_same_moment_is_applied_once()
    {
        var session = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", session));
        var renewal = Paid("evt-2", "sub-1", paymentId: "pay-2");

        var result = await ApplyAsync(renewal, new BeforeSave(async () => Assert.Equal(LedgerResult.Applied, await ApplyAsync(renewal))));

        Assert.Equal(LedgerResult.Duplicate, result);
        await using var db = database.Context();
        Assert.Equal(2, await db.Receipts.CountAsync(r => r.UserId == userId, Ct));
        Assert.Equal(Start.UtcDateTime.AddMonths(2), (await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).CurrentPeriodEndUtc);
    }

    // --- Renewals -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_renewal_extends_the_period_and_keeps_a_receipt_without_touching_the_plan()
    {
        var session = await CheckoutAsync(amount: 4.99m, currency: "EUR");
        await ApplyAsync(Paid("evt-1", "sub-1", session));
        bus.Clear();
        time.Advance(TimeSpan.FromDays(30));
        var providerEnd = Start.UtcDateTime.AddMonths(2).AddHours(1);

        var result = await ApplyAsync(Paid("evt-2", "sub-1", paymentId: "pay-2", periodEnd: providerEnd));

        Assert.Equal(LedgerResult.Applied, result);
        await using var db = database.Context();
        Assert.Equal(providerEnd, (await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).CurrentPeriodEndUtc);
        var receipts = await db.Receipts.Where(r => r.UserId == userId).OrderBy(r => r.PaidAtUtc).ToListAsync(Ct);
        Assert.Equal(2, receipts.Count);
        // The renewal didn't state a price, so its receipt carries the subscription's own.
        Assert.Equal((4.99m, "EUR"), (receipts[1].Amount, receipts[1].Currency));
        Assert.Empty(PlanChanges());
    }

    [Fact]
    public async Task A_receipt_carries_the_price_the_provider_states()
    {
        var session = await CheckoutAsync(amount: 4.99m, currency: "EUR");
        await ApplyAsync(Paid("evt-1", "sub-1", session));

        await ApplyAsync(Paid("evt-2", "sub-1", paymentId: "pay-2") with { Amount = 5.49m, Currency = "GBP" });

        await using var db = database.Context();
        var renewal = await db.Receipts.SingleAsync(r => r.ProviderPaymentId == Own("pay-2"), Ct);
        Assert.Equal((5.49m, "GBP"), (renewal.Amount, renewal.Currency));
    }

    [Fact]
    public async Task A_payment_never_shortens_what_was_already_paid()
    {
        var session = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", session, periodEnd: Start.UtcDateTime.AddMonths(3)));

        await ApplyAsync(Paid("evt-2", "sub-1", paymentId: "pay-2", periodEnd: Start.UtcDateTime.AddMonths(1)));

        await using var db = database.Context();
        Assert.Equal(Start.UtcDateTime.AddMonths(3), (await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).CurrentPeriodEndUtc);
    }

    // --- Cancelling, resuming, ending -----------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_stops_renewal_and_resuming_takes_it_back()
    {
        var session = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", session));
        bus.Clear();

        Assert.Equal(LedgerResult.Applied, await ApplyAsync(Event("evt-2", PaymentEventKind.CancelScheduled, "sub-1")));
        await using (var db = database.Context())
        {
            var cancelled = await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct);
            Assert.True(cancelled.CancelAtPeriodEnd);
            // It runs to the end of what was paid: still active, still on the plan.
            Assert.Equal(SubscriptionStatus.Active, cancelled.Status);
        }

        Assert.Equal(LedgerResult.Applied, await ApplyAsync(Event("evt-3", PaymentEventKind.Resumed, "sub-1")));
        await using (var db = database.Context())
        {
            Assert.False((await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).CancelAtPeriodEnd);
        }
        Assert.Empty(PlanChanges());
    }

    [Fact]
    public async Task An_ending_says_the_member_stopped_paying()
    {
        var session = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", session));
        bus.Clear();
        time.Advance(TimeSpan.FromDays(31));

        var result = await ApplyAsync(Event("evt-2", PaymentEventKind.Ended, "sub-1"));

        Assert.Equal(LedgerResult.Applied, result);
        await using var db = database.Context();
        var ended = await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct);
        Assert.Equal((SubscriptionStatus.Ended, (DateTime?)time.GetUtcNow().UtcDateTime), (ended.Status, ended.EndedAtUtc));
        Assert.Equal((Plans.Plus, false), Assert.Single(PlanChanges()));
    }

    [Fact]
    public async Task An_ended_subscription_paid_again_comes_back_with_its_plan()
    {
        var session = await CheckoutAsync(Plans.Plus);
        await ApplyAsync(Paid("evt-1", "sub-1", session));
        time.Advance(TimeSpan.FromDays(40));
        await ApplyAsync(Event("evt-2", PaymentEventKind.Ended, "sub-1"));
        bus.Clear();
        time.Advance(TimeSpan.FromDays(20));

        var result = await ApplyAsync(Paid("evt-3", "sub-1", paymentId: "pay-3"));

        Assert.Equal(LedgerResult.Applied, result);
        await using var db = database.Context();
        var revived = await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct);
        Assert.Equal((SubscriptionStatus.Active, (DateTime?)null), (revived.Status, revived.EndedAtUtc));
        // A lapsed subscription paid again starts afresh from now, not from where the old period ended.
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddMonths(1), revived.CurrentPeriodEndUtc);
        Assert.Equal((Plans.Plus, true), Assert.Single(PlanChanges()));
    }

    [Fact]
    public async Task An_ended_subscription_cant_come_back_beside_another_that_runs()
    {
        var first = await CheckoutAsync();
        await ApplyAsync(Paid("evt-1", "sub-1", first));
        await ApplyAsync(Event("evt-2", PaymentEventKind.Ended, "sub-1"));
        var second = await CheckoutAsync();
        await ApplyAsync(Paid("evt-3", "sub-2", second));

        var late = await ApplyAsync(Paid("evt-4", "sub-1", paymentId: "pay-4"));

        Assert.Equal(LedgerResult.Refused, late);
        await using var db = database.Context();
        Assert.Equal(Own("sub-2"), (await db.Subscriptions.SingleAsync(s => s.UserId == userId && s.Status == SubscriptionStatus.Active, Ct)).ProviderSubscriptionId);
    }

    [Theory]
    [InlineData(PaymentEventKind.CancelScheduled)]
    [InlineData(PaymentEventKind.Resumed)]
    [InlineData(PaymentEventKind.Ended)]
    public async Task A_change_to_a_subscription_the_app_doesnt_have_needs_no_retry(PaymentEventKind kind)
    {
        // Its account was deleted, which stopped it at the provider; the provider's confirmation then arrives.
        Assert.Equal(LedgerResult.NotHere, await ApplyAsync(Event("evt-1", kind, "sub-gone")));
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends one event through the ledger, on a context of its own as each webhook request has.
    /// </summary>
    private async Task<LedgerResult> ApplyAsync(PaymentEvent e, params IInterceptor[] interceptors)
    {
        await using var db = database.Context(interceptors);
        var outbox = new IntegrationEventOutbox<FinancialsContext>(db, bus, NullLogger<IntegrationEventOutbox<FinancialsContext>>.Instance);
        return await new SubscriptionLedger(db, outbox, time, NullLogger<SubscriptionLedger>.Instance).ApplyAsync(Provider, e, Ct);
    }

    /// <summary>
    /// A checkout the app made for this test's user, as buying a plan does.
    /// </summary>
    private async Task<Guid> CheckoutAsync(string plan = Plans.Plus, string interval = PlanPrices.Month, decimal amount = 4.99m,
        string currency = "USD", string provider = Provider, Guid? id = null)
    {
        await using var db = database.Context();
        var session = new CheckoutSession
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId,
            Provider = provider,
            Plan = plan,
            Interval = interval,
            Amount = amount,
            Currency = currency,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime
        };
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session.Id;
    }

    /// <summary>
    /// Provider ids are the provider's, shared by every user; each test's are made its own so tests in one database
    /// don't meet.
    /// </summary>
    private string Own(string id) => $"{id}-{userId}";

    private PaymentEvent Paid(string eventId, string subscriptionId, Guid? session = null, string? paymentId = null, DateTime? periodEnd = null) =>
        new(Own(eventId), PaymentEventKind.Paid, Own(subscriptionId), session, paymentId is null ? null : Own(paymentId), PeriodEndUtc: periodEnd);

    private PaymentEvent Event(string eventId, PaymentEventKind kind, string subscriptionId) => new(Own(eventId), kind, Own(subscriptionId));

    /// <summary>
    /// What Identity was told about this user's plan, in order: the plan, and whether they started (true) or stopped paying for it.
    /// </summary>
    private IEnumerable<(string Plan, bool Started)> PlanChanges() => bus.Published
        .Select(e => e switch
        {
            SubscriptionStartedIntegrationEvent started when started.UserId == userId => (started.Plan, true),
            SubscriptionEndedIntegrationEvent ended when ended.UserId == userId => (ended.Plan, false),
            _ => ((string Plan, bool Started)?)null
        })
        .OfType<(string Plan, bool Started)>();
}
