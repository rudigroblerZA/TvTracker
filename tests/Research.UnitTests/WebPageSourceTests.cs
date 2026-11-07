using System.Net;
using System.Net.Sockets;
using Research.API.Services;

namespace Research.UnitTests;

/// <summary>
/// The page fetcher's connect step: only public addresses, checked on the addresses it connects to, and the configured
/// proxy let through. No network: loopback and private addresses only.
/// </summary>
public class WebPageSourceTests
{
    [Theory]
    [InlineData("http://10.255.255.1/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]       // a name that resolves to a private address, as a rebinding host would
    public async Task A_private_address_is_refused_at_connect(string url)
    {
        using var http = new HttpClient(WebPageSource.CreateHandler(proxy: null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url, TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Equal("The address isn't on the public web.", ex.InnerException!.Message);
    }

    [Fact]
    public async Task The_configured_proxy_is_let_through()
    {
        // Nothing listens on port 1, so the connection to the proxy is attempted and refused by the OS, not by the check.
        using var http = new HttpClient(WebPageSource.CreateHandler(new WebProxy("http://127.0.0.1:1")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://example.com/", TestContext.Current.CancellationToken));

        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact]
    public void Only_the_proxy_for_the_request_counts_as_the_proxy()
    {
        var proxy = new WebProxy("http://10.0.0.5:3128");
        var target = new Uri("https://example.com/");

        Assert.True(WebPageSource.IsProxy(proxy, target, new DnsEndPoint("10.0.0.5", 3128)));
        Assert.False(WebPageSource.IsProxy(proxy, target, new DnsEndPoint("10.0.0.5", 8080)));
        Assert.False(WebPageSource.IsProxy(proxy, target, new DnsEndPoint("example.com", 443)));
        Assert.False(WebPageSource.IsProxy(null, target, new DnsEndPoint("10.0.0.5", 3128)));
    }
}
