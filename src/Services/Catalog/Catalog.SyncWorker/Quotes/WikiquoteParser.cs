using System.Net;
using System.Text.RegularExpressions;

namespace Catalog.SyncWorker.Quotes;

/// <param name="Season">With <paramref name="Episode"/>, the episode's numbers from its heading; null for the show.</param>
/// <param name="Lines">Each line's speaker (empty for a stage direction) and text, in order.</param>
public sealed record ParsedQuote(int? Season, int? Episode, IReadOnlyList<(string Speaker, string Text)> Lines);

/// <summary>
/// Reads memorable lines out of a Wikiquote page's wikitext (<c>action=raw</c>). TV pages share one layout: each
/// episode is a heading carrying its numbers ("=== ''Pilot'' [1.01] ==="); an exchange is a run of
/// <c>:'''Speaker''': text</c> lines between <c>&lt;hr&gt;</c> rules or blank lines, with stage directions as
/// <c>:''[…]''</c>; a single quote is a <c>*</c> bullet, its <c>**</c> notes skipped. A show's own page also has a
/// "Dialogue" or "Quotes" section about the whole show, and links to its season pages ("Breaking Bad (season 1)").
/// <list type="bullet">
/// <item><b>Lines, not scenes:</b> an exchange over <see cref="MaxCharacters"/> is left out. Wikiquote keeps quotes
/// short by policy, and the app shows lines, never transcripts.</item>
/// <item><b>Only sections it understands:</b> cast lists, "About" (interviews about the show, Dialogue included), taglines and
/// anything else is skipped, so a page with another layout gives fewer lines rather than wrong ones.</item>
/// </list>
/// </summary>
public static partial class WikiquoteParser
{
    public const int MaxCharacters = 1200;

    /// <summary>Season pages are fetched up to this many; a soap's page can link hundreds.</summary>
    public const int MaxSeasonPages = 40;

    private static readonly HashSet<string> ShowSections = new(StringComparer.OrdinalIgnoreCase) { "Dialogue", "Dialogues", "Dialog", "Quotes" };

    /// <summary>The season pages a show's page links to, in the order it lists them.</summary>
    public static IReadOnlyList<string> SeasonPages(string wikitext, string page)
    {
        var name = Parenthetical().Replace(page.Replace('_', ' '), "").Trim();
        return Link().Matches(wikitext)
            .Select(m => m.Groups[1].Value.Replace('_', ' ').Trim())
            .Where(target => SeasonSuffix().IsMatch(target) && target.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(target => char.ToUpperInvariant(target[0]) + target[1..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSeasonPages)
            .ToList();
    }

    public static IReadOnlyList<ParsedQuote> Parse(string wikitext)
    {
        var quotes = new List<ParsedQuote>();
        (int? Season, int? Episode)? section = null;
        var sectionLevel = 0;
        (int Number, int Level)? seasonGroup = null;
        var counted = 0;
        var aboutShow = false;
        var current = new List<(string Speaker, string Text)>();
        var bullet = false;

        void Flush()
        {
            if (section is { } s && current.Count > 0 && (bullet || current.Any(l => l.Speaker.Length > 0))
                && current.Sum(l => l.Speaker.Length + l.Text.Length) <= MaxCharacters)
            {
                quotes.Add(new ParsedQuote(s.Season, s.Episode, current.ToList()));
            }
            current.Clear();
            bullet = false;
        }

        foreach (var raw in wikitext.Split('\n'))
        {
            var line = raw.Trim();
            if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                var level = heading.Groups[1].Length;
                var title = Clean(heading.Groups[2].Value);
                var seasonHeading = SeasonHeading().Match(title);
                if (level == 2)
                {
                    aboutShow = title.StartsWith("About", StringComparison.OrdinalIgnoreCase);
                }
                if (seasonGroup is { } open && level <= open.Level && !seasonHeading.Success)
                {
                    seasonGroup = null;
                }
                if (EpisodeNumbers().Match(heading.Groups[2].Value) is { Success: true } numbers)
                {
                    section = (int.Parse(numbers.Groups[1].Value), int.Parse(numbers.Groups[2].Value));
                    sectionLevel = level;
                }
                else if (seasonHeading.Success)
                {
                    seasonGroup = (int.Parse(seasonHeading.Groups[1].Value), level);
                    counted = 0;
                    section = null;
                    sectionLevel = 0;
                }
                else if (seasonGroup is { } group && level == group.Level + 1)
                {
                    // Episodes headed by title alone under "Series 1" (Sherlock): numbered in page order.
                    section = (group.Number, ++counted);
                    sectionLevel = level;
                }
                else if (ShowSections.Contains(title) && !aboutShow)
                {
                    section = (null, null);
                    sectionLevel = level;
                }
                else if (level <= sectionLevel || section is null)
                {
                    // A sibling or parent heading ends the section; a deeper one ("Part 2") stays inside it.
                    section = null;
                    sectionLevel = 0;
                }
                continue;
            }
            if (section is null)
            {
                continue;
            }

            if (line.Length == 0 || line.StartsWith("<hr", StringComparison.OrdinalIgnoreCase) || line.StartsWith("----"))
            {
                Flush();
            }
            else if (line.StartsWith("**") || line.StartsWith("*:") || line.StartsWith(":*") || IsMarkup(line))
            {
                // A bullet's note or a citation (who said it, where), a file, a template or a table: not a line.
            }
            else if (line.StartsWith('*'))
            {
                Flush();
                if (Spoken(line.TrimStart('*').Trim()) is { } spoken)
                {
                    current.Add(spoken);
                    bullet = true;
                }
                Flush();
            }
            else if (line.StartsWith(':') || SpeakerFirst().IsMatch(line))
            {
                if (Spoken(line.TrimStart(':').Trim()) is { } spoken)
                {
                    current.Add(spoken);
                }
            }
        }
        Flush();
        return quotes;
    }

    private static bool IsMarkup(string line) =>
        line.StartsWith("[[File:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[[Image:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("{{") || line.StartsWith("{|") || line.StartsWith('|') || line.StartsWith('!') || line.StartsWith("__");

    /// <summary>A line as (speaker, text), the speaker empty for a direction; null when nothing readable is left.</summary>
    private static (string Speaker, string Text)? Spoken(string line)
    {
        var speaker = "";
        if (SpeakerFirst().Match(line) is { Success: true } m)
        {
            speaker = Clean(m.Groups[1].Value);
            line = m.Groups[2].Value;
        }
        var text = Clean(line);
        return text.Length == 0 ? null : (speaker, text);
    }

    /// <summary>Wikitext to plain text: links to their labels, templates, references, tags and quote marks out.</summary>
    public static string Clean(string wikitext)
    {
        var text = Reference().Replace(wikitext, "");
        text = LineBreak().Replace(text, " ");
        text = WikipediaTemplate().Replace(text, "$1");
        for (var i = 0; i < 3 && text.Contains("{{"); i++)
        {
            text = Template().Replace(text, "");
        }
        text = Link().Replace(text, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        text = ExternalLink().Replace(text, "$1");
        text = text.Replace("'''", "").Replace("''", "");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"^(={2,6})\s*(.+?)\s*\1$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?:Season|Series)\s+(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonHeading();

    [GeneratedRegex(@"\[\s*(\d+)\s*\.\s*(\d+)")]
    private static partial Regex EpisodeNumbers();

    /// <summary><c>'''Walter''':</c> or <c>'''Walter:'''</c> at the start of a line (a name may hold one quote mark).</summary>
    [GeneratedRegex(@"^'''((?:(?!''').)+?)(?::'''|'''\s*:)\s*(.*)$")]
    private static partial Regex SpeakerFirst();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:#[^\]|]*)?(?:\|([^\]]*))?\]\]")]
    private static partial Regex Link();

    [GeneratedRegex(@"\((?:season|series)\s+\d+\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonSuffix();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"<ref[^>]*/>|<ref[^>]*>.*?</ref>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Reference();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"\{\{\s*w\s*\|([^|}]+)(?:\|[^}]*)?\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex WikipediaTemplate();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\[https?://\S+\s+([^\]]+)\]|\[https?://\S+\]")]
    private static partial Regex ExternalLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
