using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace TvTracker.LoadGen;

/// <summary>
/// Request timings per endpoint (by route template, not URL, so "GET catalog /shows/{id}" is one row). Errors are 5xx and
/// failed connections; 429s (the services' rate limits) and 404s (a feature flag off, a show gone) are counted apart,
/// since they're the services answering as designed.
/// </summary>
public sealed class Stats
{
    private readonly ConcurrentDictionary<string, Endpoint> endpoints = new();
    private readonly ConcurrentQueue<(long At, double Ms, bool Error)> recent = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;

    public void Record(string name, double ms, int status)
    {
        var endpoint = endpoints.GetOrAdd(name, _ => new Endpoint());
        lock (endpoint)
        {
            endpoint.Latencies.Add(ms);
            endpoint.Statuses[status] = endpoint.Statuses.GetValueOrDefault(status) + 1;
        }
        recent.Enqueue((Environment.TickCount64, ms, IsError(status)));
    }

    private static bool IsError(int status) => status == 0 || status >= 500;

    /// <summary>One line for the last <paramref name="window"/>: requests a second, errors, throttled, p95.</summary>
    public string Progress(TimeSpan window, int activeUsers)
    {
        var since = Environment.TickCount64 - (long)window.TotalMilliseconds;
        while (recent.TryPeek(out var head) && head.At < since)
        {
            recent.TryDequeue(out _);
        }
        var items = recent.ToArray();
        var total = endpoints.Values.Sum(e => { lock (e) { return e.Latencies.Count; } });
        var p95 = Percentile(items.Select(i => i.Ms).Order().ToList(), 0.95);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(DateTimeOffset.UtcNow - started):mm\\:ss}  users {activeUsers,4}  {items.Length / window.TotalSeconds,7:0.0} req/s  p95 {p95,6:0} ms  errors {items.Count(i => i.Error),4}  total {total}");
    }

    public string Report()
    {
        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
        var rows = endpoints.Select(e =>
        {
            lock (e.Value)
            {
                var sorted = e.Value.Latencies.Order().ToList();
                var count = sorted.Count;
                return (Name: e.Key, Count: count,
                    Errors: e.Value.Statuses.Where(s => IsError(s.Key)).Sum(s => s.Value),
                    Throttled: e.Value.Statuses.GetValueOrDefault(429),
                    NotFound: e.Value.Statuses.GetValueOrDefault(404),
                    P50: Percentile(sorted, 0.5), P95: Percentile(sorted, 0.95), P99: Percentile(sorted, 0.99), Max: sorted.LastOrDefault());
            }
        }).OrderByDescending(r => r.Count).ToList();

        var width = Math.Max(30, rows.Select(r => r.Name.Length).DefaultIfEmpty(0).Max());
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"{"endpoint".PadRight(width)}  {"count",7} {"req/s",7} {"errors",7} {"429",5} {"404",5} {"p50",7} {"p95",7} {"p99",7} {"max",7}");
        foreach (var r in rows)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{r.Name.PadRight(width)}  {r.Count,7} {r.Count / elapsed,7:0.0} {r.Errors,7} {r.Throttled,5} {r.NotFound,5} {r.P50,7:0} {r.P95,7:0} {r.P99,7:0} {r.Max,7:0}");
        }
        var all = rows.Sum(r => r.Count);
        var allLatencies = endpoints.Values.SelectMany(e => { lock (e) { return e.Latencies.ToList(); } }).Order().ToList();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"{"all".PadRight(width)}  {all,7} {all / elapsed,7:0.0} {rows.Sum(r => r.Errors),7} {rows.Sum(r => r.Throttled),5} {rows.Sum(r => r.NotFound),5} " +
            $"{Percentile(allLatencies, 0.5),7:0} {Percentile(allLatencies, 0.95),7:0} {Percentile(allLatencies, 0.99),7:0} {allLatencies.LastOrDefault(),7:0}");
        report.Append("Times in ms. errors: 5xx and failed connections.");
        return report.ToString();
    }

    /// <summary>All requests as CSV (endpoint, count, errors, 429, 404, p50, p95, p99, max), for comparing runs.</summary>
    public string Csv()
    {
        var csv = new StringBuilder("endpoint,count,errors,throttled,not_found,p50_ms,p95_ms,p99_ms,max_ms\n");
        foreach (var (name, e) in endpoints.OrderBy(e => e.Key))
        {
            lock (e)
            {
                var sorted = e.Latencies.Order().ToList();
                csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"\"{name}\",{sorted.Count},{e.Statuses.Where(s => IsError(s.Key)).Sum(s => s.Value)},{e.Statuses.GetValueOrDefault(429)},{e.Statuses.GetValueOrDefault(404)},{Percentile(sorted, 0.5):0},{Percentile(sorted, 0.95):0},{Percentile(sorted, 0.99):0},{sorted.LastOrDefault():0}"));
            }
        }
        return csv.ToString();
    }

    public int ErrorCount => endpoints.Values.Sum(e => { lock (e) { return e.Statuses.Where(s => IsError(s.Key)).Sum(s => s.Value); } });

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private sealed class Endpoint
    {
        public List<double> Latencies { get; } = [];
        public Dictionary<int, int> Statuses { get; } = [];
    }
}
