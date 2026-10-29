using Research.API.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Research.API.Services;

/// <summary>
/// Drafts the insight with a chat model on Docker Model Runner (<c>Research:ChatEndpoint</c>, an OpenAI-compatible
/// <c>/engines/v1/</c>; <c>Research:ChatModel</c>), from the included sources only, citing them as [n]. It's a draft:
/// a moderator edits and approves it before saving, which is what makes it trustworthy.
/// <list type="bullet">
/// <item><b>Own client, long timeout:</b> on a CPU a small model takes minutes, past the standard resilience
/// handler's 30 s, and a retry would only start it again.</item>
/// <item><b>Reasoning models:</b> Qwen3 is asked not to think (<c>/no_think</c>), and any &lt;think&gt; block is cut.</item>
/// </list>
/// </summary>
public sealed partial class InsightDrafter(IConfiguration config, ILogger<InsightDrafter> logger)
{
    /// <summary>Characters of each source the model reads; small models lose the thread in long prompts.</summary>
    private const int PerSource = 1500;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string Model => config["Research:ChatModel"] is { Length: > 0 } model ? model : "ai/qwen3:0.6B-Q4_K_M";

    /// <returns>The draft, or the reason there's none.</returns>
    public async Task<(string? Draft, string? Problem)> DraftAsync(string showTitle, IReadOnlyList<(int Number, ResearchSource Source)> sources, CancellationToken ct)
    {
        if (config["Research:ChatEndpoint"] is not { Length: > 0 } endpoint)
        {
            return (null, "No model server is configured (Research:ChatEndpoint). Write the insight by hand.");
        }
        if (sources.Count == 0)
        {
            return (null, "Include at least one source to draft from.");
        }

        var prompt = new StringBuilder($"Series: {showTitle}\n\nSources:\n");
        foreach (var (number, source) in sources)
        {
            prompt.Append($"[{number}] {source.Title} ({source.Url})\n{Text.Clip(source.Excerpt, PerSource)}\n\n");
        }
        prompt.Append("Write the insight now.");
        if (Model.Contains("qwen3", StringComparison.OrdinalIgnoreCase))
        {
            prompt.Append(" /no_think");
        }

        var request = new
        {
            model = Model,
            temperature = 0.3,
            max_tokens = 600,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You write short insights about TV series for the editors of a TV tracking app. Use only facts from the "
                        + "numbered sources, and put the source number in brackets, like [1], after each sentence it supports. Say what "
                        + "the series is, who made it, how it was received and what makes it distinctive. No spoilers past the premise. "
                        + "120 to 200 words of plain text, no headings or lists."
                },
                new { role = "user", content = prompt.ToString() }
            }
        };

        try
        {
            using var response = await Http.PostAsJsonAsync(new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "chat/completions"), request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Model Runner refused a draft ({Status}): {Body}", (int)response.StatusCode, Text.Clip(body, 300));
                return (null, $"The model server answered {(int)response.StatusCode}. Is {Model} pulled? (The dashboard's \"Pull\" command on model-runner.)");
            }

            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            text = Think().Replace(text, "").Trim();
            return text.Length == 0 ? (null, "The model returned nothing.") : (text, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Drafting an insight for {Show} failed", showTitle);
            return (null, $"The model server didn't answer ({ex.Message}).");
        }
    }

    [GeneratedRegex(@"(?s)<think>.*?(</think>|$)")]
    private static partial Regex Think();
}
