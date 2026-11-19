using Ratings.API.Services;

namespace Ratings.UnitTests;

/// <summary>
/// GIFs in comments: what counts as a GIPHY id, the content rating, and how a moderator reads a GIF comment.
/// </summary>
public class GifsTests
{
    [Theory]
    [InlineData("3o7TKSjRrfIPjeiVyM", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc/def", false)]
    [InlineData("abc?api_key=x", false)]
    [InlineData("abc,def", false)]
    [InlineData("https://evil.example/x.gif", false)]
    public void An_id_is_letters_and_digits_only(string? id, bool valid) => Assert.Equal(valid, Gifs.IsId(id));

    [Fact]
    public void An_id_longer_than_64_characters_is_refused() => Assert.False(Gifs.IsId(new string('a', 65)));

    [Theory]
    [InlineData(null, false)]
    [InlineData(" ", false)]
    [InlineData("key", true)]
    public void GIFs_are_on_only_with_a_key(string? key, bool on) => Assert.Equal(on, Gifs.Enabled(new GiphyOptions { ApiKey = key }));

    [Theory]
    [InlineData("pg", "pg")]
    [InlineData(" PG-13 ", "pg-13")]
    [InlineData("g", "g")]
    [InlineData("x", "pg")]
    [InlineData("", "pg")]
    public void A_rating_GIPHY_doesnt_know_falls_back_to_pg(string configured, string used) =>
        Assert.Equal(used, Gifs.RatingOf(new GiphyOptions { Rating = configured }));

    [Fact]
    public void A_moderator_reads_a_GIF_as_its_page_on_giphy()
    {
        Assert.Equal("Loved it", Gifs.WithGif("Loved it", null));
        Assert.Equal("Loved it [GIF: https://giphy.com/gifs/abc123]", Gifs.WithGif("Loved it", "abc123"));
        Assert.Equal("[GIF: https://giphy.com/gifs/abc123]", Gifs.WithGif("", "abc123"));
    }
}
