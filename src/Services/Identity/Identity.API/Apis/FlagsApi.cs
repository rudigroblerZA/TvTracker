using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Identity.API.Apis;

/// <param name="Default">What flagd.json says: its default variant.</param>
/// <param name="Targeting">flagd.json targets some users differently; an override applies to everyone.</param>
/// <param name="Override">Set from the flags page; null when flagd.json decides.</param>
/// <param name="Plans">The plans the feature is part of, as it stands (override first); null for every plan.</param>
/// <param name="Name">The feature's short name, for the plan page.</param>
public record FlagDto(string Key, string? Description, bool Default, bool Targeting, bool? Override, bool On,
    IReadOnlyList<string>? Plans = null, string? Name = null);

/// <param name="On">true or false to override flagd.json; null to go back to it.</param>
/// <param name="Plans">With <paramref name="On"/> true: the plans the feature is part of (<c>Plans.All</c> keys); null or every
/// plan for everyone.</param>
public record SetFlagRequest(bool? On, IReadOnlyList<string>? Plans = null);

/// <summary>
/// The feature flags page: lists the flags in flagd.json and turns them on or off for everyone, for admins only (the
/// usernames in <c>Admin:Users</c>; anyone else gets 404, as if it weren't there).
/// <list type="bullet">
/// <item>
///   <b>Overrides, not edits:</b> changes go to <c>overrides.json</c> next to flagd.json, which flagd reads as a second
///   source; for a key in both, the later source wins. flagd.json stays as checked in, and removing an override goes
///   back to it, targeting included.
/// </item>
/// <item>
///   <b>Resync:</b> removing an override also rewrites flagd.json unchanged a few seconds later, since flagd otherwise
///   drops the flag altogether (<see cref="ResyncBaseLater"/>).
/// </item>
/// <item>
///   <b>Plans:</b> a flag can be part of some plans only: its targeting is <c>FeatureFlags.PlanRule</c> and its metadata
///   lists the plans, so a plan's set of features is edited here (or in flagd.json) rather than in code. Services put the
///   caller's plan in the flag context (<c>IUserPlans</c>).
/// </item>
/// <item>
///   <b>Same folder as flagd:</b> <c>Flags:Directory</c> is the AppHost's Flags folder, bind-mounted into the flagd
///   container, so this works where the service and flagd share a disk (development).
/// </item>
/// </list>
/// </summary>
public static class FlagsApi
{
    public const string OverridesFile = "overrides.json";
    private const string BaseFile = "flagd.json";

    // One writer at a time, so two quick changes can't lose one another.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly TimeSpan ResyncDelay = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void MapFlagsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/admin/flags").RequireAuthorization();
        api.MapGet("", GetAsync);
        api.MapPut("/{key}", SetAsync);
    }

    private static async Task<Results<Ok<List<FlagDto>>, NotFound>> GetAsync(ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        if (!IsAdmin(user, config) || Directory(config) is not { } directory)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(await ListAsync(directory, ct));
    }

    private static async Task<Results<Ok<FlagDto>, NotFound, ValidationProblem>> SetAsync(
        string key, SetFlagRequest request, ClaimsPrincipal user, IConfiguration config, ILogger<SetFlagRequest> logger, CancellationToken ct)
    {
        if (!IsAdmin(user, config) || Directory(config) is not { } directory)
        {
            return TypedResults.NotFound();
        }

        await Gate.WaitAsync(ct);
        try
        {
            var flags = await ReadFlagsAsync(Path.Combine(directory, BaseFile), ct);
            if (flags[key] is null)
            {
                return TypedResults.NotFound();
            }

            var plans = request.Plans?.Select(p => p.Trim().ToLowerInvariant()).Distinct().ToList();
            if (plans is not null && (plans.Count == 0 || plans.Any(p => !TvTracker.IntegrationEvents.Plans.All.Contains(p))))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["plans"] = [$"One or more of: {string.Join(", ", TvTracker.IntegrationEvents.Plans.All)}."]
                });
            }
            // Every plan is the same as no plan rule.
            if (plans is not null && TvTracker.IntegrationEvents.Plans.All.All(plans.Contains))
            {
                plans = null;
            }

            var overrides = await ReadFlagsAsync(Path.Combine(directory, OverridesFile), ct);
            if (request.On is { } on)
            {
                var by = user.FindFirstValue("preferred_username");
                var metadata = new JsonObject
                {
                    ["description"] = plans is null
                        ? $"Turned {(on ? "on" : "off")} on the flags page by {by}"
                        : $"Limited to {string.Join(", ", plans)} on the flags page by {by}"
                };
                if (flags[key]?["metadata"]?[TvTracker.FeatureFlags.NameMetadata] is JsonNode name)
                {
                    metadata[TvTracker.FeatureFlags.NameMetadata] = name.DeepClone();
                }
                var flag = new JsonObject
                {
                    ["state"] = "ENABLED",
                    ["variants"] = new JsonObject { ["on"] = true, ["off"] = false },
                    ["defaultVariant"] = on ? "on" : "off",
                    ["metadata"] = metadata
                };
                if (on && plans is not null)
                {
                    metadata[TvTracker.FeatureFlags.PlansMetadata] = string.Join(",", plans);
                    flag["targeting"] = JsonNode.Parse(TvTracker.FeatureFlags.PlanRule(plans));
                }
                overrides[key] = flag;
            }
            else
            {
                overrides.Remove(key);
            }

            // Written in place rather than renamed over: flagd watches the file, and keeps watching it this way.
            await File.WriteAllTextAsync(Path.Combine(directory, OverridesFile),
                new JsonObject { ["flags"] = overrides.DeepClone() }.ToJsonString(Indented), ct);

            if (request.On is null)
            {
                ResyncBaseLater(directory, logger);
            }
            logger.LogInformation("Flag {Flag} set to {Value} (plans {Plans}) by {User}", key, request.On?.ToString() ?? "flagd.json's",
                plans is null ? "all" : string.Join(",", plans), user.FindFirstValue("preferred_username"));
        }
        finally
        {
            Gate.Release();
        }

        return TypedResults.Ok((await ListAsync(directory, ct)).Single(f => f.Key == key));
    }

    /// <summary>
    /// flagd (v0.17) forgets a flag when the source that last set it drops it, rather than going back to flagd.json's.
    /// Writing flagd.json's own bytes back makes flagd re-read it (the content, and git, don't change). flagd checks
    /// the files every 3 seconds and, when both changed in one check, reads flagd.json first and then drops the flag
    /// anyway, so the rewrite waits for a later check.
    /// </summary>
    private static void ResyncBaseLater(string directory, ILogger logger) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(ResyncDelay);
            await Gate.WaitAsync();
            try
            {
                var basePath = Path.Combine(directory, BaseFile);
                await File.WriteAllBytesAsync(basePath, await File.ReadAllBytesAsync(basePath));
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't make flagd re-read flagd.json; a flag reset to it may be missing until flagd.json changes");
        }
    });

    private static async Task<List<FlagDto>> ListAsync(string directory, CancellationToken ct)
    {
        var flags = await ReadFlagsAsync(Path.Combine(directory, BaseFile), ct);
        var overrides = await ReadFlagsAsync(Path.Combine(directory, OverridesFile), ct);
        return flags
            .Where(f => f.Value is JsonObject)
            .Select(f =>
            {
                var flag = f.Value!.AsObject();
                var on = IsOn(flag);
                var o = overrides[f.Key] as JsonObject;
                bool? overridden = o is not null ? IsOn(o) : null;
                var current = o ?? flag;
                return new FlagDto(f.Key, flag["metadata"]?["description"]?.GetValue<string>(), on, flag["targeting"] is not null,
                    overridden, overridden ?? on, PlansOf(current), flag["metadata"]?[TvTracker.FeatureFlags.NameMetadata]?.GetValue<string>());
            })
            .OrderBy(f => f.Key)
            .ToList();
    }

    /// <summary>The plans in a flag's metadata; null when it has none (every plan).</summary>
    private static List<string>? PlansOf(JsonObject flag) =>
        flag["metadata"]?[TvTracker.FeatureFlags.PlansMetadata]?.GetValue<string>() is { Length: > 0 } plans
            ? plans.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : null;

    /// <summary>The value of a flag's default variant; off when the flag is disabled.</summary>
    private static bool IsOn(JsonObject flag) =>
        flag["state"]?.GetValue<string>() != "DISABLED"
        && flag["defaultVariant"]?.GetValue<string>() is { } variant
        && flag["variants"]?[variant] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static async Task<JsonObject> ReadFlagsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        await using var stream = File.OpenRead(path);
        var document = await JsonNode.ParseAsync(stream, cancellationToken: ct);
        return document?["flags"]?.DeepClone().AsObject() ?? [];
    }

    /// <summary>An app admin: a Keycloak username in <c>Admin:Users</c>. Plans use the same list.</summary>
    internal static bool IsAdmin(ClaimsPrincipal user, IConfiguration config) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string? Directory(IConfiguration config) =>
        config["Flags:Directory"] is { Length: > 0 } directory && System.IO.Directory.Exists(directory) ? directory : null;
}
