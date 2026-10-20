using System.CommandLine;
using Catalog.ImportCli;

// tvtracker-import: bulk imports shows into the catalog. The sync worker does the work (one job at a time, under
// TVmaze's rate limit, about two shows a second); this tool starts the job and shows its progress.
// Stopping the tool (Ctrl+C) doesn't stop the import: use `cancel` for that.

var urlOption = new Option<string>("--url")
{
    Description = "The sync worker's address (or set TVTRACKER_SYNC_URL)",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TVTRACKER_SYNC_URL") ?? "http://localhost:5107",
    Recursive = true
};
var detachOption = new Option<bool>("--detach")
{
    Description = "Start the import and exit instead of following its progress",
    Recursive = true
};

var fileArgument = new Argument<string>("file") { Description = "A file with one show title per line (blank lines and lines starting with # are skipped), or - for standard input" };
var titles = new Command("titles", "Import the best TVmaze match for each title") { fileArgument };
titles.SetAction((parse, ct) => StartAsync(parse, new BulkImportRequest(Titles: ReadLines(parse.GetValue(fileArgument)!)), ct));

var idsArgument = new Argument<string[]>("id") { Description = "TVmaze show ids", Arity = ArgumentArity.ZeroOrMore };
var idsFileOption = new Option<string>("--file") { Description = "A file with one TVmaze id per line, or - for standard input" };
var ids = new Command("ids", "Import shows by TVmaze id") { idsArgument, idsFileOption };
ids.SetAction((parse, ct) =>
{
    var given = parse.GetValue(idsArgument) ?? [];
    var fromFile = parse.GetValue(idsFileOption) is { } file ? ReadLines(file) : [];
    return StartAsync(parse, new BulkImportRequest(Ids: given.Concat(fromFile).ToList()), ct);
});

var countArgument = new Argument<int>("count") { Description = "How many shows (at most 5000)" };
var top = new Command("top", "Import TVmaze's most popular shows (reads TVmaze's whole index first, a few minutes)") { countArgument };
top.SetAction((parse, ct) => StartAsync(parse, new BulkImportRequest(Top: parse.GetValue(countArgument)), ct));

var status = new Command("status", "Show the running or most recent import (follows a running one unless --detach)");
status.SetAction(async (parse, ct) =>
{
    using var client = Connect(parse);
    var current = await client.GetCurrentAsync(ct);
    if (current is null)
    {
        Console.WriteLine("No import since the sync worker started.");
        return 0;
    }

    return current.IsRunning && !parse.GetValue(detachOption) ? await FollowAsync(client, current, ct) : Report(current);
});

var cancel = new Command("cancel", "Stop the running import after the show it's importing");
cancel.SetAction(async (parse, ct) =>
{
    using var client = Connect(parse);
    if (await client.GetCurrentAsync(ct) is not { IsRunning: true } current || !await client.CancelAsync(current.Id, ct))
    {
        Console.WriteLine("No import is running.");
        return 0;
    }

    Console.WriteLine($"Cancelling import {current.Id} ({Progress(current)}).");
    return 0;
});

var root = new RootCommand("Bulk import shows from TVmaze into the TV Tracker catalog, through the sync worker")
{
    titles, ids, top, status, cancel, urlOption, detachOption
};

try
{
    // Without the default handler, so a CliException prints just its message.
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (CliException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<int> StartAsync(ParseResult parse, BulkImportRequest request, CancellationToken ct)
{
    using var client = Connect(parse);
    var (started, job) = await client.StartAsync(request, ct);
    if (!started)
    {
        Console.Error.WriteLine($"Another import is running ({Progress(job)}). " +
            "Follow it with `tvtracker-import status`, or stop it with `tvtracker-import cancel`.");
        return 1;
    }

    Console.WriteLine($"Started import {job.Id}.");
    if (parse.GetValue(detachOption))
    {
        Console.WriteLine("Follow it with `tvtracker-import status`.");
        return 0;
    }

    return await FollowAsync(client, job, ct);
}

// Polls every two seconds. On a terminal the progress line updates in place; otherwise a line is written when it changes.
async Task<int> FollowAsync(SyncWorkerClient client, BulkImportStatus job, CancellationToken ct)
{
    var interactive = !Console.IsOutputRedirected;
    string? last = null;
    try
    {
        while (job.IsRunning)
        {
            var line = Progress(job);
            if (interactive)
            {
                Console.Write($"\r{line.PadRight(Math.Max(line.Length, last?.Length ?? 0))}");
            }
            else if (line != last)
            {
                Console.WriteLine(line);
            }
            last = line;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            job = await client.GetCurrentAsync(ct) is { } current && current.Id == job.Id
                ? current
                : throw new CliException("\nThe import is no longer known to the sync worker (it restarted?). Run it again to import the rest.");
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        Console.WriteLine();
        Console.WriteLine("Stopped following; the import carries on. `tvtracker-import status` to follow it again, `tvtracker-import cancel` to stop it.");
        return 130;
    }

    if (interactive)
    {
        Console.WriteLine();
    }
    return Report(job);
}

static string Progress(BulkImportStatus job)
{
    if (job.Stage is { } stage && stage != "Importing")
    {
        return $"{stage}…";
    }

    var percent = job.Total == 0 ? 0 : 100 * job.Processed / job.Total;
    var line = $"{job.Processed}/{job.Total} ({percent}%) · {job.Imported} imported, {job.AlreadyInCatalog} already in catalog";
    if (job.NotFound + job.Failed > 0)
    {
        line += $", {job.NotFound + job.Failed} skipped";
    }
    return job.LastImported is { } title ? $"{line} · {Truncate(title, 40)}" : line;
}

static int Report(BulkImportStatus job)
{
    var took = (job.FinishedAtUtc ?? DateTime.UtcNow) - job.StartedAtUtc;
    Console.WriteLine($"Import {job.Id}: {job.State} after {took:h\\:mm\\:ss}" + (job.Error is { } error ? $" ({error})" : ""));
    if (job.IsRunning)
    {
        Console.WriteLine($"  {Progress(job)}");
        return 0;
    }
    Console.WriteLine($"  {job.Imported} imported, {job.AlreadyInCatalog} already in catalog, {job.NotFound} not found, {job.Failed} failed" +
        (job.Processed < job.Total ? $", {job.Total - job.Processed} not reached" : ""));
    foreach (var problem in job.Problems)
    {
        Console.WriteLine($"  - {problem.Item}: {problem.Reason}");
    }

    if (job.Problems.Count < job.NotFound + job.Failed)
    {
        Console.WriteLine($"  … and {job.NotFound + job.Failed - job.Problems.Count} more (see the sync worker's log)");
    }

    return job.State switch
    {
        "completed" when job.Failed == 0 => 0,
        "completed" => 2,
        _ => 1
    };
}

static List<string> ReadLines(string file)
{
    try
    {
        var lines = file == "-" ? ReadAll(Console.In) : File.ReadAllLines(file);
        return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }
    catch (IOException ex)
    {
        throw new CliException($"Couldn't read {file}: {ex.Message}");
    }
}

SyncWorkerClient Connect(ParseResult parse) =>
    Uri.TryCreate(parse.GetValue(urlOption), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
        ? new SyncWorkerClient(url)
        : throw new CliException($"--url must be an http(s) address, like http://localhost:5107.");

static string[] ReadAll(TextReader reader) => reader.ReadToEnd().Split('\n');

static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
