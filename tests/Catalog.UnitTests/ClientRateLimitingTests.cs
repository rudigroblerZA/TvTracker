using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Catalog.UnitTests;

/// <summary>
/// Who a request counts against: the signed-in user, else the client address the gateway or web
/// app added (the last X-Forwarded-For entry, since earlier ones come from the client), else a shared bucket for services.
/// </summary>
public class ClientRateLimitingTests
{
    private static DefaultHttpContext Request(string? userId = null, string? forwarded = null)
    {
        var context = new DefaultHttpContext();
        if (userId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test"));
        }
        if (forwarded is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwarded;
        }
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        return context;
    }

    [Fact]
    public void A_signed_in_user_is_counted_as_themselves_wherever_they_call_from() =>
        Assert.Equal(("user:abc", false), ClientRateLimiting.PartitionKey(Request("abc", "203.0.113.9")));

    [Fact]
    public void Signed_out_the_last_forwarded_address_counts_not_one_the_client_wrote()
    {
        // A client can send its own X-Forwarded-For; the gateway appends the address it saw.
        Assert.Equal(("ip:203.0.113.9", false), ClientRateLimiting.PartitionKey(Request(forwarded: "1.2.3.4, 203.0.113.9")));
    }

    [Fact]
    public void Calls_with_neither_are_another_service_and_share_one_bucket()
    {
        Assert.Equal(("service:10.0.0.7", true), ClientRateLimiting.PartitionKey(Request()));
        Assert.Equal(("service:10.0.0.7", true), ClientRateLimiting.PartitionKey(Request(forwarded: "not-an-address")));
    }

    [Fact]
    public void A_service_accounts_token_counts_as_a_service_not_a_person()
    {
        // Keycloak's client-credentials token for Mcp.Server: a sub, but it speaks for every Ask user at once.
        var context = Request("8c0f-service-account-user", "203.0.113.9");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "8c0f"), new Claim("preferred_username", "service-account-tvtracker-mcp"), new Claim("azp", "tvtracker-mcp")], "test"));

        Assert.Equal(("service:tvtracker-mcp", true), ClientRateLimiting.PartitionKey(context));
    }
}
