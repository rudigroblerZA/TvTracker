using System.CommandLine;
using Catalog.ImportCli;

// tvtracker-import: bulk imports shows into the catalog, and (wikidata) runs the jobs that add Wikidata's data to it. The sync worker does the work (one job at a time, under
// its provider's rate limit, about two shows a second for TVmaze); this tool starts the job and shows its progress. The shows come from whichever
// provider the catalog was set up with (Catalog:Provider: TVmaze unless said otherwise, or Wikidata or TheTVDB), so ids are that provider's.
// Against a deployment, scripts/deploy-import.sh runs it inside the stack's network, where the sync worker is.
// Stopping the tool (Ctrl+C) doesn't stop the import: use `cancel` for that.

var urlOption = new Option<string>("--url")
{
    Description = "The sync worker's address (or set TVTRACKER_SYNC_URL)",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TVTRACKER_SYNC_URL") ?? "http://localhost:5107",
    Recursive = true
};
var keycloakOption = new Option<string>("--keycloak")
{
    Description = "Keycloak's address, where the tool signs in as the internal client (or set TVTRACKER_KEYCLOAK_URL)",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TVTRACKER_KEYCLOAK_URL") ?? "http://localhost:8080",
    Recursive = true
};
var secretOption = new Option<string>("--client-secret")
{
    Description = "The internal client's secret: the AppHost's internal-client-secret, INTERNAL_CLIENT_SECRET in a deployment's .env " +
        "(or set TVTRACKER_INTERNAL_SECRET; the development value by default)",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TVTRACKER_INTERNAL_SECRET") ?? "tvtracker-internal-dev-secret",
    Recursive = true
};
var detachOption = new Option<bool>("--detach")
{
    Description = "Start the import and exit instead of following its progress",
    Recursive = true
};

var fileArgument = new Argument<string>("file") { Description = "A file with one show title per line (blank lines and lines starting with # are skipped), or - for standard input" };
var titles = new Command("titles", "Import the catalog provider's best match for each title") { fileArgument };
titles.SetAction((parse, ct) => StartAsync(parse, new BulkImportRequest(Titles: ReadLines(parse.GetValue(fileArgument)!)), ct));

var idsArgument = new Argument<string[]>("id") { Description = "Show ids at the catalog's provider (TVmaze's unless the catalog uses another)", Arity = ArgumentArity.ZeroOrMore };
var idsFileOption = new Option<string>("--file") { Description = "A file with one id per line, or - for standard input" };
var ids = new Command("ids", "Import shows by their id at the catalog's provider") { idsArgument, idsFileOption };
ids.SetAction((parse, ct) =>
{
    var given = parse.GetValue(idsArgument) ?? [];
    var fromFile = parse.GetValue(idsFileOption) is { } file ? ReadLines(file) : [];
    return StartAsync(parse, new BulkImportRequest(Ids: given.Concat(fromFile).ToList()), ct);
});

var countArgument = new Argument<int>("count") { Description = "How many shows (at most 5000)" };
var top = new Command("top", "Import the provider's most popular shows (with TVmaze, its whole index is read first: a few minutes)") { countArgument };
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

// wikidata: the background jobs that add Wikidata's data to the catalog, run now instead of on their timers.
// "atlas" and "fill" are the sync worker's, "research" is Research.API's.
var researchUrlOption = new Option<string>("--research-url")
{
    Description = "Research.API's address, for the research job (or set TVTRACKER_RESEARCH_URL)",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TVTRACKER_RESEARCH_URL") ?? "http://localhost:5114",
    Recursive = true
};
var jobsArgument = new Argument<string[]>("job")
{
    Description = "Which to run: atlas (where shows are set and filmed), fill (overview, network, premiere date and poster for shows " +
        "without), research (popular shows checked against Wikidata, and their awards). All three when none is given.",
    Arity = ArgumentArity.ZeroOrMore
};
jobsArgument.AcceptOnlyFromAmong(WikidataJob.Names);
var wikidataRun = new Command("run", "Run the jobs now and follow them until they've caught up (unless --detach)") { jobsArgument };
wikidataRun.SetAction((parse, ct) => RunWikidataAsync(parse, parse.GetValue(jobsArgument) is { Length: > 0 } given ? given : WikidataJob.Names, ct));
var wikidataStatus = new Command("status", "Show how each job is doing");
wikidataStatus.SetAction(async (parse, ct) =>
{
    foreach (var job in await WikidataStatusAsync(parse, WikidataJob.Names, ct))
    {
        Console.WriteLine(Describe(job));
    }
    return 0;
});
var wikidata = new Command("wikidata", "Run the jobs that add Wikidata's data to the catalog, or see how they're doing")
{
    wikidataRun, wikidataStatus, researchUrlOption
};

var root = new RootCommand("Bulk import shows into the catalog from its provider (TVmaze, Wikidata or TheTVDB), through the sync worker")
{
    titles, ids, top, status, cancel, wikidata, urlOption, keycloakOption, secretOption, detachOption
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

// Asks each job to run, then polls every two seconds until none is still at it. A job that's off, or that fails, is
// reported and makes the exit code 2; the others carry on.
async Task<int> RunWikidataAsync(ParseResult parse, IReadOnlyList<string> names, CancellationToken ct)
{
    names = names.Distinct().ToList();
    var skipped = false;
    using (var sync = Connect(parse))
    using (var research = ConnectResearch(parse))
    {
        foreach (var name in names)
        {
            var (started, job) = await WikidataJob.ClientFor(name, sync, research).RunJobAsync(WikidataJob.RunPath(name), ct);
            Console.WriteLine(started ? $"Started {name}." : $"{name}: {job.Note ?? "off"}");
            skipped |= !started;
        }
    }
    if (parse.GetValue(detachOption))
    {
        Console.WriteLine("Follow them with `tvtracker-import wikidata status`.");
        return skipped ? 2 : 0;
    }

    var interactive = !Console.IsOutputRedirected;
    string? last = null;
    IReadOnlyList<JobStatus> jobs;
    try
    {
        while (true)
        {
            jobs = await WikidataStatusAsync(parse, names, ct);
            var line = string.Join(" · ", jobs.Where(j => j.State != "off").Select(Brief));
            if (interactive)
            {
                Console.Write($"\r{line.PadRight(Math.Max(line.Length, last?.Length ?? 0))}");
            }
            else if (line != last)
            {
                Console.WriteLine(line);
            }
            last = line;
            if (!jobs.Any(j => j.Requested))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        Console.WriteLine();
        Console.WriteLine("Stopped following; the jobs carry on. `tvtracker-import wikidata status` to see them.");
        return 130;
    }

    if (interactive)
    {
        Console.WriteLine();
    }
    foreach (var job in jobs)
    {
        Console.WriteLine(Describe(job));
    }
    return skipped || jobs.Any(j => j.LastError is not null) ? 2 : 0;
}

async Task<IReadOnlyList<JobStatus>> WikidataStatusAsync(ParseResult parse, IReadOnlyList<string> names, CancellationToken ct)
{
    var jobs = new List<JobStatus>();
    if (names.Any(n => n != WikidataJob.Research))
    {
        using var sync = Connect(parse);
        jobs.AddRange(await sync.GetJobsAsync(WikidataJob.SyncJobsPath, ct));
    }
    if (names.Contains(WikidataJob.Research))
    {
        using var research = ConnectResearch(parse);
        jobs.AddRange(await research.GetJobsAsync(WikidataJob.ResearchJobsPath, ct));
    }
    return jobs.Where(j => names.Contains(j.Name)).OrderBy(j => names.ToList().IndexOf(j.Name)).ToList();
}

static string Brief(JobStatus job) =>
    $"{job.Name}: {job.Done} done" + (job.Due is > 0 ? $", {job.Due} to go" : "") + (job.State == "running" ? "…" : "");

static string Describe(JobStatus job)
{
    if (job.State == "off")
    {
        return $"{job.Name}: {job.Note ?? "off"}";
    }
    var line = $"{job.Name}: {job.State}{(job.Requested ? " (asked to run now)" : "")}, {job.Done} done" +
        (job.Due is { } due ? $", {due} to go" : "") +
        (job.LastRunAtUtc is { } at ? $", last round {at:yyyy-MM-dd HH:mm} UTC" : ", no round yet");
    return job.LastError is { } error ? $"{line}\n  last round: {error}" : line;
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
        ? Uri.TryCreate(parse.GetValue(keycloakOption), UriKind.Absolute, out var keycloak) && keycloak.Scheme is "http" or "https"
            ? new SyncWorkerClient(url, keycloak, parse.GetValue(secretOption)!)
            : throw new CliException("--keycloak must be an http(s) address, like http://localhost:8080.")
        : throw new CliException($"--url must be an http(s) address, like http://localhost:5107.");

SyncWorkerClient ConnectResearch(ParseResult parse) =>
    Uri.TryCreate(parse.GetValue(researchUrlOption), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
        && Uri.TryCreate(parse.GetValue(keycloakOption), UriKind.Absolute, out var keycloak) && keycloak.Scheme is "http" or "https"
        ? new SyncWorkerClient(url, keycloak, parse.GetValue(secretOption)!, "Research.API")
        : throw new CliException("--research-url and --keycloak must be http(s) addresses, like http://localhost:5114.");

static string[] ReadAll(TextReader reader) => reader.ReadToEnd().Split('\n');

static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
