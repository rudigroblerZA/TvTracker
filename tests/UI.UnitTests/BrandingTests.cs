using TvTracker.UI.Models;
using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// What the pages show for the app's name and the company: Settings' values over the ones the app was started with.
/// </summary>
public class BrandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_nothing_in_Settings_the_configured_values_show()
    {
        var brand = new Branding("Couch Club", "Example Media", "hello@example.org", governingLaw: "South Africa");

        Assert.Equal(("Couch Club", "Example Media", "hello@example.org", "South Africa"),
            (brand.AppName, brand.Operator, brand.ContactEmail, brand.GoverningLaw));
        Assert.Null(brand.AccentColor);
        Assert.Null(brand.LoadedAt);
    }

    [Fact]
    public void Settings_win_field_by_field_and_an_empty_one_keeps_the_configured_value()
    {
        var brand = new Branding("Couch Club", "Example Media", "hello@example.org");
        var changes = 0;
        brand.Changed += () => changes++;

        brand.Apply(new BrandingSettings(AppName: "Sofa Society", AccentColor: "#0f766e", ContactEmail: "  "), Now);

        Assert.Equal(("Sofa Society", "#0f766e", "Example Media", "hello@example.org"),
            (brand.AppName, brand.AccentColor, brand.Operator, brand.ContactEmail));
        Assert.Equal(Now, brand.LoadedAt);
        Assert.Equal(1, changes);

        // The same values again change nothing, so nothing is told to redraw.
        brand.Apply(new BrandingSettings(AppName: "Sofa Society", AccentColor: "#0f766e", ContactEmail: "  "), Now.AddMinutes(1));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Clearing_Settings_goes_back_to_what_the_app_was_started_with()
    {
        var brand = new Branding("Couch Club");
        brand.Apply(new BrandingSettings(AppName: "Sofa Society", Operator: "Example Media"), Now);

        brand.Apply(null, Now.AddMinutes(2));

        // With no operator anywhere, the app's own name stands in.
        Assert.Equal(("Couch Club", "Couch Club"), (brand.AppName, brand.Operator));
    }
}
