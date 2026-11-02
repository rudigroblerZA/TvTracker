using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TvTracker.UI.Services;

/// <summary>A show in an export, keyed by its TheTVDB id when the export has one, else its normalised title.</summary>
public sealed record ParsedShow(string Key, string? TvdbId, string Title, int? Year);

/// <param name="WatchedAtUtc">The watch's time; null when the export gives none (such a watch is marked at import time).</param>
public sealed record ParsedWatch(string ShowKey, int Season, int Episode, DateTimeOffset? WatchedAtUtc);

/// <param name="Status">"watching", "plan" or "stopped".</param>
public sealed record ParsedFollow(string ShowKey, string Status);

/// <param name="Files">The files that were read, with what each gave.</param>
/// <param name="Skipped">Rows that couldn't be read (no show, no episode number, …).</param>
public sealed record ParsedHistory(string Source, Dictionary<string, ParsedShow> Shows, List<ParsedWatch> Watches, List<ParsedFollow> Follows,
    List<string> Files, int Skipped)
{
    public DateTimeOffset? Earliest => Watches.Where(w => w.WatchedAtUtc is not null).Min(w => w.WatchedAtUtc);
    public DateTimeOffset? Latest => Watches.Where(w => w.WatchedAtUtc is not null).Max(w => w.WatchedAtUtc);
    public int Undated => Watches.Count(w => w.WatchedAtUtc is null);
}

/// <summary>
/// Reads a viewing-history export into shows, watches and follows, for Bring your history. It takes the official TV Time
/// GDPR archive (<c>tracking-prod-records-v2.csv</c>, <c>followed_tv_show.csv</c>, the older <c>seen_episode.csv</c>), the
/// TV Time Out and TV Time Liberator CSVs (<c>series_tvdb_id</c>/<c>tvdb_id</c>, <c>season</c>, <c>episode</c>,
/// <c>watched_at</c>), Trakt's JSON (history items, or the watched list with <c>last_watched_at</c>), and any CSV with a show
/// title or TheTVDB id, a season, an episode and, ideally, a date. Columns are matched by name, loosely, since the exports
/// have changed names over the years (<c>s_no</c>, <c>season_number</c>, <c>episode_season_number</c>, <c>season</c>).
/// <list type="bullet">
/// <item><b>Times without a zone</b> ("2024-11-02 11:53:58", TV Time's) are read in the viewer's time zone, so a Tuesday
/// evening stays a Tuesday evening; times with a zone are kept as given.</item>
/// <item><b>One watch per episode:</b> the earliest time wins; rewatch rows count only when there's no first watch.</item>
/// <item><b>Movies are left out:</b> the catalog has none.</item>
/// </list>
/// Nothing is sent anywhere by this class: the page decides what to do with the result.
/// </summary>
public static partial class HistoryImportParser
{
    private static readonly string[] ShowIdColumns = ["series_tvdb_id", "show_tvdb_id", "s_id", "series_id", "tv_show_id", "show_id", "tvdb_show_id"];
    private static readonly string[] TitleColumns = ["series_name", "tv_show_name", "show_name", "show_title", "series_title", "show", "series", "title", "name"];
    private static readonly string[] SeasonColumns = ["s_no", "season_number", "episode_season_number", "season_no", "season"];
    private static readonly string[] EpisodeColumns = ["ep_no", "episode_number", "episode_no", "number", "episode"];
    private static readonly string[] DateColumns = ["watched_at", "watch_date", "last_watched_at", "created_at", "ts", "date", "timestamp"];
    private static readonly string[] YearColumns = ["year", "show_year", "first_aired", "premiered", "release_date"];

    public static ParsedHistory Parse(Stream file, string fileName, TimeZoneInfo zone)
    {
        var shows = new Dictionary<string, ParsedShow>();
        var watches = new Dictionary<(string, int, int), ParsedWatch>();
        var rewatches = new Dictionary<(string, int, int), ParsedWatch>();
        var follows = new Dictionary<string, ParsedFollow>();
        var files = new List<string>();
        var skipped = 0;
        var source = "a CSV file";

        void ReadEntry(string name, Stream content)
        {
            var lower = name.ToLowerInvariant();
            if (lower.EndsWith(".json"))
            {
                var (s, w, f, n) = ReadJson(content, zone);
                if (w > 0 || f > 0 || s > 0)
                {
                    files.Add($"{Path.GetFileName(name)}: {w} watches, {f} follows");
                    source = "Trakt";
                }
                skipped += n;
                // ReadJson merged into the shared dictionaries through closures below.
                return;
            }
            if (!lower.EndsWith(".csv"))
            {
                return;
            }
            var (sw, sf, sn, kind) = ReadCsv(content);
            if (sw > 0 || sf > 0)
            {
                files.Add($"{Path.GetFileName(name)}: {sw} watches, {sf} follows");
                if (kind is not null)
                {
                    source = kind;
                }
            }
            skipped += sn;
        }

        (int, int, int, int) ReadJson(Stream content, TimeZoneInfo tz)
        {
            using var doc = JsonDocument.Parse(content);
            int s = 0, w = 0, f = 0, n = 0;
            foreach (var item in Items(doc.RootElement))
            {
                // Trakt: {watched_at, type: "episode", episode: {season, number}, show: {title, year, ids: {tvdb}}}
                if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)
                {
                    var key = AddShow(show.TryGetProperty("ids", out var ids) && ids.TryGetProperty("tvdb", out var tvdb) && tvdb.ValueKind == JsonValueKind.Number
                        ? tvdb.GetInt64().ToString() : null,
                        show.TryGetProperty("title", out var t) ? t.GetString() : null,
                        show.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null);
                    if (key is null) { n++; continue; }
                    s++;
                    if (item.TryGetProperty("episode", out var ep) && ep.ValueKind == JsonValueKind.Object)
                    {
                        var at = item.TryGetProperty("watched_at", out var wa) ? ParseDate(wa.GetString(), tz) : null;
                        if (AddWatch(key, Int(ep, "season"), Int(ep, "number"), at, first: true)) w++; else n++;
                    }
                    else if (item.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                    {
                        // Trakt's watched list: seasons[].episodes[] with last_watched_at.
                        foreach (var season in seasons.EnumerateArray())
                        {
                            var sn = Int(season, "number");
                            if (!season.TryGetProperty("episodes", out var eps)) continue;
                            foreach (var e in eps.EnumerateArray())
                            {
                                var at = e.TryGetProperty("last_watched_at", out var lw) ? ParseDate(lw.GetString(), tz) : null;
                                if (AddWatch(key, sn, Int(e, "number"), at, first: true)) w++; else n++;
                            }
                        }
                    }
                    else if (item.TryGetProperty("listed_at", out _) || item.TryGetProperty("rank", out _))
                    {
                        follows[key] = new ParsedFollow(key, "plan");
                        f++;
                    }
                }
            }
            return (s, w, f, n);
        }

        (int, int, int, string?) ReadCsv(Stream content)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var rows = Csv(reader).GetEnumerator();
            if (!rows.MoveNext()) return (0, 0, 0, null);
            var header = rows.Current.Select(h => h.Trim().Trim('﻿').ToLowerInvariant()).ToList();
            int Col(params string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);

            var idCol = Col(ShowIdColumns);
            // "tvdb_id" is the show's unless the file also has the show's id (TV Time Out's episode files: it's the episode's then).
            if (idCol < 0 && header.IndexOf("tvdb_id") is var tv and >= 0) idCol = tv;
            var titleCol = Col(TitleColumns);
            var seasonCol = Col(SeasonColumns);
            var episodeCol = Col(EpisodeColumns);
            var dateCol = Col(DateColumns);
            var yearCol = Col(YearColumns);
            var keyCol = header.IndexOf("key");
            var typeCol = header.IndexOf("type");
            var entityCol = header.IndexOf("entity_type");
            var watchedCol = header.IndexOf("is_watched");
            var statusCol = header.IndexOf("status");
            var followedCol = Col("is_followed", "followed");
            var laterCol = Col("is_for_later", "for_later", "is_watchlisted");
            var archivedCol = Col("is_archived", "archived", "is_stopped");
            var kind = header.Contains("s_no") || header.Contains("tv_show_name") || header.Contains("series_name") ? "TV Time"
                : header.Contains("series_tvdb_id") || header.Contains("is_watched") ? "TV Time Out"
                : null;
            if (idCol < 0 && titleCol < 0) return (0, 0, 0, null);

            int w = 0, f = 0, n = 0;
            while (rows.MoveNext())
            {
                var row = rows.Current;
                string? Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : null;
                if (Cell(entityCol) is "movie" || Cell(typeCol) is "movie") continue;
                var key = AddShow(Cell(idCol), Cell(titleCol), YearOf(Cell(yearCol)));
                if (key is null) { n++; continue; }

                var rowKey = Cell(keyCol) ?? "";
                var type = Cell(typeCol) ?? "";
                // Per-show rows (TV Time's user-series-*, followed_tv_show.csv, TV Time Out's series files) give follows.
                var isShowRow = rowKey.StartsWith("user-series", StringComparison.Ordinal) || seasonCol < 0 || string.IsNullOrEmpty(Cell(seasonCol));
                if (isShowRow)
                {
                    var status = Status(Cell(statusCol), Cell(archivedCol), Cell(laterCol), Cell(followedCol), type, hasFollowFile: header.Contains("tv_show_name") || header.Contains("tv_show_id"));
                    if (status is not null) { follows[key] = new ParsedFollow(key, status); f++; }
                    continue;
                }
                if (rowKey.StartsWith("tracking-stats", StringComparison.Ordinal) || type is "towatch" or "follow" || type.StartsWith("count", StringComparison.Ordinal)) continue;
                if (watchedCol >= 0 && !True(Cell(watchedCol))) continue;
                var isRewatch = rowKey.StartsWith("rewatch", StringComparison.Ordinal) || type == "rewatch";
                var at = ParseDate(Cell(dateCol), zone);
                if (AddWatch(key, Int(Cell(seasonCol)), Int(Cell(episodeCol)), at, first: !isRewatch)) w++; else n++;
            }
            return (w, f, n, kind);
        }

        string? AddShow(string? tvdbId, string? title, int? year)
        {
            tvdbId = tvdbId is { Length: > 0 } id && id.All(char.IsDigit) && id != "0" && id != "-1" ? id : null;
            var cleanTitle = title?.Trim();
            if (cleanTitle is { Length: > 0 } && TrailingYear().Match(cleanTitle) is { Success: true } m)
            {
                year ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                cleanTitle = cleanTitle[..m.Index].Trim();
            }
            if (tvdbId is null && string.IsNullOrEmpty(cleanTitle)) return null;
            var key = tvdbId is not null ? $"tvdb:{tvdbId}" : $"title:{Normalise(cleanTitle!)}:{year}";
            if (!shows.TryGetValue(key, out var known) || (known.Title.Length == 0 && cleanTitle is { Length: > 0 }))
            {
                shows[key] = new ParsedShow(key, tvdbId, cleanTitle ?? known?.Title ?? "", year ?? known?.Year);
            }
            return key;
        }

        bool AddWatch(string key, int? season, int? episode, DateTimeOffset? at, bool first)
        {
            if (season is not { } s || episode is not { } e || e <= 0 || s < 0) return false;
            var target = first ? watches : rewatches;
            var id = (key, s, e);
            if (!target.TryGetValue(id, out var known) || (at is not null && (known.WatchedAtUtc is null || at < known.WatchedAtUtc)))
            {
                target[id] = new ParsedWatch(key, s, e, at ?? known?.WatchedAtUtc);
            }
            return true;
        }

        var lowerName = fileName.ToLowerInvariant();
        if (lowerName.EndsWith(".zip"))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries.Where(e => e.Length > 0 && !IsPrivate(e.Name)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                using var content = entry.Open();
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                buffer.Position = 0;
                ReadEntry(entry.FullName, buffer);
            }
            if (source == "a CSV file") source = "a TV Time archive";
        }
        else
        {
            ReadEntry(fileName, file);
        }

        foreach (var (id, rewatch) in rewatches.Where(r => !watches.ContainsKey(r.Key)))
        {
            watches[id] = rewatch;
        }
        var used = watches.Values.Select(w => w.ShowKey).Concat(follows.Keys).ToHashSet();
        return new ParsedHistory(source, shows.Where(s => used.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value),
            watches.Values.OrderBy(w => w.WatchedAtUtc ?? DateTimeOffset.MaxValue).ToList(), follows.Values.ToList(), files, skipped);
    }

    /// <summary>Account files in a TV Time archive that an importer has no business opening.</summary>
    private static bool IsPrivate(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("token") || lower.Contains("password") || lower.Contains("login") || lower.Contains("session") || lower.Contains("device")
            || lower.Contains("ip_") || lower.Contains("auth");
    }

    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // traktexport wraps lists: {"history": [...], "watched": [...], "watchlist": [...]}
            foreach (var name in new[] { "history", "watched", "watched_shows", "watchlist", "shows", "episodes" })
            {
                if (root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray()) yield return item;
                }
            }
        }
    }

    private static string? Status(string? status, string? archived, string? later, string? followed, string type, bool hasFollowFile)
    {
        if (True(archived) || status is "stopped" || status is "dropped") return "stopped";
        if (True(later) || status is "not_started_yet" || type is "towatch") return "plan";
        if (True(followed) || status is "continuing" or "up_to_date" || type is "follow" || hasFollowFile) return "watching";
        return null;
    }

    private static bool True(string? value) => value is "1" or "true" or "True" or "TRUE" or "yes";

    private static int? Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static int? YearOf(string? value) =>
        value is { Length: >= 4 } && int.TryParse(value[..4], out var y) && y is > 1900 and < 2100 ? y : null;

    /// <summary>ISO with a zone as given; "yyyy-MM-dd HH:mm:ss" and "yyyy-MM-dd" in <paramref name="zone"/>; "0001-01-01" and blanks as none.</summary>
    internal static DateTimeOffset? ParseDate(string? value, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("0001", StringComparison.Ordinal)) return null;
        value = value.Trim();
        if (value.All(char.IsDigit) && long.TryParse(value, out var unix) && unix > 100_000_000)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix > 10_000_000_000 ? unix / 1000 : unix);
        }
        if (value.EndsWith('Z') || value[^6] is '+' or '-' && value[^3] == ':')
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var withZone) ? withZone : null;
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            // A bare date lands mid-day, so it keeps its day in any zone.
            if (naive.TimeOfDay == TimeSpan.Zero && value.Length <= 10) naive = naive.AddHours(12);
            var local = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        return null;
    }

    internal static string Normalise(string title) =>
        string.Join(' ', NotWord().Replace(title.Replace("&", " and ").ToLowerInvariant(), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A small RFC 4180 reader: quoted fields, doubled quotes, newlines inside quotes.</summary>
    private static IEnumerable<List<string>> Csv(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else quoted = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) yield return row;
                row = [];
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0) yield return row;
        }
    }

    [GeneratedRegex(@"\s*\(((?:19|20)\d{2})\)\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NotWord();
}
