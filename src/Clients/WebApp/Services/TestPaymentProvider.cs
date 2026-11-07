using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TvTracker.UI.Services;

namespace WebApp.Services;

/// <summary>
/// The test payment provider's pages (Identity's <c>TestPaymentProvider</c>), for trying payments locally: a checkout
/// that pays, and a subscription page that renews or ends one, each by posting Identity a webhook signed with
/// <c>Payments:Test:WebhookSecret</c>, as a real provider would.
/// <list type="bullet">
/// <item><b>Development only,</b> and only with the secret.</item>
/// <item><b>Back to this site:</b> after paying, the buyer goes to <c>/settings/plan</c> here, never to an address from
/// the query, so the page can't be used to send someone elsewhere.</item>
/// <item><b>No antiforgery:</b> the forms post to development-only pages whose only effect is a pretend payment.</item>
/// </list>
/// </summary>
public static class TestPaymentProvider
{
    private const string Path = "/dev/payments";

    public static void Map(WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || app.Configuration["Payments:Test:WebhookSecret"] is not { Length: > 0 } secret)
        {
            return;
        }

        app.MapGet($"{Path}/checkout", (string? session, string? plan, string? interval, string? amount, string? currency) =>
            Page("Checkout", $"""
                <p>{E(Name(plan))} by the {E(interval)}: <strong>{E(currency)} {E(amount)}</strong></p>
                <form method="post" action="{Path}/pay" class="card">
                  <input type="hidden" name="session" value="{E(session)}"><input type="hidden" name="interval" value="{E(interval)}">
                  <input type="hidden" name="amount" value="{E(amount)}"><input type="hidden" name="currency" value="{E(currency)}">
                  <button type="submit">Pay (test)</button>
                </form>
                <p><a href="/settings/plan">Cancel and go back</a></p>
                """));

        app.MapPost($"{Path}/pay", async (HttpRequest request, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var interval = form["interval"].ToString();
            var outcome = await SendAsync(clients, secret, new
            {
                id = $"evt_{Guid.NewGuid():N}",
                type = "payment.succeeded",
                session = Guid.TryParse(form["session"], out var id) ? id : (Guid?)null,
                subscription = $"sub_{Guid.NewGuid():N}",
                payment = $"pay_{Guid.NewGuid():N}",
                amount = decimal.TryParse(form["amount"], CultureInfo.InvariantCulture, out var amount) ? amount : (decimal?)null,
                currency = form["currency"].ToString(),
                periodEndUtc = interval == "year" ? DateTime.UtcNow.AddYears(1) : DateTime.UtcNow.AddMonths(1)
            }, ct);
            return outcome is null ? Results.Redirect("/settings/plan") : Page("Checkout", $"<p class=\"outcome\">{E(outcome)}</p>");
        }).DisableAntiforgery();

        app.MapGet($"{Path}/subscription", (string? id, string? interval) => Page("Your test subscription", $"""
            <p>Subscription <code>{E(id)}</code>, by the {E(interval)}. Cancel or resume it on the plan page; here the provider
               can renew it now (another period paid) or end it now (as at a period's end without renewal).</p>
            <form method="post" action="{Path}/event" class="card">
              <input type="hidden" name="id" value="{E(id)}">
              <button type="submit" name="type" value="payment.succeeded">Renew now</button>
              <button type="submit" name="type" value="subscription.ended">End now</button>
            </form>
            <p><a href="/settings/plan">Back to your plan</a></p>
            """));

        app.MapPost($"{Path}/event", async (HttpRequest request, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var type = form["type"].ToString() is "subscription.ended" ? "subscription.ended" : "payment.succeeded";
            // A renewal names no period: Identity extends the current one by the subscription's interval.
            var outcome = await SendAsync(clients, secret, new
            {
                id = $"evt_{Guid.NewGuid():N}",
                type,
                subscription = form["id"].ToString(),
                payment = type == "payment.succeeded" ? $"pay_{Guid.NewGuid():N}" : null
            }, ct);
            return outcome is null ? Results.Redirect("/settings/plan") : Page("Your test subscription", $"<p class=\"outcome\">{E(outcome)}</p>");
        }).DisableAntiforgery();
    }

    /// <summary>
    /// Posts the event to Identity's webhook, signed; null when it was taken, else what went wrong.
    /// </summary>
    private static async Task<string?> SendAsync(IHttpClientFactory clients, string secret, object e, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(e, JsonSerializerOptions.Web);
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/payments/webhooks/test")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("TvTracker-Signature", $"t={t},v1={signature}");
        try
        {
            using var response = await clients.CreateClient(ApiClients.Identity).SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"Identity answered {(int)response.StatusCode}.";
        }
        catch (HttpRequestException ex)
        {
            return $"Identity couldn't be reached: {ex.Message}";
        }
    }

    private static string Name(string? plan) => plan switch { "plus" => "Plus", "family" => "Family", _ => plan ?? "" };

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static IResult Page(string title, string content) => Results.Content($$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Test payments</title>
        <style>
          :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
          body { max-width: 40rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
          .card { border: 1px solid #8884; border-radius: .75rem; padding: 1rem 1.25rem; margin: 1rem 0; }
          button { font: inherit; padding: .5rem 1rem; border-radius: .5rem; border: 1px solid #8886; cursor: pointer; margin-right: .5rem; }
          .outcome { font-weight: 600; }
        </style></head>
        <body>
          <h1>{{E(title)}}</h1>
          <p class="muted">This page stands in for a payment provider while you try payments locally. Nothing is charged.</p>
          {{content}}
        </body></html>
        """, "text/html; charset=utf-8");
}
