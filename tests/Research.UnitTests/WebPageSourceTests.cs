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
    public void Only_the_configured_proxy_counts_as_the_proxy()
    {
        // A loopback proxy, as in the sandbox: the bypass list covers localhost, which must not wave the proxy itself off.
        var proxy = new WebProxy("http://127.0.0.1:37121", BypassOnLocal: true);

        Assert.True(WebPageSource.IsProxy(proxy, new DnsEndPoint("127.0.0.1", 37121)));
        Assert.False(WebPageSource.IsProxy(proxy, new DnsEndPoint("127.0.0.1", 8080)));
        Assert.False(WebPageSource.IsProxy(proxy, new DnsEndPoint("example.com", 443)));
        Assert.False(WebPageSource.IsProxy(null, new DnsEndPoint("127.0.0.1", 37121)));
    }

    [Fact]
    public async Task An_https_page_through_a_loopback_proxy_is_let_through()
    {
        // The tunnel to the proxy is attempted (nothing listens, so the OS refuses it), not stopped by the check.
        using var http = new HttpClient(WebPageSource.CreateHandler(new WebProxy("http://127.0.0.1:1", BypassOnLocal: true)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://example.com/", TestContext.Current.CancellationToken));

        Assert.IsType<SocketException>(ex.InnerException);
    }
}
