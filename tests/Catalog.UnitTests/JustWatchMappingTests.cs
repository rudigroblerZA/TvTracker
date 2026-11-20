using System.Text.Json;
using Catalog.API.Apis;
using Catalog.Data;
using Catalog.Worker.JustWatch;

namespace Catalog.UnitTests;

/// <summary>
/// JustWatch's answers as where-to-watch rows. The JSON is the shape of the Partner API's own published examples.
/// </summary>
public class JustWatchMappingTests
{
    private static readonly Dictionary<long, string> Providers = new() { [8] = "Netflix", [9] = "Amazon Prime Video", [164] = "Hollystar" };

    private static JustWatchTitle Title(string offers) => JsonSerializer.Deserialize<JustWatchTitle>($$"""
        {
          "title": "Dark", "original_title": "Dark", "original_release_year": 2017, "full_path": "/us/tv-show/dark",
          "imdb_id": "tt5753856", "justwatch_id": 76331, "object_type": "show", "offers": {{offers}}
        }
        """)!;

    private static string Offer(string monetization, int provider, string target, string? raw = null, string quality = "hd") => $$"""
        { "monetization_type": "{{monetization}}", "presentation_type": "{{quality}}", "provider_id": {{provider}}, "currency": "USD", "retail_price": 4,
          "urls": { "standard_web": "https://click.justwatch.com/a?cx=abc&r={{Uri.EscapeDataString(target)}}&uct_country=us"{{(raw is null ? "" : $", \"raw_web\": \"{raw}\"")}} } }
        """;

    [Fact]
    public void Offers_become_one_row_for_each_service_and_way_of_watching()
    {
        var title = Title($"[{Offer("flatrate", 8, "https://www.netflix.com/title/80100172", quality: "hd")}, {Offer("flatrate", 8, "https://www.netflix.com/title/80100172", quality: "4k")}, "
            + $"{Offer("buy", 9, "https://www.amazon.com/gp/video/detail/B07XYZ")}, {Offer("rent", 9, "https://www.amazon.com/gp/video/detail/B07XYZ")}, {Offer("ads", 164, "https://www.hollystar.ch/x")}]");

        var options = JustWatchMapping.Options(title, Providers);

        // A service the catalog knows goes by the catalog's name for it; another by JustWatch's.
        Assert.Equal([("Netflix", WatchProviderType.Stream), ("Prime Video", WatchProviderType.Buy), ("Prime Video", WatchProviderType.Rent), ("Hollystar", WatchProviderType.Free)],
            options.Select(o => (o.ProviderName, o.Type)));
        // The link is JustWatch's attributed one, as given.
        Assert.All(options, o => Assert.StartsWith("https://click.justwatch.com/a?cx=abc&r=", o.Url));
    }

    [Fact]
    public void An_offer_without_a_usable_link_a_known_way_of_watching_or_a_known_service_is_left_out()
    {
        var title = Title($$"""
            [ { "monetization_type": "flatrate", "provider_id": 8, "urls": { "standard_web": "javascript:alert(1)" } },
              { "monetization_type": "flatrate", "provider_id": 8, "urls": {} },
              { "monetization_type": "cinema", "provider_id": 8, "urls": { "standard_web": "https://click.justwatch.com/a?r=x" } },
              { "monetization_type": "flatrate", "provider_id": 777, "urls": { "standard_web": "https://click.justwatch.com/a?r=https%3A%2F%2Fexample.org%2F" } } ]
            """);

        Assert.Empty(JustWatchMapping.Options(title, Providers));
        Assert.Empty(JustWatchMapping.Options(Title("null"), Providers));
    }

    [Fact]
    public void The_services_own_address_names_the_service_when_given()
    {
        var title = Title($"[{Offer("flatrate", 164, "https://example.org/", raw: "https://tv.apple.com/show/dark/umc.cmc.x")}]");

        Assert.Equal("Apple TV", Assert.Single(JustWatchMapping.Options(title, Providers)).ProviderName);
    }

    [Theory]
    [InlineData("GB", "en_GB")]
    [InlineData("CA", "en_CA")]
    [InlineData("DE", "de_DE")]
    [InlineData("ZA", null)]
    [InlineData("FR", null)]
    public void A_country_is_asked_in_its_english_locale_when_it_has_one(string region, string? locale)
    {
        JustWatchCountry[] countries = [new("en_US", "active"), new("fr_CA", "active"), new("en_CA", "active"), new("de_DE", "active"), new("en_GB", "active"), new("fr_FR", "inactive")];

        Assert.Equal(locale, JustWatchMapping.LocaleFor(region, countries));
    }

    [Theory]
    [InlineData("Dark", 2017, true)]
    [InlineData("DARK!", 2018, true)]
    [InlineData("Dark", 2020, false)]
    [InlineData("Dark Matter", 2017, false)]
    public void A_title_found_by_name_is_taken_only_when_it_is_the_show(string title, int year, bool same) =>
        Assert.Equal(same, JustWatchMapping.IsSameShow(Title("[]"), title, year));

    [Theory]
    [InlineData("/us/tv-show/dark", "/us/tv-show/dark")]
    [InlineData("//evil.example/x", null)]
    [InlineData("https://evil.example/x", null)]
    [InlineData("", null)]
    public void The_credit_links_only_to_a_page_on_justwatch(string path, string? kept) =>
        Assert.Equal(kept, JustWatchMapping.PathOf(Title("[]") with { FullPath = path }));

    [Fact]
    public void A_JustWatch_row_is_served_with_its_link_as_given()
    {
        var row = new WatchProvider { Region = "US", ProviderName = "Netflix", Type = WatchProviderType.Stream, Url = "https://click.justwatch.com/a?r=x", Source = WatchSource.JustWatch };

        Assert.Equal(new WatchProviderDto("Netflix", WatchProviderType.Stream, "https://click.justwatch.com/a?r=x", WatchLink.Title), CatalogApi.WatchLinkFor(row, "Dark"));
    }
}
