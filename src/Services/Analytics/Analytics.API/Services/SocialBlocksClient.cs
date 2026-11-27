using System.Net.Http.Json;

namespace Analytics.API.Services;

/// <summary>
/// Asks Social.API, which keeps blocks, whether either of two members has blocked the other.
/// </summary>
public sealed class SocialBlocksClient(HttpClient http)
{
    public async Task<bool> BlockedBetweenAsync(string a, string b, CancellationToken ct)
    {
        var answer = await http.GetFromJsonAsync<Answer>(
            $"api/v1/internal/blocks/between?a={Uri.EscapeDataString(a)}&b={Uri.EscapeDataString(b)}", ct);
        return answer?.Blocked ?? throw new InvalidOperationException("Social answered a blocks question with nothing.");
    }

    private sealed record Answer(bool Blocked);
}
