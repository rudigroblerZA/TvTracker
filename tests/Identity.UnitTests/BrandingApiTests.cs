using Identity.API.Apis;

namespace Identity.UnitTests;

/// <summary>
/// The app's name, colour and company details an admin saves in Settings: tidied, and checked before they're shown
/// to everyone.
/// </summary>
public class BrandingApiTests
{
    private static BrandingDto With(string? appName = null, string? accent = null, string? op = null, string? email = null,
        string? address = null, string? number = null, string? law = null) => new(appName, accent, op, email, address, number, law);

    [Fact]
    public void Empty_fields_become_none_and_the_rest_are_trimmed()
    {
        var cleaned = BrandingApi.Clean(With(appName: "  Couch Club ", accent: " #2563EB ", op: "   ", email: ""));

        Assert.Equal(With(appName: "Couch Club", accent: "#2563eb"), cleaned);
        Assert.Null(BrandingApi.Problem(cleaned));
    }

    [Fact]
    public void Nothing_set_is_fine_everything_falls_back_to_configuration() =>
        Assert.Null(BrandingApi.Problem(With()));

    [Theory]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#12345g")]
    [InlineData("2563eb")]
    public void An_accent_colour_must_be_six_hex_digits(string colour) =>
        Assert.Contains("accent colour", BrandingApi.Problem(BrandingApi.Clean(With(accent: colour))));

    [Theory]
    [InlineData("not an address")]
    [InlineData("a@b")]
    [InlineData("two words@example.org")]
    public void A_contact_address_must_look_like_an_email_address(string email) =>
        Assert.Contains("email address", BrandingApi.Problem(BrandingApi.Clean(With(email: email))));

    [Fact]
    public void Names_and_company_details_have_limits_and_no_line_breaks()
    {
        Assert.Contains("40 characters", BrandingApi.Problem(With(appName: new string('a', 41))));
        Assert.Contains("120 characters", BrandingApi.Problem(With(op: new string('a', 121))));
        Assert.Contains("Line breaks", BrandingApi.Problem(With(address: "1 Long Street\nCape Town")));
        Assert.Null(BrandingApi.Problem(With(appName: "Couch Club", accent: "#0f766e", op: "Example Media (Pty) Ltd", email: "privacy@example.org",
            address: "1 Long Street, Cape Town, 8001, South Africa", number: "registered in South Africa, no. 2026/123456/07", law: "South Africa")));
    }
}
