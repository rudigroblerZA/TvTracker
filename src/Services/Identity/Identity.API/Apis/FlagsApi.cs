using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Identity.API.Apis;

/// <param name="Default">What flagd.json says: its default variant.</param>
/// <param name="Targeting">flagd.json targets some users differently; an override applies to everyone.</param>
/// <param name="Override">Set from the flags page; null when flagd.json decides.</param>
public record FlagDto(string Key, string? Description, bool Default, bool Targeting, bool? Override, bool On);

/// <param name="On">true or false to override flagd.json; null to go back to it.</param>
public record SetFlagRequest(bool? On);

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

    private static async Task<Results<Ok<FlagDto>, NotFound>> SetAsync(
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

            var overrides = await ReadFlagsAsync(Path.Combine(directory, OverridesFile), ct);
            if (request.On is { } on)
            {
                overrides[key] = new JsonObject
                {
                    ["state"] = "ENABLED",
                    ["variants"] = new JsonObject { ["on"] = true, ["off"] = false },
                    ["defaultVariant"] = on ? "on" : "off",
                    ["metadata"] = new JsonObject { ["description"] = $"Turned {(on ? "on" : "off")} on the flags page by {user.FindFirstValue("preferred_username")}" }
                };
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
            logger.LogInformation("Flag {Flag} set to {Value} by {User}", key, request.On?.ToString() ?? "flagd.json's", user.FindFirstValue("preferred_username"));
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
                bool? overridden = overrides[f.Key] is JsonObject o ? IsOn(o) : null;
                return new FlagDto(f.Key, flag["metadata"]?["description"]?.GetValue<string>(), on, flag["targeting"] is not null,
                    overridden, overridden ?? on);
            })
            .OrderBy(f => f.Key)
            .ToList();
    }

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

    private static bool IsAdmin(ClaimsPrincipal user, IConfiguration config) =>
        user.FindFirstValue("preferred_username") is { } name
        && (config.GetSection("Admin:Users").Get<string[]>() ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string? Directory(IConfiguration config) =>
        config["Flags:Directory"] is { Length: > 0 } directory && System.IO.Directory.Exists(directory) ? directory : null;
}
