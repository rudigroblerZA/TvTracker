using Catalog.API.Apis;
using Catalog.API.Services;
using Catalog.Data;
using Microsoft.Extensions.Configuration;

namespace Catalog.UnitTests;

/// <summary>
/// Affiliate links: which program covers a click, the link it becomes, and the report.
/// </summary>
public class AffiliateTests
{
    private static readonly AffiliateProgram Netflix = new()
    {
        Name = "netflix-us",
        Services = ["Netflix"],
        Regions = ["US"],
        Template = "https://network.example/c/123?u={url}&subId1={click}",
        PostbackKey = "s3cret",
        Currency = "USD"
    };

    private static readonly AffiliateProgram Prime = new()
    {
        Name = "prime",
        Services = ["Prime Video", "Amazon Prime Video"],
        Template = "https://network.example/p?dest={url}&sid={click}",
        Currency = "GBP"
    };

    private static readonly AffiliateOptions Options = new() { Programs = [Netflix, Prime] };

    [Theory]
    [InlineData("Netflix", "US", "netflix-us")]
    [InlineData("netflix", "us", "netflix-us")]
    [InlineData("Netflix", "GB", null)]
    [InlineData("Prime Video", "DE", "prime")]
    [InlineData("Max", "US", null)]
    public void The_program_covering_a_service_and_region(string service, string region, string? program) =>
        Assert.Equal(program, AffiliateLinks.For(Options.Programs, service, region)?.Name);

    [Fact]
    public void The_link_carries_the_escaped_address_and_the_click_id()
    {
        var click = Guid.Parse("0193f2a4-1111-7222-8333-444455556666");

        var link = AffiliateLinks.Wrap(Netflix, "https://www.netflix.com/title/80057281?x=1&y=2", click);

        Assert.Equal("https://network.example/c/123?u=https%3A%2F%2Fwww.netflix.com%2Ftitle%2F80057281%3Fx%3D1%26y%3D2&subId1=0193f2a4111172228333444455556666", link);
    }

    [Theory]
    [InlineData("http://network.example/c?u={url}")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative?u={url}")]
    [InlineData("")]
    public void A_template_that_isnt_an_https_address_is_never_used(string template)
    {
        var program = new AffiliateProgram { Name = "bad", Services = ["Netflix"], Template = template };

        Assert.False(AffiliateLinks.IsValid(program));
        Assert.Null(AffiliateLinks.For([program], "Netflix", "US"));
    }

    [Theory]
    [InlineData("http://localhost:5120/dev/affiliate-network?u={url}&click={click}", true)]
    [InlineData("http://127.0.0.1:5120/n?u={url}", true)]
    [InlineData("http://network.example/n?u={url}", false)]
    public void Http_is_only_for_this_machine(string template, bool valid) =>
        Assert.Equal(valid, AffiliateLinks.IsValid(new AffiliateProgram { Name = "t", Services = [AffiliateLinks.AnyService], Template = template }));

    [Fact]
    public void The_test_network_covers_every_service_after_real_programs()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Affiliates:TestNetwork:Url"] = "http://localhost:5120/",
            ["Affiliates:TestNetwork:Key"] = "k"
        }).Build();
        var test = AffiliateLinks.TestNetwork(config)!;
        var options = new AffiliateOptions { Programs = [Netflix, test] };

        Assert.Equal("netflix-us", AffiliateLinks.For(options.Programs, "Netflix", "US")?.Name);
        Assert.Equal(AffiliateLinks.TestNetworkName, AffiliateLinks.For(options.Programs, "Max", "GB")?.Name);
        Assert.Equal("http://localhost:5120/dev/affiliate-network?u=https%3A%2F%2Fmax.com%2F&click=" + Guid.Empty.ToString("N") + "&program=test-network",
            AffiliateLinks.Wrap(test, "https://max.com/", Guid.Empty));
        Assert.Null(AffiliateLinks.TestNetwork(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void A_program_needs_a_name_services_and_a_currency()
    {
        Assert.False(AffiliateLinks.IsValid(new AffiliateProgram { Services = ["Netflix"], Template = Netflix.Template }));
        Assert.False(AffiliateLinks.IsValid(new AffiliateProgram { Name = "x", Template = Netflix.Template }));
        Assert.False(AffiliateLinks.IsValid(new AffiliateProgram { Name = "x", Services = ["Netflix"], Template = Netflix.Template, Currency = "Dollars" }));
        Assert.True(AffiliateLinks.IsValid(Netflix));
    }

    [Theory]
    [InlineData("s3cret", true)]
    [InlineData("S3CRET", false)]
    [InlineData("s3cre", false)]
    [InlineData(null, false)]
    public void Postback_keys(string? key, bool matches) => Assert.Equal(matches, AffiliateLinks.KeyMatches(Netflix, key));

    [Fact]
    public void A_program_without_a_key_takes_no_postbacks() => Assert.False(AffiliateLinks.KeyMatches(Prime, ""));

    [Fact]
    public void The_report_counts_clicks_sales_and_commission_per_service()
    {
        var from = new DateOnly(2026, 11, 1);
        var to = new DateOnly(2026, 11, 3);
        var (lasso, bad) = (Guid.NewGuid(), Guid.NewGuid());
        var clicks = new List<ClickGroup>
        {
            new(from, lasso, "Netflix", "netflix-us", "watch-now", "free", 30),
            new(from.AddDays(2), bad, "Netflix", "netflix-us", "where-to-watch", "signed-out", 10),
            new(from, bad, "Max", null, "where-to-watch", "plus", 25)
        };
        var conversions = new List<ConversionRow>
        {
            new(from.AddDays(1), lasso, "Netflix", "watch-now", "free", "netflix-us", 4.50m, "USD"),
            new(from.AddDays(2), bad, "Netflix", "where-to-watch", "signed-out", "netflix-us", 3.50m, "USD")
        };

        var report = AffiliateReport.Build(from, to, clicks, conversions, new Dictionary<Guid, string> { [lasso] = "Ted Lasso" }, [Netflix]);

        Assert.Equal((65, 40, 2), (report.Clicks, report.AffiliatedClicks, report.Conversions));
        Assert.Equal([new MoneyDto("USD", 8.00m)], report.Revenue);

        var netflix = report.Services[0];
        Assert.Equal(("Netflix", "netflix-us", 40, 2, 8.00m, "USD"), (netflix.Service, netflix.Program, netflix.Clicks, netflix.Conversions, netflix.Revenue, netflix.Currency));
        Assert.Equal(0.2m, netflix.EarningsPerClick);
        Assert.Equal(0.05, netflix.ConversionRate);

        // A service with no program is listed with its clicks, and nothing per click.
        var max = report.Services[1];
        Assert.Equal(("Max", null, 25, 0), (max.Service, max.Program, max.Clicks, max.Conversions));
        Assert.Null(max.EarningsPerClick);

        Assert.Equal(new AffiliateDayDto[] { new(from, 55, 0), new(from.AddDays(1), 0, 1), new(to, 10, 1) }, report.Days);
        Assert.Equal(new AffiliateBreakdownDto[] { new("where-to-watch", 35, 1), new("watch-now", 30, 1) }, report.Surfaces);
        // Most clicks first: the other show's 35 across two services, then Ted Lasso's 30.
        Assert.Equal([(bad, 35, 1), (lasso, 30, 1)], report.Shows.Select(s => (s.ShowId, s.Clicks, s.Conversions)));
        Assert.Equal("Ted Lasso", report.Shows[1].Title);
        Assert.Equal("(removed show)", report.Shows.Single(s => s.ShowId == bad).Title);
        var program = Assert.Single(report.Programs);
        Assert.Equal(("netflix-us", "USD", true), (program.Name, program.Currency, program.Valid));
        Assert.Equal(["Netflix"], program.Services);
    }

    [Fact]
    public void An_empty_window_has_a_row_per_day_and_nothing_else()
    {
        var report = AffiliateReport.Build(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7), [], [], new Dictionary<Guid, string>(), []);

        Assert.Equal(7, report.Days.Count);
        Assert.Empty(report.Services);
        Assert.Empty(report.Revenue);
    }

    private static IConfiguration WithTestNetwork() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Affiliates:TestNetwork:Url"] = "http://localhost:5120", ["Affiliates:TestNetwork:Key"] = "test-network-key"
    }).Build();

    private static AffiliateProgramRecord Record(string name, string service, bool test = false, bool enabled = true) => new()
    {
        Name = name, Services = [service], Currency = "USD", Test = test, Enabled = enabled,
        Template = test ? "" : "https://network.example/c/9?u={url}&s={click}", PostbackKey = test ? null : "a-long-random-secret"
    };

    [Fact]
    public void Programs_from_Settings_come_first_then_configurations_then_the_test_networks_catch_all()
    {
        var test = AffiliateLinks.TestNetwork(WithTestNetwork());

        var merged = AffiliatePrograms.Merge([Record("hulu-live", "Hulu")], [Netflix, Prime], test);

        Assert.Equal(["hulu-live", "netflix-us", "prime", AffiliateLinks.TestNetworkName], merged.Select(p => p.Name));
        Assert.Equal([AffiliateSources.Settings, AffiliateSources.Configuration, AffiliateSources.Configuration, AffiliateSources.TestNetwork],
            merged.Select(p => p.Source));
    }

    [Fact]
    public void A_program_in_Settings_replaces_the_configurations_of_that_name_even_when_switched_off()
    {
        var merged = AffiliatePrograms.Merge([Record("netflix-us", "Netflix", enabled: false)], [Netflix, Prime], null);

        // Switched off in Settings: not used, and the configuration's doesn't come back in its place.
        Assert.Equal(["prime"], merged.Select(p => p.Name));
    }

    [Fact]
    public void A_test_program_takes_the_test_networks_link_and_key_and_is_unusable_without_one()
    {
        var withNetwork = AffiliatePrograms.Merge([Record("netflix-test", "Netflix", test: true)], [], AffiliateLinks.TestNetwork(WithTestNetwork()))[0];
        Assert.True(AffiliateLinks.IsValid(withNetwork));
        Assert.True(AffiliateLinks.KeyMatches(withNetwork, "test-network-key"));
        var click = Guid.Parse("11111111-2222-3333-4444-555555555555");
        // The page is told which program the click belongs to, so a sale reported there counts against it.
        Assert.Equal("http://localhost:5120/dev/affiliate-network?u=https%3A%2F%2Fwww.netflix.com%2Ftitle%2F1&click=11111111222233334444555555555555&program=netflix-test",
            AffiliateLinks.Wrap(withNetwork, "https://www.netflix.com/title/1", click));

        var without = AffiliatePrograms.Merge([Record("netflix-test", "Netflix", test: true)], [], null)[0];
        Assert.False(AffiliateLinks.IsValid(without));
        Assert.Null(AffiliateLinks.For([without], "Netflix", "US"));
    }

    [Fact]
    public void Starter_programs_are_a_test_program_per_main_service_added_once()
    {
        var now = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

        var all = StarterPrograms.Missing([], now);
        Assert.Contains(all, p => p is { Name: "netflix-test", Test: true, Enabled: true } && p.Services.SequenceEqual(["Netflix"]));
        Assert.Equal(StarterPrograms.All.Count, all.Count);

        // Not again by name, and not for a service that has a program of its own.
        var missing = StarterPrograms.Missing([Record("NETFLIX-TEST", "Other", test: true), Record("hulu-us", "Hulu")], now);
        Assert.DoesNotContain(missing, p => p.Name is "netflix-test" or "hulu-test");
        Assert.Equal(StarterPrograms.All.Count - 2, missing.Count);
    }

    [Fact]
    public void A_live_program_is_tried_before_a_test_one_whatever_their_names()
    {
        var test = AffiliateLinks.TestNetwork(WithTestNetwork());

        var merged = AffiliatePrograms.Merge(
            [Record("netflix-test", "Netflix", test: true), Record("all", "*"), Record("netflix-us", "Netflix")], [Prime], test);

        // Live from Settings (naming its service before every-service), configuration, then the stand-ins.
        Assert.Equal(["netflix-us", "all", "prime", "netflix-test", AffiliateLinks.TestNetworkName], merged.Select(p => p.Name));
        Assert.Equal("netflix-us", AffiliateLinks.For(merged, "Netflix", "US")?.Name);
    }

    [Fact]
    public void A_switched_off_program_is_not_linked_to_but_still_takes_its_sales()
    {
        IReadOnlyList<AffiliateProgramRecord> records = [Record("netflix-us", "Netflix", enabled: false)];

        Assert.Empty(AffiliatePrograms.Merge(records, [], null));
        var forPostbacks = Assert.Single(AffiliatePrograms.Merge(records, [], null, includeOff: true));
        Assert.True(AffiliateLinks.KeyMatches(forPostbacks, "a-long-random-secret"));
    }
}
