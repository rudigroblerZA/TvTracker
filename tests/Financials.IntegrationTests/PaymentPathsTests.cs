using System.Security.Claims;
using Financials.API.Apis;
using Financials.API.IntegrationEvents;
using Financials.API.Payments;
using Financials.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TvTracker.IntegrationEvents;

namespace Financials.IntegrationTests;

/// <summary>
/// What a member does with a subscription they have, and what happens to it when their account goes: cancelling and
/// resuming (the provider is asked first, then it's recorded), their payment records in the data export, and the
/// account's deletion (stopped at the provider, records removed). The provider is a stand-in that notes what it was
/// asked and can be told not to answer.
/// </summary>
public sealed class PaymentPathsTests(FinancialsDatabase database) : IClassFixture<FinancialsDatabase>
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Start);
    private readonly RecordingProvider provider = new();
    private readonly string userId = $"user-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Cancel and resume -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_asks_the_provider_and_keeps_the_plan_to_the_period_end()
    {
        await SubscriptionAsync(userId);

        var cancelled = await CancelAsync();

        var dto = Assert.IsType<Ok<SubscriptionDto>>(cancelled.Result).Value!;
        Assert.True(dto is { Active: true, CancelAtPeriodEnd: true });
        Assert.Equal(["cancel-at-period-end"], provider.Asked);
        await using var db = database.Context();
        Assert.True((await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).CancelAtPeriodEnd);
    }

    [Fact]
    public async Task Resuming_undoes_a_cancellation_and_asking_twice_asks_the_provider_once()
    {
        await SubscriptionAsync(userId);
        await CancelAsync();
        await CancelAsync();

        var resumed = await ResumeAsync();
        await ResumeAsync();

        Assert.False(Assert.IsType<Ok<SubscriptionDto>>(resumed.Result).Value!.CancelAtPeriodEnd);
        Assert.Equal(["cancel-at-period-end", "resume"], provider.Asked);
    }

    [Fact]
    public async Task A_provider_that_doesnt_answer_leaves_the_subscription_as_it_was()
    {
        await SubscriptionAsync(userId);
        provider.Fails = true;

        var cancelled = await CancelAsync();

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ProblemHttpResult>(cancelled.Result).StatusCode);
        await using var db = database.Context();
        Assert.False((await db.Subscriptions.SingleAsync(s => s.UserId == userId, Ct)).CancelAtPeriodEnd);
    }

    [Fact]
    public async Task There_is_nothing_to_cancel_without_a_running_subscription()
    {
        await SubscriptionAsync(userId, SubscriptionStatus.Ended);

        Assert.IsType<NotFound>((await CancelAsync()).Result);
        Assert.Empty(provider.Asked);
    }

    // --- The data export ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_export_has_the_members_own_subscription_and_receipts_and_nobody_elses()
    {
        await SubscriptionAsync(userId, receipts: 2);
        await SubscriptionAsync($"other-{Guid.NewGuid():N}", receipts: 3);

        await using var db = database.Context();
        var export = (await PaymentsApi.ExportAsync(Member(), db, Providers(), Ct)).Value!;

        Assert.Equal((Plans.Plus, true), (export.Subscription!.Plan, export.Subscription.Active));
        Assert.Equal(2, export.Receipts.Count);
        Assert.All(export.Receipts, r => Assert.Equal((4.99m, "EUR"), (r.Amount, r.Currency)));
    }

    [Fact]
    public async Task The_export_of_someone_who_never_paid_is_empty()
    {
        await using var db = database.Context();
        var export = (await PaymentsApi.ExportAsync(Member(), db, Providers(), Ct)).Value!;

        Assert.Null(export.Subscription);
        Assert.Empty(export.Receipts);
    }

    // --- The account deleted ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_deleted_accounts_subscription_is_stopped_at_the_provider_and_its_records_removed()
    {
        var other = $"other-{Guid.NewGuid():N}";
        await SubscriptionAsync(userId, receipts: 2);
        await SubscriptionAsync(other, receipts: 1);

        await DeletedAsync(new UserDeletedIntegrationEvent(userId));

        Assert.Equal(["cancel-now"], provider.Asked);
        await using var db = database.Context();
        Assert.False(await db.Subscriptions.AnyAsync(s => s.UserId == userId, Ct));
        Assert.False(await db.Receipts.AnyAsync(r => r.UserId == userId, Ct));
        Assert.False(await db.CheckoutSessions.AnyAsync(c => c.UserId == userId, Ct));
        // Nobody else's.
        Assert.True(await db.Subscriptions.AnyAsync(s => s.UserId == other, Ct));
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.UserId == other, Ct));
    }

    [Fact]
    public async Task The_records_go_even_when_the_provider_doesnt_answer()
    {
        await SubscriptionAsync(userId, receipts: 1);
        provider.Fails = true;

        await DeletedAsync(new UserDeletedIntegrationEvent(userId));

        await using var db = database.Context();
        Assert.False(await db.Subscriptions.AnyAsync(s => s.UserId == userId, Ct));
        Assert.False(await db.Receipts.AnyAsync(r => r.UserId == userId, Ct));
    }

    [Fact]
    public async Task The_same_deletion_delivered_twice_asks_the_provider_once()
    {
        await SubscriptionAsync(userId);
        var deleted = new UserDeletedIntegrationEvent(userId);

        await DeletedAsync(deleted);
        await DeletedAsync(deleted);

        Assert.Equal(["cancel-now"], provider.Asked);
    }

    [Fact]
    public async Task An_ended_subscription_isnt_stopped_again_but_its_records_still_go()
    {
        await SubscriptionAsync(userId, SubscriptionStatus.Ended, receipts: 1);

        await DeletedAsync(new UserDeletedIntegrationEvent(userId));

        Assert.Empty(provider.Asked);
        await using var db = database.Context();
        Assert.False(await db.Receipts.AnyAsync(r => r.UserId == userId, Ct));
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private async Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> CancelAsync()
    {
        await using var db = database.Context();
        return await PaymentsApi.CancelAsync(Member(), db, Providers(), time, NullLogger<Subscription>.Instance, Ct);
    }

    private async Task<Results<Ok<SubscriptionDto>, NotFound, ProblemHttpResult>> ResumeAsync()
    {
        await using var db = database.Context();
        return await PaymentsApi.ResumeAsync(Member(), db, Providers(), time, NullLogger<Subscription>.Instance, Ct);
    }

    private async Task DeletedAsync(UserDeletedIntegrationEvent e)
    {
        await using var db = database.Context();
        await new UserDeletedHandler(db, Providers(), NullLogger<UserDeletedHandler>.Instance).HandleAsync(e, Ct);
    }

    private ClaimsPrincipal Member() => new(new ClaimsIdentity([new Claim("sub", userId)], "test"));

    private PaymentProviders Providers() => new([provider], Options.Create(new PaymentOptions()));

    /// <summary>
    /// A subscription as the ledger leaves one after a checkout was paid, with its checkout and receipts.
    /// </summary>
    private async Task SubscriptionAsync(string user, SubscriptionStatus status = SubscriptionStatus.Active, int receipts = 0)
    {
        await using var db = database.Context();
        var now = time.GetUtcNow().UtcDateTime;
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), UserId = user, Provider = RecordingProvider.ProviderName, ProviderSubscriptionId = $"sub-{Guid.NewGuid():N}",
            Plan = Plans.Plus, Interval = PlanPrices.Month, Amount = 4.99m, Currency = "EUR", Status = status,
            CurrentPeriodEndUtc = now.AddDays(20), CreatedAtUtc = now, UpdatedAtUtc = now, EndedAtUtc = status == SubscriptionStatus.Ended ? now : null
        };
        db.Subscriptions.Add(subscription);
        db.CheckoutSessions.Add(new CheckoutSession
        {
            Id = Guid.NewGuid(), UserId = user, Provider = RecordingProvider.ProviderName, Plan = Plans.Plus, Interval = PlanPrices.Month,
            Amount = 4.99m, Currency = "EUR", CreatedAtUtc = now, CompletedAtUtc = now
        });
        for (var i = 0; i < receipts; i++)
        {
            db.Receipts.Add(new PaymentReceipt
            {
                Id = Guid.NewGuid(), UserId = user, SubscriptionId = subscription.Id, Provider = RecordingProvider.ProviderName,
                ProviderPaymentId = $"pay-{Guid.NewGuid():N}", Plan = Plans.Plus, Interval = PlanPrices.Month, Amount = 4.99m, Currency = "EUR",
                PaidAtUtc = now.AddDays(-30 * i), PeriodEndUtc = now.AddDays(20 - 30 * i)
            });
        }
        await db.SaveChangesAsync(Ct);
    }

    private sealed class RecordingProvider : IPaymentProvider
    {
        public const string ProviderName = "recording";

        public List<string> Asked { get; } = [];

        /// <summary>
        /// The provider doesn't answer: every call fails as an unreachable one would.
        /// </summary>
        public bool Fails { get; set; }

        public string Name => ProviderName;

        public Task<Uri> CreateCheckoutAsync(CheckoutSession session, string returnUrl, CancellationToken ct) => throw new NotSupportedException();

        public Task CancelAsync(Subscription subscription, bool now, CancellationToken ct) => Ask(now ? "cancel-now" : "cancel-at-period-end");

        public Task ResumeAsync(Subscription subscription, CancellationToken ct) => Ask("resume");

        public PaymentEvent? ReadWebhook(string body, IHeaderDictionary headers, DateTimeOffset now) => null;

        public string? ManageUrl(Subscription subscription) => null;

        private Task Ask(string what)
        {
            if (Fails)
            {
                throw new HttpRequestException("The provider didn't answer.");
            }
            Asked.Add(what);
            return Task.CompletedTask;
        }
    }
}
