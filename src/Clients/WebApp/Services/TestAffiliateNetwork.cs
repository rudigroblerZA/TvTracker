using System.Globalization;
using System.Net;
using TvTracker.UI.Services;

namespace WebApp.Services;

/// <summary>
/// A pretend affiliate network for trying affiliate links locally (the AppHost's <c>Affiliates:TestNetwork=true</c>):
/// Catalog.API's test program sends every where-to-watch click here, and this page can report a sale for it through the
/// real postback (<c>/api/v1/affiliates/postback/test-network</c>) before sending the viewer on to the service, so the
/// report at /settings/affiliates fills with clicks and sales.
/// <list type="bullet">
/// <item><b>Development only,</b> and only with <c>Affiliates:TestNetworkKey</c>, the postback key Catalog.API's test
/// program expects.</item>
/// <item><b>Server to server, as a network's would be:</b> the browser never sees the key; the web app calls Catalog.API
/// directly.</item>
/// <item><b>No antiforgery:</b> the sale form posts to a development-only page whose only effect is a pretend
/// sale.</item>
/// </list>
/// </summary>
public static class TestAffiliateNetwork
{
    private const string Path = "/dev/affiliate-network";

    public static void Map(WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || app.Configuration["Affiliates:TestNetworkKey"] is not { Length: > 0 } key)
        {
            return;
        }

        app.MapGet(Path, (string? u, string? click) => Page(u, click, null));

        app.MapPost($"{Path}/sale", async (HttpRequest request, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            string? u = form["u"], click = form["click"], amount = form["amount"];
            var transaction = Guid.NewGuid().ToString("N");
            var url = $"api/v1/affiliates/postback/test-network?click={Uri.EscapeDataString(click ?? "")}&transaction={transaction}" +
                      $"&amount={Uri.EscapeDataString(amount ?? "0")}&currency=USD&key={Uri.EscapeDataString(key)}";
            string outcome;
            try
            {
                using var response = await clients.CreateClient(ApiClients.Catalog).GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                outcome = response.IsSuccessStatusCode
                    ? $"Sale reported: ${amount} commission (transaction {transaction[..8]}…). It's in the report now."
                    : $"The postback answered {(int)response.StatusCode}: {body}";
            }
            catch (HttpRequestException ex)
            {
                outcome = $"The postback failed: {ex.Message}";
            }
            return Page(u, click, outcome);
        }).DisableAntiforgery();
    }

    private static IResult Page(string? u, string? click, string? outcome)
    {
        var destination = Uri.TryCreate(u, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp)
            ? target.AbsoluteUri
            : null;
        string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Test affiliate network</title>
            <style>
              :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
              body { max-width: 40rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
              .card { border: 1px solid #8884; border-radius: .75rem; padding: 1rem 1.25rem; margin: 1rem 0; }
              code { word-break: break-all; }
              button, .go { font: inherit; padding: .5rem 1rem; border-radius: .5rem; border: 1px solid #8886; cursor: pointer; }
              .go { display: inline-block; text-decoration: none; }
              .outcome { font-weight: 600; }
            </style></head>
            <body>
              <h1>Test affiliate network</h1>
              <p>This page stands in for an affiliate network while you try affiliate links locally. A real network would count
                 the click, send you on, and later report any sale back to the app.</p>
              <div class="card">
                <div>Click id: <code>{{E(click)}}</code></div>
                <div>Destination: <code>{{E(destination ?? "(none)")}}</code></div>
              </div>
              {{(outcome is null ? "" : $"<p class=\"outcome\">{E(outcome)}</p>")}}
              <form method="post" action="{{Path}}/sale" class="card">
                <input type="hidden" name="u" value="{{E(u)}}"><input type="hidden" name="click" value="{{E(click)}}">
                <label>Commission (USD) <input name="amount" value="{{E(4.20m.ToString(CultureInfo.InvariantCulture))}}" inputmode="decimal" size="8"></label>
                <button type="submit">Report a sale</button>
              </form>
              {{(destination is null ? "" : $"<a class=\"go\" href=\"{E(destination)}\" rel=\"noreferrer\">Continue to the service</a>")}}
            </body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
