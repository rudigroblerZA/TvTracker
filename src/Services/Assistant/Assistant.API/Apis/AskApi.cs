using Assistant.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using TvTracker;

namespace Assistant.API.Apis;

public record AskRequest(string Question, Guid? PreviousId = null);

/// <param name="Status">"queued", "thinking", "answered" or "failed".</param>
/// <param name="Ahead">Questions waiting ahead of this one while it's queued.</param>
/// <param name="Shows">Shows the answer names that a tool returned, in the order the answer names them, for links.</param>
public record AskDto(Guid Id, string Question, Guid? PreviousId, string Status, int Ahead, IReadOnlyList<ToolStep> Steps, string? Answer,
    IReadOnlyList<ShowRef> Shows, string? Problem, DateTime AskedAtUtc, double? Seconds);

/// <param name="Available">Whether a model server and the MCP gateway are configured.</param>
public record AboutDto(bool Available, string Model, int PerHour);

/// <summary>"Ask the app": questions about shows, answered by the local model with the app's MCP tools. Signed in only.</summary>
public static class AskApi
{
    public const int MinLength = 3;
    public const int MaxLength = 500;

    public static void MapAskApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/ask").RequireAuthorization().RequireFeature(FeatureFlags.Ask);
        api.MapGet("/about", (AnswerLoop loop) => TypedResults.Ok(new AboutDto(loop.Configured, loop.Model, AskQueue.PerHour)));
        api.MapPost("", AskAsync);
        api.MapGet("/{id:guid}", Get);
    }

    private static Results<Accepted<AskDto>, ValidationProblem, ProblemHttpResult> AskAsync(
        AskRequest request, AskQueue queue, AnswerLoop loop, HttpContext http)
    {
        var question = request.Question?.Trim() ?? "";
        if (question.Length is < MinLength or > MaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["question"] = [$"Ask in {MinLength} to {MaxLength} characters."]
            });
        }
        if (!loop.Configured)
        {
            return TypedResults.Problem("The assistant isn't set up here (no model server or MCP gateway).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var (ask, refusal) = queue.Enqueue(http.User.GetUserId(), question, request.PreviousId);
        return refusal switch
        {
            AskQueue.Refusal.Busy => TypedResults.Problem("Your last question is still being answered. Wait for it, then ask again.",
                statusCode: StatusCodes.Status429TooManyRequests),
            AskQueue.Refusal.HourlyLimit => TypedResults.Problem($"That's {AskQueue.PerHour} questions this hour. Try again later.",
                statusCode: StatusCodes.Status429TooManyRequests),
            _ => TypedResults.Accepted($"/api/v1/ask/{ask!.Id}", ToDto(ask, queue))
        };
    }

    private static Results<Ok<AskDto>, NotFound> Get(Guid id, AskQueue queue, HttpContext http) =>
        queue.Get(id, http.User.GetUserId()) is { } ask ? TypedResults.Ok(ToDto(ask, queue)) : TypedResults.NotFound();

    private static AskDto ToDto(Ask ask, AskQueue queue) => new(ask.Id, ask.Question, ask.PreviousId, ask.Status,
        ask.StartedAtUtc is null ? queue.Ahead(ask) : 0, ask.Steps.ToArray(), ask.Answer, ask.Shows, ask.Problem, ask.AskedAtUtc,
        ask.FinishedAtUtc is { } finished && ask.StartedAtUtc is { } started ? Math.Round((finished - started).TotalSeconds, 1) : null);
}
