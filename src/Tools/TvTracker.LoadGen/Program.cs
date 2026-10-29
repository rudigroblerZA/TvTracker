using System.CommandLine;
using System.Globalization;
using System.Net.Http.Headers;
using TvTracker.LoadGen;

// tvtracker-load: generates load against a running AppHost, as signed-in users browsing (and optionally tracking)
// through the Mobile BFF. `setup` creates the users once, `run` drives them, `cleanup` deletes them and their data.
// Development only: setup adds a Keycloak client that accepts passwords directly.

var bffOption = new Option<string>("--url")
{
    Description = "The Mobile BFF's address (or set TVTRACKER_BFF_URL)",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("TVTRACKER_BFF_URL") ?? "http://localhost:5110",
    Recursive = true
};
var keycloakOption = new Option<string>("--keycloak")
{
    Description = "Keycloak's address",
    DefaultValueFactory = _ => "http://localhost:8080",
    Recursive = true
};
var usersOption = new Option<int>("--users", "-u") { Description = "How many load-test users", DefaultValueFactory = _ => 10, Recursive = true };
var prefixOption = new Option<string>("--prefix") { Description = "Load-test usernames start with this", DefaultValueFactory = _ => "load-", Recursive = true };
var passwordOption = new Option<string>("--password") { Description = "The load-test users' password", DefaultValueFactory = _ => "load-test", Recursive = true };
var adminUserOption = new Option<string>("--admin-user") { Description = "Keycloak's console admin (master realm)", DefaultValueFactory = _ => "admin" };
var adminPasswordOption = new Option<string>("--admin-password") { Description = "Its password (the AppHost's keycloak-admin-password parameter)", DefaultValueFactory = _ => "admin" };

var setup = new Command("setup", "Create the load-test client and users in Keycloak (safe to run again)")
{
    adminUserOption, adminPasswordOption
};
setup.SetAction(async (parse, ct) =>
{
    using var http = new HttpClient();
    var keycloak = new Keycloak(http, parse.GetValue(keycloakOption)!);
    var admin = await keycloak.AdminTokenAsync(parse.GetValue(adminUserOption)!, parse.GetValue(adminPasswordOption)!, ct);
    Console.WriteLine(await keycloak.EnsureClientAsync(admin, ct) ? $"Created client {Keycloak.ClientId}." : $"Client {Keycloak.ClientId} exists.");
    var created = 0;
    var count = parse.GetValue(usersOption);
    for (var i = 1; i <= count; i++)
    {
        if (await keycloak.EnsureUserAsync(admin, Username(parse, i), i.ToString("000", CultureInfo.InvariantCulture), parse.GetValue(passwordOption)!, ct))
        {
            created++;
        }
    }
    Console.WriteLine($"{created} users created, {count - created} already there ({Username(parse, 1)} … {Username(parse, count)}).");
    return 0;
});

var durationOption = new Option<string>("--duration", "-d") { Description = "How long to run: 30s, 5m, 1h", DefaultValueFactory = _ => "60s" };
var rampOption = new Option<string>("--ramp-up") { Description = "Start users evenly over this long", DefaultValueFactory = _ => "10s" };
var thinkOption = new Option<string>("--think") { Description = "Pause between a user's pages (±50%): 1s, 500ms; 0 for flat out", DefaultValueFactory = _ => "1s" };
var writesOption = new Option<bool>("--writes") { Description = "Also follow shows, mark episodes watched and rate them" };
var csvOption = new Option<string?>("--csv") { Description = "Also write the results to this CSV file" };

var run = new Command("run", "Sign the load-test users in and have them use the app; prints progress, then a table per endpoint")
{
    durationOption, rampOption, thinkOption, writesOption, csvOption
};
run.SetAction(async (parse, ct) =>
{
    var options = new LoadOptions(parse.GetValue(bffOption)!, parse.GetValue(usersOption), ParseDuration(parse.GetValue(durationOption)!),
        ParseDuration(parse.GetValue(rampOption)!), ParseDuration(parse.GetValue(thinkOption)!), parse.GetValue(writesOption),
        parse.GetValue(passwordOption)!, parse.GetValue(prefixOption)!);

    using var http = new HttpClient(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = Math.Max(16, options.Users * 4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    { Timeout = TimeSpan.FromSeconds(30) };
    var keycloak = new Keycloak(http, parse.GetValue(keycloakOption)!);
    var stats = new Stats();
    var pool = new ShowPool();

    var users = Enumerable.Range(1, options.Users)
        .Select(i => new VirtualUser(http, keycloak, stats, pool, options, Username(parse, i)))
        .ToList();
    await pool.FillAsync(users[0], ct);
    if (pool.Count == 0)
    {
        throw new CliException($"The catalog returned no shows through {options.BffUrl}. Is the AppHost running, with shows imported?");
    }
    Console.WriteLine($"{options.Users} users for {options.Duration}, ramping up over {options.RampUp}, {options.Think} between pages" +
        $"{(options.Writes ? ", with writes" : ", reads only")}, {pool.Count} shows. Ctrl+C stops early.");

    using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    stop.CancelAfter(options.Duration);
    var active = 0;
    var tasks = users.Select(async (user, i) =>
    {
        try
        {
            await Task.Delay(options.RampUp * i / Math.Max(1, options.Users), stop.Token);
            Interlocked.Increment(ref active);
            await user.RunAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }).ToList();

    var window = TimeSpan.FromSeconds(5);
    while (!stop.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(window, stop.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        Console.WriteLine(stats.Progress(window, Volatile.Read(ref active)));
    }
    await Task.WhenAll(tasks);

    Console.WriteLine();
    Console.WriteLine(stats.Report());
    if (parse.GetValue(csvOption) is { } csv)
    {
        await File.WriteAllTextAsync(csv, stats.Csv(), CancellationToken.None);
        Console.WriteLine($"Wrote {csv}.");
    }
    return stats.ErrorCount > 0 ? 2 : 0;
});

var cleanup = new Command("cleanup", "Delete the load-test users and everything they did, through the app's own account deletion")
{
    adminUserOption, adminPasswordOption
};
cleanup.SetAction(async (parse, ct) =>
{
    using var http = new HttpClient();
    var keycloak = new Keycloak(http, parse.GetValue(keycloakOption)!);
    var admin = await keycloak.AdminTokenAsync(parse.GetValue(adminUserOption)!, parse.GetValue(adminPasswordOption)!, ct);
    var usernames = await keycloak.FindUsersAsync(admin, parse.GetValue(prefixOption)!, ct);
    var deleted = 0;
    foreach (var username in usernames)
    {
        // Identity.API deletes the account everywhere (each service's data, then the Keycloak user), as Settings does.
        var (token, _) = await keycloak.SignInAsync(username, parse.GetValue(passwordOption)!, ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete, parse.GetValue(bffOption)!.TrimEnd('/') + "/identity/api/v1/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            deleted++;
        }
        else
        {
            Console.Error.WriteLine($"{username}: {(int)response.StatusCode}");
        }
    }
    Console.WriteLine($"Deleted {deleted} of {usernames.Count} load-test users.");
    return deleted == usernames.Count ? 0 : 1;
});

var root = new RootCommand("Generate load against a running TV Tracker AppHost with load-test users")
{
    setup, run, cleanup, bffOption, keycloakOption, usersOption, prefixOption, passwordOption
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
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"{ex.Message} Is the AppHost running?");
    return 1;
}

string Username(ParseResult parse, int i) => $"{parse.GetValue(prefixOption)}{i:0000}";

static TimeSpan ParseDuration(string text)
{
    text = text.Trim().ToLowerInvariant();
    var (number, unit) = text.EndsWith("ms") ? (text[..^2], 0.001) : text[^1] switch
    {
        's' => (text[..^1], 1.0),
        'm' => (text[..^1], 60.0),
        'h' => (text[..^1], 3600.0),
        _ => (text, 1.0)
    };
    return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
        ? TimeSpan.FromSeconds(value * unit)
        : throw new CliException($"\"{text}\" isn't a duration; use e.g. 30s, 5m, 1h or 500ms.");
}
