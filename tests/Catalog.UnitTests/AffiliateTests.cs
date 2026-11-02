using Catalog.API.Apis;
using Catalog.API.Services;

namespace Catalog.UnitTests;

/// <summary>
/// Affiliate links: which program covers a click, the link it becomes, and the report (see CONTEXT.md, Affiliate links).
/// </summary>
public class AffiliateTests
{
    private static readonly AffiliateProgram Netflix = new()
    {
        Name = "netflix-us", Services = ["Netflix"], Regions = ["US"],
        Template = "https://network.example/c/123?u={url}&subId1={click}", PostbackKey = "s3cret", Currency = "USD"
    };

    private static readonly AffiliateProgram Prime = new()
    {
        Name = "prime", Services = ["Prime Video", "Amazon Prime Video"], Template = "https://network.example/p?dest={url}&sid={click}", Currency = "GBP"
    };

    private static readonly AffiliateOptions Options = new() { Programs = [Netflix, Prime] };

    [Theory]
    [InlineData("Netflix", "US", "netflix-us")]
    [InlineData("netflix", "us", "netflix-us")]
    [InlineData("Netflix", "GB", null)]
    [InlineData("Prime Video", "DE", "prime")]
    [InlineData("Max", "US", null)]
    public void The_program_covering_a_service_and_region(string service, string region, string? program) =>
        Assert.Equal(program, AffiliateLinks.For(Options, service, region)?.Name);

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
        Assert.Null(AffiliateLinks.For(new AffiliateOptions { Programs = [program] }, "Netflix", "US"));
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
}
