using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TvTracker.Tests;

namespace Privacy.IntegrationTests;

/// <summary>
/// A member's data export has what each service holds about them. Every table that names a member is filled for one
/// (as <see cref="ErasureTests"/> does), the service's export is asked for as that member, and each table's row must be
/// found in it, unless the table is listed here as one the export leaves out, with why. So a new member table fails
/// here until its service exports it or someone decides it needn't.
/// <para>
/// Identity.API, which gathers these and adds the profile, isn't here: it asks each service over HTTP.
/// </para>
/// </summary>
public sealed class ExportTests(PostgresServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task Tracking_exports_what_it_holds() => ExportedAsync<Tracking.Data.TrackingContext>(
        (user, db) => Tracking.API.Apis.ExportApi.ExportAsync(user, db, Ct), new() { ["user_plans"] = "A copy of their plan, which Identity keeps and puts in the export itself." });

    [Fact]
    public Task Ratings_exports_what_it_holds() => ExportedAsync<Ratings.Data.RatingsContext>(
        (user, db) => Ratings.API.Apis.ExportApi.ExportAsync(user, db, Ct), new());

    [Fact]
    public Task Social_exports_what_it_holds() => ExportedAsync<Social.Data.SocialContext>(
        async (user, db) => await Social.API.Apis.SocialApi.ExportAsync(user, db, Ct), new()
        {
            ["user_plans"] = "A copy of their plan, which Identity keeps and puts in the export itself.",
            ["profiles"] = "A copy of their name and picture, which Identity keeps and exports.",
            ["shared_lists"] = "A copy of the lists they share, which Tracking keeps and exports.",
            ["user_shows"] = "A copy of what they follow and rated, which Tracking and Ratings keep and export.",
            ["user_watches"] = "A copy of what they watched, which Tracking keeps and exports."
        });

    [Fact]
    public Task Recommendations_exports_what_it_holds() => ExportedAsync<Recommendations.Data.RecommendationsContext>(
        (user, db) => Recommendations.API.Apis.RecommendationsApi.ExportAsync(user, db, Ct), new()
        {
            ["user_plans"] = "A copy of their plan, which Identity keeps and puts in the export itself.",
            ["user_show_signals"] = "A copy of what they follow, watched and rated, which Tracking and Ratings keep and export.",
            ["dirty_users"] = "A note that their recommendations need working out again; it says nothing else of them."
        });

    [Fact]
    public Task Notifications_exports_what_it_holds() => ExportedAsync<Notifications.Data.NotificationsContext>(
        (user, db) => Notifications.API.Apis.NotificationsApi.ExportAsync(user, db, Ct), new()
        {
            ["followers"] = "A copy of what they follow, which Tracking keeps and exports.",
            ["deliveries"] = "How each notification was sent (channel, attempts); the notifications themselves are exported."
        });

    [Fact]
    public Task Research_exports_what_it_holds() => ExportedAsync<Research.Data.ResearchContext>(
        async (user, db) => await Research.API.Apis.ExportApi.ExportAsync(user, db, Ct), new());

    [Fact]
    public Task Analytics_exports_what_it_holds() => ExportedAsync<Analytics.Data.AnalyticsContext>(
        async (user, db) => await Analytics.API.Apis.ExportApi.GetAsync(user, db, Ct), new()
        {
            ["user_plans"] = "A copy of their plan, which Identity keeps and puts in the export itself.",
            ["user_follows"] = "A copy of what they follow, which Tracking keeps and exports.",
            ["user_episode_watches"] = "A copy of what they watched, which Tracking keeps and exports.",
            ["user_show_ratings"] = "A copy of their show ratings, which Ratings keeps and exports.",
            ["user_episode_ratings"] = "A copy of their episode ratings, which Ratings keeps and exports."
        });

    [Fact]
    public Task Financials_exports_what_it_holds() => ExportedAsync<Financials.Data.FinancialsContext>(
        async (user, db) => await Financials.API.Apis.PaymentsApi.ExportAsync(user, db,
            new Financials.API.Payments.PaymentProviders([], Options.Create(new Financials.API.Payments.PaymentOptions())), Ct), new()
        {
            ["payment_settings"] = "The deployment's payment settings, with which admin saved them last: the business's record, not the member's."
        });

    /// <param name="leftOut">Tables that name a member and aren't in the export, each with why.</param>
    private async Task ExportedAsync<TContext>(Func<ClaimsPrincipal, TContext, Task<IResult>> export, Dictionary<string, string> leftOut)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql(await server.CreateDatabaseAsync()).Options;
        var member = $"member-{Guid.NewGuid():N}"[..20];
        await using (var db = Create(options))
        {
            await db.Database.MigrateAsync(Ct);
            var seeded = await MemberData.SeedAsync(db, member, Ct);
            Assert.True(seeded.Failures.Count == 0, "Rows this test couldn't make from the model alone; teach MemberData about them:\n  " + string.Join("\n  ", seeded.Failures));
        }

        await using var read = Create(options);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", member)], "test"));
        var json = JsonSerializer.Serialize(Value(await export(user, read)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var held = await MemberData.DistinctiveAsync(read, member, Ct);

        var exported = held.Where(t => t.Value.Any(v => json.Contains(v, StringComparison.OrdinalIgnoreCase))).Select(t => t.Key).ToHashSet();
        var missing = held.Keys.Where(t => !exported.Contains(t) && !leftOut.ContainsKey(t)).Order().ToList();
        Assert.True(missing.Count == 0, "Tables that hold something about a member that their export doesn't have. Export them, or list them as left out with why: "
            + string.Join(", ", missing));
        var stale = leftOut.Keys.Where(t => exported.Contains(t) || !held.ContainsKey(t)).Order().ToList();
        Assert.True(stale.Count == 0, "Listed as left out of the export, but exported or no longer a member's table: " + string.Join(", ", stale));
    }

    /// <summary>
    /// What an endpoint's result carries, whichever way it was written (<c>Results.Ok(x)</c>, <c>Ok&lt;T&gt;</c>, or one of several).
    /// </summary>
    private static object? Value(IResult result) => result switch
    {
        INestedHttpResult nested => Value(nested.Result),
        IValueHttpResult value => value.Value,
        _ => throw new InvalidOperationException($"The export answered {result.GetType().Name}, which carries nothing.")
    };

    private static TContext Create<TContext>(DbContextOptions<TContext> options) where TContext : DbContext =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;
}
