using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TvTracker.EventBus;
using TvTracker.IntegrationEvents;
using TvTracker.Tests;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace Privacy.IntegrationTests;

/// <summary>
/// Deleting an account leaves nothing of the member in any service that keeps something about them. For each service:
/// its database is made by its own migrations and given a row per table that names a member, for two members; its own
/// handler of <see cref="UserDeletedIntegrationEvent"/> is run for one of them; and then the whole database is searched
/// for that member's id. The other member's rows must all still be there.
/// <para>
/// Identity.API, where the deletion starts, isn't here: it removes the profile and the login itself, in the request.
/// </para>
/// </summary>
public sealed class ErasureTests(PostgresServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task Tracking_forgets_a_deleted_member() => ErasedAsync<Tracking.Data.TrackingContext>(db => new Tracking.API.IntegrationEvents.UserDeletedHandler(db));

    [Fact]
    public Task Ratings_forgets_a_deleted_member() => ErasedAsync<Ratings.Data.RatingsContext>(db => new Ratings.API.IntegrationEvents.UserDeletedHandler(db));

    [Fact]
    public Task Social_forgets_a_deleted_member() => ErasedAsync<Social.Data.SocialContext>(db => new Social.API.IntegrationEvents.ProfileEventHandlers(db, new FakeTimeProvider()));

    [Fact]
    public Task Recommendations_forgets_a_deleted_member() => ErasedAsync<Recommendations.Data.RecommendationsContext>(db => new Recommendations.API.IntegrationEvents.UserDeletedHandler(db));

    [Fact]
    public Task Notifications_forgets_a_deleted_member() => ErasedAsync<Notifications.Data.NotificationsContext>(
        db => new Notifications.API.IntegrationEvents.UserEventHandlers(db,
            // No notification hub is configured, so devices are only forgotten here.
            new Notifications.API.Services.NotificationHubPush(db, new ConfigurationBuilder().Build(),
                NullLogger<Notifications.API.Services.NotificationHubPush>.Instance),
            new FakeTimeProvider()));

    [Fact]
    public Task Research_forgets_a_deleted_member() => ErasedAsync<Research.Data.ResearchContext>(db => new Research.API.IntegrationEvents.UserDeletedHandler(db));

    [Fact]
    public Task Analytics_forgets_a_deleted_member() => ErasedAsync<Analytics.Data.AnalyticsContext>(db => new Analytics.Worker.IntegrationEvents.UserDeletedHandler(db));

    [Fact]
    public Task Financials_forgets_a_deleted_member() => ErasedAsync<Financials.Data.FinancialsContext>(
        db => new Financials.API.IntegrationEvents.UserDeletedHandler(db,
            // No provider is registered, so there is nowhere to stop a subscription: only the records go.
            new Financials.API.Payments.PaymentProviders([], Options.Create(new Financials.API.Payments.PaymentOptions())),
            NullLogger<Financials.API.IntegrationEvents.UserDeletedHandler>.Instance));

    /// <summary>
    /// A moderator who deletes their account: what they did stays in the log, and on the corrections of other members
    /// that they reviewed, without their id or name.
    /// </summary>
    [Fact]
    public async Task Ratings_keeps_a_former_moderators_work_without_their_name()
    {
        var options = new DbContextOptionsBuilder<Ratings.Data.RatingsContext>().UseNpgsql(await server.CreateDatabaseAsync()).Options;
        var (moderator, member) = (Member("moderator"), Member("member"));
        var correction = Guid.NewGuid();
        await using (var db = new Ratings.Data.RatingsContext(options))
        {
            await db.Database.MigrateAsync(Ct);
            db.Corrections.Add(new Ratings.Data.Correction
            {
                Id = correction, ShowId = Guid.NewGuid(), Field = "title", SuggestedValue = "Severance", UserId = member,
                DisplayName = MemberData.NameOf(member), Status = "accepted", ReviewedBy = MemberData.NameOf(moderator),
                ReviewedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
            });
            db.ModerationActions.Add(new Ratings.Data.ModerationAction
            {
                Id = Guid.NewGuid(), ModeratorId = moderator, ModeratorName = MemberData.NameOf(moderator), Action = "accept",
                TargetKind = "correction", TargetId = correction, AtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new Ratings.Data.RatingsContext(options))
        {
            await new Ratings.API.IntegrationEvents.UserDeletedHandler(db).HandleAsync(new UserDeletedIntegrationEvent(moderator), Ct);
        }

        await using var after = new Ratings.Data.RatingsContext(options);
        Assert.Empty(await MemberData.MentionsAsync(after, moderator, Ct));
        var kept = await after.Corrections.SingleAsync(Ct);
        Assert.Equal((member, "accepted", "a former moderator"), (kept.UserId, kept.Status, kept.ReviewedBy));
        Assert.Equal("a former moderator", (await after.ModerationActions.SingleAsync(Ct)).ModeratorName);
    }

    // Short enough for every column that holds a member's id or, with "name-" before it, their name.
    private static string Member(string who) => $"{who}-{Guid.NewGuid():N}"[..(who.Length + 13)];

    /// <summary>
    /// Every text column that might be about a person has been looked at: it's a member's id or name (and so searched
    /// for above), or it's listed here as being about something else. A column added to any service under a new name
    /// fails this until someone decides which.
    /// </summary>
    [Fact]
    public void Every_column_that_might_name_a_person_has_been_decided()
    {
        // About a show, an episode, a provider, a program or a thing, not a person.
        HashSet<string> notAboutAMember =
        [
            "Name", "ExternalId", "ShowExternalId", "ImdbId", "TvdbId", "TmdbId", "WikidataId", "GifId", "PlaceId", "EventId", "TransactionId",
            "ProviderSubscriptionId", "ProviderPaymentId", "PersonName", "ProviderName", "ShowName", "NetworkName", "Title", "EventTypeName"
        ];
        DbContext[] models =
        [
            Model<Tracking.Data.TrackingContext>(), Model<Ratings.Data.RatingsContext>(), Model<Social.Data.SocialContext>(),
            Model<Recommendations.Data.RecommendationsContext>(), Model<Notifications.Data.NotificationsContext>(),
            Model<Research.Data.ResearchContext>(), Model<Analytics.Data.AnalyticsContext>(), Model<Financials.Data.FinancialsContext>()
        ];

        var undecided = models.SelectMany(db => MemberData.Undecided(db, notAboutAMember).Select(c => $"{db.GetType().Name}: {c}")).ToList();

        Assert.True(undecided.Count == 0, "Is each of these a member's id or name (add its pattern to MemberData), or about something else (list it here)?\n  "
            + string.Join("\n  ", undecided));

        var notText = models.SelectMany(db => MemberData.NotText(db).Select(c => $"{db.GetType().Name}: {c}")).ToList();
        Assert.True(notText.Count == 0, "Named like a member's id but not text, so these tests neither fill nor expect it; teach MemberData about the type:\n  "
            + string.Join("\n  ", notText));
    }

    // The model only: nothing is connected to.
    private static TContext Model<TContext>() where TContext : DbContext => MemberData.Create(new DbContextOptionsBuilder<TContext>().UseNpgsql("Host=unused").Options);

    private async Task ErasedAsync<TContext>(Func<TContext, IIntegrationEventHandler<UserDeletedIntegrationEvent>> handler) where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql(await server.CreateDatabaseAsync()).Options;
        var (leaving, staying) = (Member("leaving"), Member("staying"));
        await using (var db = MemberData.Create(options))
        {
            await db.Database.MigrateAsync(Ct);
            var seeded = await MemberData.SeedAsync(db, leaving, Ct);
            Assert.True(seeded.Failures.Count == 0, "Rows this test couldn't make from the model alone; teach MemberData about them:\n  " + string.Join("\n  ", seeded.Failures));
            Assert.Empty((await MemberData.SeedAsync(db, staying, Ct)).Failures);
        }

        Dictionary<string, long> stayingBefore;
        await using (var db = MemberData.Create(options))
        {
            // Every table that names a member has the leaving member in it: otherwise the search below proves nothing of it.
            var before = await MemberData.MentionsAsync(db, leaving, Ct);
            var unseeded = MemberData.MemberTables(db).Where(table => !before.Keys.Any(k => k.StartsWith(table + ".", StringComparison.Ordinal))).ToList();
            Assert.True(unseeded.Count == 0, "Tables that name a member but got no row for one: " + string.Join(", ", unseeded));
            stayingBefore = await MemberData.MentionsAsync(db, staying, Ct);
        }

        await using (var db = MemberData.Create(options))
        {
            await handler(db).HandleAsync(new UserDeletedIntegrationEvent(leaving), Ct);
        }

        await using (var after = MemberData.Create(options))
        {
            var left = await MemberData.MentionsAsync(after, leaving, Ct);
            Assert.True(left.Count == 0, "Still there after the account was deleted: " + string.Join(", ", left.Select(m => $"{m.Key} ({m.Value})")));
            Assert.Equal(stayingBefore, await MemberData.MentionsAsync(after, staying, Ct));
        }
    }
}
