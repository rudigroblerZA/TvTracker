using System.Net;
using System.Net.Http.Headers;
using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// How long the app waits on a 429: the seconds it gives, or until its date, and never a negative time (a date already
/// past, or a clock ahead of the server's), which Task.Delay would refuse.
/// </summary>
public class RetryAfterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage TooMany(RetryConditionHeaderValue? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    [Fact]
    public void Seconds_are_waited_as_given() =>
        Assert.Equal(TimeSpan.FromSeconds(10), ApiClientBase.RetryAfter(TooMany(new(TimeSpan.FromSeconds(10))), Now));

    [Fact]
    public void A_date_is_waited_until_and_a_past_one_means_now()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ApiClientBase.RetryAfter(TooMany(new(Now.AddSeconds(5))), Now));
        Assert.Equal(TimeSpan.Zero, ApiClientBase.RetryAfter(TooMany(new(Now.AddSeconds(-2))), Now));
    }

    [Fact]
    public void No_header_means_no_wait_to_honour() => Assert.Null(ApiClientBase.RetryAfter(TooMany(null), Now));
}
