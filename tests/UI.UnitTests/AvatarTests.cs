using TvTracker.UI.Components;

namespace UI.UnitTests;

public class AvatarTests
{
    [Theory]
    // An uploaded picture's path, under wherever the host serves them: the web app itself, or the phone's gateway.
    [InlineData("avatars/u1?v=7", "", "avatars/u1?v=7")]
    [InlineData("avatars/u1?v=7", "https://tv.example.com/api/identity/api/v1/", "https://tv.example.com/api/identity/api/v1/avatars/u1?v=7")]
    // A picture elsewhere, as it is.
    [InlineData("https://example.com/a.png", "", "https://example.com/a.png")]
    [InlineData("HTTPS://example.com/a.png", "https://x/", "HTTPS://example.com/a.png")]
    public void A_picture_is_loaded_from_where_it_lives(string avatar, string avatarBase, string address) =>
        Assert.Equal(address, Avatar.Address(avatar, avatarBase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Never over plain http, and never anything that isn't a picture's address at all.
    [InlineData("http://example.com/a.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/avatars/u1")]
    [InlineData("//example.com/a.png")]
    public void Nothing_else_is_loaded(string? avatar) =>
        Assert.Null(Avatar.Address(avatar, "https://tv.example.com/"));

    [Theory]
    [InlineData("ann", "A")]
    [InlineData("Émile", "É")]
    [InlineData("", "?")]
    public void Without_a_picture_the_initial_shows(string name, string initial) =>
        Assert.Equal(initial, Avatar.Initial(name));
}
