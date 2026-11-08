using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Ratings.API.Services;

namespace Ratings.UnitTests;

/// <summary>
/// The light moderation layer as CONTEXT.md states it: blocked terms are refused as whole words, a link holds a post for
/// review, and moderators are Admin:Users plus Moderation:Users.
/// </summary>
public class ModerationTests
{
    private static Moderation With(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build());

    private static readonly Moderation Blocking = With(("Moderation:BlockedTerms:0", "slur"), ("Moderation:BlockedTerms:1", "buy now"));

    [Theory]
    [InlineData("What a SLUR to use")]
    [InlineData("slur.")]
    [InlineData("Buy now and save")]
    public void A_blocked_term_is_refused_in_any_case(string text) => Assert.Equal(TextVerdict.Rejected, Blocking.Check(text));

    [Theory]
    [InlineData("Slurred speech in the finale")]   // whole words only
    [InlineData("I'd buy nowhere near this")]
    public void A_blocked_term_inside_another_word_is_fine(string text) => Assert.Equal(TextVerdict.Ok, Blocking.Check(text));

    [Theory]
    [InlineData("Full recap at https://example.com/recap")]
    [InlineData("see www.example.com")]
    [InlineData("HTTP://EXAMPLE.COM")]
    public void A_link_holds_the_post_for_review(string text) => Assert.Equal(TextVerdict.Held, Blocking.Check(text));

    [Fact]
    public void A_blocked_term_wins_over_a_link() => Assert.Equal(TextVerdict.Rejected, Blocking.Check("slur at https://example.com"));

    [Fact]
    public void Plain_text_and_no_configured_terms_are_fine()
    {
        Assert.Equal(TextVerdict.Ok, With().Check("The finale stuck the landing. 10/10, www is a word here."));
        Assert.Equal(TextVerdict.Ok, Blocking.Check("A fair review of season 2."));
    }

    [Fact]
    public void Moderators_are_admins_and_named_moderators_by_username()
    {
        var moderation = With(("Admin:Users:0", "admin"), ("Moderation:Users:0", "Rudi"));
        static ClaimsPrincipal User(string name) => new(new ClaimsIdentity([new Claim("preferred_username", name)], "test"));

        Assert.True(moderation.IsModerator(User("admin")));
        Assert.True(moderation.IsModerator(User("rudi")));      // usernames compare without case
        Assert.False(moderation.IsModerator(User("demo")));
        Assert.False(moderation.IsModerator(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
