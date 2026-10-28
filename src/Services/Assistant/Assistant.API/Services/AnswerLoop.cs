using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TvTracker;

namespace Assistant.API.Services;

/// <summary>A tool the model called, as the page shows it under "How I got this".</summary>
public record ToolStep(string Tool, string Arguments, bool Failed);

/// <summary>The outcome of one question: the answer and the shows it can link, or why there's none.</summary>
public record Answer(string? Text, IReadOnlyList<ShowRef> Shows, IReadOnlyList<ToolStep> Steps, string? Problem);

/// <summary>An earlier question and answer in the same conversation, for follow-ups ("and which of those ended well?").</summary>
public record Turn(string Question, string Answer, IReadOnlyList<ShowRef> Shows);

/// <summary>
/// Answers a question with the chat model on Model Runner (<c>Assistant:ChatEndpoint</c>, OpenAI-compatible;
/// <c>Assistant:ChatModel</c>) and the app's MCP tools: the model is offered every tool, the calls it makes are run
/// through the gateway, and their results go back to it until it answers.
/// <list type="bullet">
/// <item><b>Grounded:</b> the system prompt asks for answers from tool results only, and to say so when they have
/// nothing. A small model still slips; the page shows which tools were used, and every show it links is one a tool named.</item>
/// <item><b>A lookup first:</b> a reply without a tool call, while no lookup has worked yet, is sent back (twice at
/// most) with "look it up first". Left to choose, qwen3 0.6B answered a follow-up ("which of those is the shortest to
/// watch?") from the conversation alone, with a guess, and after a refused call it answered "let me check that" and
/// stopped. (Model Runner ignores <c>tool_choice: "required"</c>.)</item>
/// <item><b>Checked calls:</b> a call missing a required argument, or with a value outside a property's enum, isn't
/// run; the model is told what's wrong.</item>
/// <item><b>Known ids only:</b> a call with an id no tool returned isn't run; the model is told where ids come from.</item>
/// <item><b>Bounded:</b> at most <see cref="MaxRounds"/> rounds of tool calls (and 3 calls a round); past that the
/// model is asked to answer with what it has.</item>
/// <item><b>Own client, long timeout:</b> each model call can take minutes on a CPU, past the standard resilience
/// handler's 30 s.</item>
/// <item><b>Nothing personal:</b> the tools have no user, so the assistant knows shows, not the asker's history.</item>
/// </list>
/// </summary>
public sealed partial class AnswerLoop(IConfiguration config, Toolbox toolbox, Branding branding, TimeProvider time, ILogger<AnswerLoop> logger)
{
    public const int MaxRounds = 3;
    private const int MaxCallsPerRound = 3;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string Model => config["Assistant:ChatModel"] is { Length: > 0 } model ? model : "ai/qwen3:0.6B-Q4_K_M";

    public bool Configured => config["Assistant:ChatEndpoint"] is { Length: > 0 } && toolbox.Configured;

    public async Task<Answer> AnswerAsync(string question, IReadOnlyList<Turn> earlier, Action<ToolStep> onStep, CancellationToken ct)
    {
        if (config["Assistant:ChatEndpoint"] is not { Length: > 0 } endpoint || !toolbox.Configured)
        {
            return new Answer(null, [], [], "The assistant isn't set up here (no model server or MCP gateway).");
        }

        var tools = await toolbox.ListAsync(ct);
        if (tools.Count == 0)
        {
            return new Answer(null, [], [], "The app's tools aren't answering right now. Try again in a minute.");
        }
        var toolSpecs = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText())
            }
        }).ToArray());

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() }
        };
        foreach (var turn in earlier)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = turn.Question });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.Answer });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question + NoThink });

        var steps = new List<ToolStep>();
        // Shows an earlier answer linked stay linkable in a follow-up ("which of those…").
        var shows = earlier.SelectMany(t => t.Shows).ToList();
        var knownIds = shows.Select(s => s.Id).ToHashSet();
        var nudges = 0;
        for (var round = 0; ; round++)
        {
            var offerTools = round < MaxRounds;
            var (message, problem) = await CompleteAsync(endpoint, messages, offerTools ? toolSpecs : null, ct);
            if (message is null)
            {
                return new Answer(null, shows, steps, problem);
            }

            var calls = message["tool_calls"] as JsonArray;
            if (offerTools && calls is not { Count: > 0 } && steps.All(st => st.Failed) && nudges < 2)
            {
                nudges++;
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Look it up with a tool first, then answer." + NoThink });
                continue;
            }
            if (!offerTools || calls is not { Count: > 0 })
            {
                var text = Think().Replace(message["content"]?.GetValue<string>() ?? "", "").Trim();
                if (text.Length == 0)
                {
                    return new Answer(null, shows, steps, "The model returned no answer. Try asking another way.");
                }
                return new Answer(text, Mentioned(text, shows), steps, null);
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = calls.DeepClone()
            });
            foreach (var call in calls.Take(MaxCallsPerRound))
            {
                var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                var rawArgs = call?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                JsonObject? args;
                try
                {
                    args = JsonNode.Parse(rawArgs) as JsonObject;
                }
                catch (JsonException)
                {
                    args = null;
                }

                var tool = tools.FirstOrDefault(t => t.Name == name);
                var output = tool is null ? new ToolOutput($"There's no tool named \"{name}\".", [], true)
                    : Missing(tool.InputSchema, args) is { Count: > 0 } missing ? new ToolOutput(
                        $"Not run: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} required. {tool.Description}", [], true)
                    : NotAllowed(tool.InputSchema, args) is { } notAllowed ? new ToolOutput($"Not run: {notAllowed}", [], true)
                    : UnknownId(args, knownIds) is { } unknown ? new ToolOutput(
                        $"Not run: {unknown} isn't an id any tool has returned. Look the show up first (show_details lists its " +
                        "cast and creators with their person ids), or pass the show's title.", [], true)
                    : await toolbox.CallAsync(name, args, ct);
                knownIds.UnionWith(output.Ids ?? []);
                var step = new ToolStep(name, args?.ToJsonString() ?? rawArgs, output.IsError);
                steps.Add(step);
                onStep(step);
                shows.AddRange(output.Shows);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call?["id"]?.GetValue<string>() ?? name,
                    ["content"] = output.Text
                });
            }
            // A call past the limit still needs an answer, or the model server refuses the conversation.
            foreach (var call in calls.Skip(MaxCallsPerRound))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call?["id"]?.GetValue<string>() ?? "",
                    ["content"] = "Not run: at most three tools at a time."
                });
            }
        }
    }

    /// <summary>
    /// Required arguments the call left out or left empty. The server's own answer to those is "An error occurred
    /// invoking 'discover_list'", which tells a model nothing; this names the argument and repeats the tool's description
    /// (where the allowed values are).
    /// </summary>
    private static List<string> Missing(JsonElement schema, JsonObject? args) =>
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(r => r.GetString() ?? "")
                .Where(key => args?[key] is not JsonValue value || (value.TryGetValue<string>(out var text) && text.Trim().Length == 0))
                .ToList()
            : [];

    /// <summary>
    /// A value outside its property's allowed values (the schema's enum), with the allowed ones. qwen3 0.6B asked
    /// discover_list for the list "drama" when it meant hidden gems within drama.
    /// </summary>
    private static string? NotAllowed(JsonElement schema, JsonObject? args)
    {
        if (args is null || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("enum", out var allowed)
                && allowed.ValueKind == JsonValueKind.Array && args[property.Name] is JsonValue value && value.TryGetValue<string>(out var text)
                && !allowed.EnumerateArray().Any(a => string.Equals(a.GetString(), text, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{property.Name} \"{text}\" isn't one of {string.Join(", ", allowed.EnumerateArray().Select(a => a.GetString()))}.";
            }
        }
        return null;
    }

    /// <summary>
    /// An id among the arguments that no tool has returned. Asked about a person, qwen3 0.6B called person_insights with
    /// a made-up id ("12345678-9012-…") instead of finding the person first; told so, it can look them up.
    /// </summary>
    private static string? UnknownId(JsonObject? args, HashSet<Guid> known) =>
        args?.Select(p => p.Value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null)
            .FirstOrDefault(text => Guid.TryParse(text, out var id) && !known.Contains(id));

    private async Task<(JsonObject? Message, string? Problem)> CompleteAsync(string endpoint, JsonArray messages, JsonArray? tools, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0.2,
            // Enough for a list of a dozen shows; a small model sometimes repeats itself until the limit.
            ["max_tokens"] = 500,
            ["messages"] = messages.DeepClone()
        };
        if (tools is not null)
        {
            request["tools"] = tools.DeepClone();
        }

        try
        {
            using var response = await Http.PostAsJsonAsync(new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "chat/completions"), request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Model Runner refused a question ({Status}): {Body}", (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
                return (null, $"The model server answered {(int)response.StatusCode}. Is {Model} pulled? (The dashboard's \"Pull\" command on model-runner.)");
            }
            return (JsonNode.Parse(body)?["choices"]?[0]?["message"] as JsonObject, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Asking the model failed");
            return (null, $"The model server didn't answer ({ex.Message}).");
        }
    }

    private string SystemPrompt() =>
        $"You are {branding.AppName}'s assistant. You answer questions about TV shows for the app's users. Today is " +
        $"{time.GetUtcNow():d MMMM yyyy}.\n" +
        "Rules:\n" +
        "- Call a tool before answering. Use only facts from the tools' results; never guess ratings, dates or names.\n" +
        "- If the results don't answer the question, say so plainly.\n" +
        "- Write show titles exactly as the tools give them.\n" +
        "- Be brief: two to six sentences, or a short list of shows with a few words each. Plain text, no tables.\n" +
        "- Don't reveal plot twists or endings.\n" +
        // Mistakes qwen3 0.6B made reading the results: it called Severance's cast its creators, and read a landing
        // verdict of "as-it-ran" as a strong finale.
        "Reading the results:\n" +
        "- A show's creators are the creatives whose roles include \"Creator\"; the cast are actors, not makers.\n" +
        "- landing.verdict in show_insights: sticks or great-finale = it sticks the landing; as-it-ran = it ends about as good as " +
        "it was; weaker-last-season, finale-lets-down or fizzles = it doesn't.";

    // Qwen3 thinks before answering by default, which costs minutes on a CPU; "/no_think" turns that off.
    private string NoThink => Model.Contains("qwen3", StringComparison.OrdinalIgnoreCase) ? " /no_think" : "";

    /// <summary>The tool-named shows the answer mentions, in the order it mentions them; longer titles win overlaps.</summary>
    internal static IReadOnlyList<ShowRef> Mentioned(string answer, IEnumerable<ShowRef> named) =>
        named.DistinctBy(s => s.Id)
            .GroupBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(s => (Show: s, At: IndexOfWords(answer, s.Title)))
            .Where(x => x.At >= 0)
            .OrderBy(x => x.At).ThenByDescending(x => x.Show.Title.Length)
            .Select(x => x.Show)
            .ToList();

    /// <summary>Where the title appears as whole words, so a show called "Lost" isn't found in "lostness".</summary>
    private static int IndexOfWords(string text, string title)
    {
        for (var i = text.IndexOf(title, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(title, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + title.Length;
            if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
            {
                return i;
            }
        }
        return -1;
    }

    [GeneratedRegex(@"(?s)<think>.*?(</think>|$)")]
    private static partial Regex Think();
}
