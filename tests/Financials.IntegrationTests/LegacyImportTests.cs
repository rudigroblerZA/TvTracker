using Catalog.Data;
using Financials.API.Services;
using Financials.Data;
using Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TvTracker.Tests;

namespace Financials.IntegrationTests;

/// <summary>
/// What payments and affiliates kept while they were Identity's and Catalog's comes across, once: the old databases
/// are built by their own migrations, so the tables are as an upgraded deployment has them.
/// </summary>
public sealed class LegacyImportTests(PostgresServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rows_kept_before_the_move_are_copied_once()
    {
        var identity = await server.CreateDatabaseAsync();
        var catalog = await server.CreateDatabaseAsync();
        var financials = await server.CreateDatabaseAsync();
        await using (var old = new IdentityContext(new DbContextOptionsBuilder<IdentityContext>().UseNpgsql(identity).Options))
        {
            await old.Database.MigrateAsync(Ct);
            await old.Database.ExecuteSqlRawAsync("""
                insert into checkout_sessions ("Id", "UserId", "Provider", "Plan", "Interval", "Amount", "Currency", "CreatedAtUtc", "CompletedAtUtc")
                values ('11111111-1111-1111-1111-111111111111', 'user-1', 'stripe', 'plus', 'month', 4.99, 'EUR', now(), now());
                insert into subscriptions ("Id", "UserId", "Provider", "ProviderSubscriptionId", "Plan", "Interval", "Amount", "Currency", "Status",
                    "CurrentPeriodEndUtc", "CancelAtPeriodEnd", "CreatedAtUtc", "UpdatedAtUtc")
                values ('22222222-2222-2222-2222-222222222222', 'user-1', 'stripe', 'sub_1', 'plus', 'month', 4.99, 'EUR', 0, now() + interval '20 days', false, now(), now());
                insert into payment_receipts ("Id", "UserId", "SubscriptionId", "Provider", "ProviderPaymentId", "Plan", "Interval", "Amount", "Currency",
                    "PaidAtUtc", "PeriodEndUtc")
                values ('33333333-3333-3333-3333-333333333333', 'user-1', '22222222-2222-2222-2222-222222222222', 'stripe', 'in_1', 'plus', 'month', 4.99, 'EUR',
                    now(), now() + interval '20 days');
                insert into processed_payment_events ("Provider", "EventId", "ProcessedAtUtc") values ('stripe', 'evt_1', now());
                """, Ct);
        }
        await using (var old = new CatalogContext(new DbContextOptionsBuilder<CatalogContext>().UseNpgsql(catalog, o => o.UseVector()).Options))
        {
            await old.Database.MigrateAsync(Ct);
            await old.Database.ExecuteSqlRawAsync("""
                insert into affiliate_programs ("Name", "Services", "Regions", "Template", "PostbackKey", "Currency", "Test", "Enabled", "CreatedAtUtc", "UpdatedAtUtc")
                values ('netflix-us', '{{Netflix}}', '{{US}}', 'https://net.example/?u={{url}}&c={{click}}', 'a-postback-key-of-16', 'USD', false, true, now(), now());
                insert into outbound_clicks ("Id", "ShowId", "Service", "Type", "Link", "Region", "Surface", "Plan", "Program", "ClickedAtUtc")
                values ('44444444-4444-4444-4444-444444444444', '55555555-5555-5555-5555-555555555555', 'Netflix', 0, 2, 'US', 'where-to-watch', 'plus', 'netflix-us', now());
                insert into affiliate_conversions ("Id", "ClickId", "Program", "TransactionId", "Amount", "Currency", "ReceivedAtUtc")
                values ('66666666-6666-6666-6666-666666666666', '44444444-4444-4444-4444-444444444444', 'netflix-us', 'tx-1', 4.50, 'USD', now());
                """, Ct);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:identitydb"] = identity, ["ConnectionStrings:catalogdb"] = catalog
        }).Build();
        await using var db = new FinancialsContext(new DbContextOptionsBuilder<FinancialsContext>().UseNpgsql(financials).Options);
        await db.Database.MigrateAsync(Ct);

        await LegacyImport.RunAsync(db, configuration, NullLogger.Instance, Ct);
        // A second start finds its tables filled and copies nothing.
        await LegacyImport.RunAsync(db, configuration, NullLogger.Instance, Ct);

        var subscription = await db.Subscriptions.SingleAsync(Ct);
        Assert.Equal(("user-1", "sub_1", SubscriptionStatus.Active, 4.99m, "EUR"),
            (subscription.UserId, subscription.ProviderSubscriptionId, subscription.Status, subscription.Amount, subscription.Currency));
        Assert.Equal("in_1", (await db.Receipts.SingleAsync(Ct)).ProviderPaymentId);
        Assert.NotNull((await db.CheckoutSessions.SingleAsync(Ct)).CompletedAtUtc);
        Assert.Equal("evt_1", (await db.ProcessedPaymentEvents.SingleAsync(Ct)).EventId);

        var program = await db.AffiliatePrograms.SingleAsync(Ct);
        Assert.Equal(("netflix-us", "Netflix", "US"), (program.Name, Assert.Single(program.Services), Assert.Single(program.Regions)));
        var click = await db.OutboundClicks.SingleAsync(Ct);
        // The listing's type and link by name, and a title even for a show Catalog no longer has.
        Assert.Equal(("Stream", "Title", "(removed show)", "netflix-us"), (click.Type, click.Link, click.ShowTitle, click.Program));
        Assert.Equal((click.Id, 4.50m), await db.AffiliateConversions.Select(c => new ValueTuple<Guid, decimal>(c.ClickId, c.Amount)).SingleAsync(Ct));
    }

    [Fact]
    public async Task A_deployment_that_began_here_has_nothing_to_copy()
    {
        var empty = await server.CreateDatabaseAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:identitydb"] = empty, ["ConnectionStrings:catalogdb"] = empty
        }).Build();
        await using var db = new FinancialsContext(new DbContextOptionsBuilder<FinancialsContext>().UseNpgsql(await server.CreateDatabaseAsync()).Options);
        await db.Database.MigrateAsync(Ct);

        await LegacyImport.RunAsync(db, configuration, NullLogger.Instance, Ct);

        Assert.False(await db.Subscriptions.AnyAsync(Ct));
        Assert.False(await db.AffiliatePrograms.AnyAsync(Ct));
    }

    [Fact]
    public async Task An_old_database_that_is_gone_has_nothing_to_copy()
    {
        var gone = new Npgsql.NpgsqlConnectionStringBuilder(await server.CreateDatabaseAsync()) { Database = "dropped_since" }.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:identitydb"] = gone, ["ConnectionStrings:catalogdb"] = gone
        }).Build();
        await using var db = new FinancialsContext(new DbContextOptionsBuilder<FinancialsContext>().UseNpgsql(await server.CreateDatabaseAsync()).Options);
        await db.Database.MigrateAsync(Ct);

        await LegacyImport.RunAsync(db, configuration, NullLogger.Instance, Ct);

        Assert.False(await db.Subscriptions.AnyAsync(Ct));
    }
}
