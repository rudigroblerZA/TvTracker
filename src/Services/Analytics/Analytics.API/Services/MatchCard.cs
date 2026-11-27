using SkiaSharp;

namespace Analytics.API.Services;

/// <summary>
/// The two shapes a Taste Match card comes in.
/// </summary>
public enum MatchCardSize
{
    /// <summary>
    /// 1080×1920: a phone's full screen, for stories.
    /// </summary>
    Story,

    /// <summary>
    /// 1200×630: what a pasted link shows.
    /// </summary>
    Link
}

/// <param name="Posters">Up to four posters, already downloaded; one that couldn't be is left out.</param>
/// <param name="PostersLabel">What the posters are: "You both love" or "Watch together".</param>
public sealed record MatchCardContent(string Inviter, string Friend, int Score, string Line, IReadOnlyList<byte[]> Posters, string? PostersLabel,
    string AppName);

/// <summary>
/// Draws a Taste Match's share card as a PNG: the two names, the score, its line, and a row of posters. It shows nothing
/// the result page doesn't. Drawn in process with an embedded typeface, so it needs no browser and no fonts on the host.
/// </summary>
public static class MatchCard
{
    public const int MaxPosters = 4;

    private static readonly SKColor Background = new(0x15, 0x11, 0x0e);
    private static readonly SKColor Panel = new(0x24, 0x1c, 0x17);
    private static readonly SKColor Accent = new(0xfb, 0x92, 0x3c);
    private static readonly SKColor Text = new(0xfa, 0xf5, 0xf0);
    private static readonly SKColor Muted = new(0xb5, 0xa8, 0x9c);

    private static readonly Lazy<SKTypeface> Regular = new(() => Load("Inter-Regular.ttf"));
    private static readonly Lazy<SKTypeface> SemiBold = new(() => Load("Inter-SemiBold.ttf"));
    private static readonly Lazy<SKTypeface> ExtraBold = new(() => Load("Inter-ExtraBold.ttf"));

    public static (int Width, int Height) Dimensions(MatchCardSize size) => size == MatchCardSize.Story ? (1080, 1920) : (1200, 630);

    public static byte[] Render(MatchCardContent content, MatchCardSize size)
    {
        var (width, height) = Dimensions(size);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        // A soft glow behind the number, in the accent.
        using (var glow = new SKPaint { IsAntialias = true })
        {
            var centre = size == MatchCardSize.Story ? new SKPoint(width / 2f, height * 0.36f) : new SKPoint(width * 0.3f, height * 0.5f);
            glow.Shader = SKShader.CreateRadialGradient(centre, width * 0.75f, [Accent.WithAlpha(70), Accent.WithAlpha(0)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, glow);
        }

        if (size == MatchCardSize.Story)
        {
            Story(canvas, content, width, height);
        }
        else
        {
            Link(canvas, content, width, height);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Story(SKCanvas canvas, MatchCardContent content, int width, int height)
    {
        var centre = width / 2f;
        Draw(canvas, "TV TASTE MATCH", centre, 250, SemiBold.Value, 40, Accent, SKTextAlign.Center, spacing: 6);
        Draw(canvas, Fit($"{content.Inviter} × {content.Friend}", SemiBold.Value, 64, width - 160), centre, 370, SemiBold.Value, 64, Text, SKTextAlign.Center);
        Draw(canvas, $"{content.Score}%", centre, 760, ExtraBold.Value, 380, Accent, SKTextAlign.Center);
        Draw(canvas, "match", centre, 860, SemiBold.Value, 64, Text, SKTextAlign.Center);
        var y = 1000f;
        foreach (var line in Wrap(content.Line, Regular.Value, 52, width - 200))
        {
            Draw(canvas, line, centre, y, Regular.Value, 52, Text, SKTextAlign.Center);
            y += 70;
        }

        if (content.Posters.Count > 0)
        {
            Draw(canvas, content.PostersLabel?.ToUpperInvariant() ?? "", centre, 1260, SemiBold.Value, 34, Muted, SKTextAlign.Center, spacing: 4);
            const float posterWidth = 210, posterHeight = 315, gap = 24;
            var total = content.Posters.Count * posterWidth + (content.Posters.Count - 1) * gap;
            Posters(canvas, content.Posters, centre - total / 2, 1310, posterWidth, posterHeight, gap);
        }
        Draw(canvas, content.AppName, centre, height - 110, SemiBold.Value, 44, Muted, SKTextAlign.Center);
    }

    private static void Link(SKCanvas canvas, MatchCardContent content, int width, int height)
    {
        const float left = 80;
        // The posters take the right-hand side when there are any; the text keeps the left.
        var textWidth = content.Posters.Count > 0 ? 600f : width - 2 * left;
        Draw(canvas, "TV TASTE MATCH", left, 110, SemiBold.Value, 26, Accent, SKTextAlign.Left, spacing: 4);
        Draw(canvas, Fit($"{content.Inviter} × {content.Friend}", SemiBold.Value, 44, textWidth), left, 180, SemiBold.Value, 44, Text, SKTextAlign.Left);
        Draw(canvas, $"{content.Score}%", left - 8, 400, ExtraBold.Value, 230, Accent, SKTextAlign.Left);
        var y = 470f;
        foreach (var line in Wrap(content.Line, Regular.Value, 32, textWidth).Take(2))
        {
            Draw(canvas, line, left, y, Regular.Value, 32, Text, SKTextAlign.Left);
            y += 44;
        }
        Draw(canvas, content.AppName, left, height - 50, SemiBold.Value, 28, Muted, SKTextAlign.Left);

        if (content.Posters.Count > 0)
        {
            // Two by two, or one row of up to two.
            const float posterWidth = 190, posterHeight = 250, gap = 20;
            var columns = Math.Min(2, content.Posters.Count);
            var rows = (content.Posters.Count + 1) / 2;
            var x = width - left - columns * posterWidth - (columns - 1) * gap;
            var top = (height - rows * posterHeight - (rows - 1) * gap) / 2;
            for (var row = 0; row < rows; row++)
            {
                Posters(canvas, content.Posters.Skip(row * 2).Take(2).ToList(), x, top + row * (posterHeight + gap), posterWidth, posterHeight, gap);
            }
        }
    }

    /// <summary>
    /// A row of posters, each cropped to fill its rounded frame. A picture that can't be read leaves its frame plain.
    /// </summary>
    private static void Posters(SKCanvas canvas, IReadOnlyList<byte[]> posters, float x, float y, float width, float height, float gap)
    {
        using var frame = new SKPaint { IsAntialias = true, Color = Panel };
        using var picture = new SKPaint { IsAntialias = true };
        foreach (var bytes in posters)
        {
            var rect = new SKRect(x, y, x + width, y + height);
            using var shape = new SKRoundRect(rect, 18);
            canvas.DrawRoundRect(shape, frame);
            using var bitmap = Decode(bytes);
            if (bitmap is not null)
            {
                // Fill the frame: crop whichever dimension overflows.
                var scale = Math.Max(width / bitmap.Width, height / bitmap.Height);
                var (cropWidth, cropHeight) = (width / scale, height / scale);
                var source = SKRect.Create((bitmap.Width - cropWidth) / 2, (bitmap.Height - cropHeight) / 2, cropWidth, cropHeight);
                canvas.Save();
                canvas.ClipRoundRect(shape, antialias: true);
                using var image = SKImage.FromBitmap(bitmap);
                canvas.DrawImage(image, source, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), picture);
                canvas.Restore();
            }
            x += width + gap;
        }
    }

    /// <summary>
    /// The picture in <paramref name="bytes"/>, or null when they aren't one.
    /// </summary>
    private static SKBitmap? Decode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec is null ? null : SKBitmap.Decode(codec);
    }

    private static void Draw(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float size, SKColor color, SKTextAlign align,
        float spacing = 0)
    {
        using var font = new SKFont(typeface, size) { Subpixel = true };
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        if (spacing == 0)
        {
            canvas.DrawText(text, x, y, align, font, paint);
            return;
        }

        // Letter-spaced: drawn a character at a time.
        var widths = text.Select(c => font.MeasureText(c.ToString()) + spacing).ToList();
        var start = align == SKTextAlign.Center ? x - (widths.Sum() - spacing) / 2 : x;
        for (var i = 0; i < text.Length; i++)
        {
            canvas.DrawText(text[i].ToString(), start, y, SKTextAlign.Left, font, paint);
            start += widths[i];
        }
    }

    /// <summary>
    /// <paramref name="text"/> in lines no wider than <paramref name="maxWidth"/>, broken between words.
    /// </summary>
    internal static List<string> Wrap(string text, SKTypeface typeface, float size, float maxWidth)
    {
        using var font = new SKFont(typeface, size);
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var longer = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length > 0 && font.MeasureText(longer) > maxWidth)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = longer;
            }
        }
        if (line.Length > 0)
        {
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// <paramref name="text"/>, cut short with an ellipsis when it is wider than <paramref name="maxWidth"/>.
    /// </summary>
    internal static string Fit(string text, SKTypeface typeface, float size, float maxWidth)
    {
        using var font = new SKFont(typeface, size);
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }
        var cut = text;
        while (cut.Length > 1 && font.MeasureText(cut + "…") > maxWidth)
        {
            cut = cut[..^1];
        }
        return cut.TrimEnd() + "…";
    }

    internal static SKTypeface Typeface(string weight) => weight switch { "regular" => Regular.Value, "semibold" => SemiBold.Value, _ => ExtraBold.Value };

    private static SKTypeface Load(string file)
    {
        using var stream = typeof(MatchCard).Assembly.GetManifestResourceStream($"Analytics.API.Fonts.{file}")
            ?? throw new InvalidOperationException($"The card's typeface {file} isn't embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SKTypeface.FromData(SKData.CreateCopy(memory.ToArray()));
    }
}
