using Analytics.API.Services;
using SkiaSharp;

namespace Analytics.UnitTests;

/// <summary>
/// The Taste Match share card: a PNG of the right size in both shapes, with or without posters, whatever the names.
/// </summary>
public class MatchCardTests
{
    private static readonly MatchCardContent Content = new("Tess", "Robin", 82, "You'd fight over the remote for about four minutes.", [], null, "TV Tracker");

    [Theory]
    [InlineData(MatchCardSize.Story, 1080, 1920)]
    [InlineData(MatchCardSize.Link, 1200, 630)]
    public void A_card_is_a_png_of_its_shapes_size(MatchCardSize size, int width, int height)
    {
        var png = MatchCard.Render(Content, size);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4));
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
    }

    [Theory]
    [InlineData(MatchCardSize.Story)]
    [InlineData(MatchCardSize.Link)]
    public void Posters_are_drawn_and_one_that_isnt_a_picture_is_skipped(MatchCardSize size)
    {
        var plain = MatchCard.Render(Content, size);
        var withPosters = MatchCard.Render(Content with { Posters = [Poster(SKColors.Teal), [1, 2, 3], Poster(SKColors.Crimson)], PostersLabel = "You both love" }, size);

        Assert.NotEqual(plain, withPosters);
        // Somewhere on the card is the first poster's colour.
        using var bitmap = SKBitmap.Decode(withPosters);
        Assert.Contains(Enumerable.Range(0, bitmap.Width / 10).SelectMany(x => Enumerable.Range(0, bitmap.Height / 10).Select(y => bitmap.GetPixel(x * 10, y * 10))),
            pixel => pixel == SKColors.Teal);
    }

    [Fact]
    public void A_very_long_name_is_cut_short_to_fit_and_a_long_line_is_wrapped()
    {
        var typeface = MatchCard.Typeface("semibold");

        var fitted = MatchCard.Fit(new string('W', 80), typeface, 64, 900);
        var wrapped = MatchCard.Wrap("Not much overlap, which is where the good recommendations come from.", MatchCard.Typeface("regular"), 52, 880);

        Assert.EndsWith("…", fitted);
        Assert.True(fitted.Length < 80);
        Assert.Equal("Tess × Robin", MatchCard.Fit("Tess × Robin", typeface, 64, 900));
        Assert.True(wrapped.Count >= 2);
        Assert.Equal("Not much overlap, which is where the good recommendations come from.", string.Join(' ', wrapped));
    }

    [Theory]
    [InlineData("Robin", "Robin")]
    [InlineData("Zoë", "Zoë")]
    [InlineData("美咲", "A friend")]
    [InlineData("Sam🎬", "A friend")]
    [InlineData("", "A friend")]
    [InlineData(null, "A friend")]
    public void A_name_the_typeface_cannot_draw_is_not_drawn_as_boxes(string? name, string shown) =>
        Assert.Equal(shown, MatchCard.NameOr(name, "A friend"));

    [Fact]
    public void Cutting_a_name_short_never_splits_a_character()
    {
        var fitted = MatchCard.Fit(string.Concat(Enumerable.Repeat("👩‍👩‍👧", 30)), MatchCard.Typeface("semibold"), 64, 300);

        Assert.EndsWith("…", fitted);
        Assert.True(System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(fitted)) == fitted);
        Assert.DoesNotContain('\uFFFD', new string(fitted.Normalize().ToCharArray()));
        Assert.All(new System.Globalization.StringInfo(fitted[..^1]).String.EnumerateRunes(), _ => { });
    }

    /// <summary>
    /// A poster-shaped picture of one colour.
    /// </summary>
    private static byte[] Poster(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 300));
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Sample()
    {
        if (Environment.GetEnvironmentVariable("CARD_SAMPLES") is not { Length: > 0 } directory)
        {
            return;
        }
        var posters = Directory.GetFiles(directory, "poster*.jpg").Order().Select(File.ReadAllBytes).ToList();
        var content = Content with { Posters = posters, PostersLabel = "You both love" };
        File.WriteAllBytes(Path.Combine(directory, "card-story.png"), MatchCard.Render(content, MatchCardSize.Story));
        File.WriteAllBytes(Path.Combine(directory, "card-link.png"), MatchCard.Render(content, MatchCardSize.Link));
        File.WriteAllBytes(Path.Combine(directory, "card-link-plain.png"), MatchCard.Render(Content with { Score = 28, Line = "Two very different watchlists: twice as much to discover." }, MatchCardSize.Link));
    }
}
