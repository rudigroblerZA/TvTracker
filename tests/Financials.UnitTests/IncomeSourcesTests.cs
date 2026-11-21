using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Financials.API.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Financials.UnitTests;

/// <summary>
/// Asking Identity.API and Catalog.API for income: the admin's token is passed on, and a service that can't be reached
/// is a gap in the statement rather than an error.
/// </summary>
public class IncomeSourcesTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://service/") };
    }

    private static IncomeSources Sources(StubHandler handler) => new(new Factory(handler), NullLogger<IncomeSources>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_admins_token_is_passed_on_to_the_service_asked()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SubscriptionIncome([], [])) });

        var income = await Sources(handler).SubscriptionsAsync("Bearer abc", 6, Ct);

        Assert.NotNull(income);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "abc"), request.Headers.Authorization);
        Assert.Equal("/api/v1/admin/income?months=6", request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task A_missing_or_malformed_token_is_not_sent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new SubscriptionIncome([], [])) });

        await Sources(handler).SubscriptionsAsync("", 3, Ct);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Subscription_income_is_null_when_the_service_says_no(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        Assert.Null(await Sources(handler).SubscriptionsAsync("Bearer abc", 6, Ct));
    }

    [Fact]
    public async Task A_service_that_cant_be_reached_is_a_gap_not_a_failure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var sources = Sources(handler);

        Assert.Null(await sources.SubscriptionsAsync("Bearer abc", 6, Ct));
        Assert.Equal(FinancialStatement.AffiliatesUnknown, (await sources.CommissionAsync("Bearer abc", 6, Ct)).State);
    }

    [Fact]
    public async Task A_service_that_answers_nonsense_is_a_gap_too()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json") });

        Assert.Null(await Sources(handler).SubscriptionsAsync("Bearer abc", 6, Ct));
    }

    [Fact]
    public async Task Commission_is_unknown_not_off_when_catalog_answers_404()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(FinancialStatement.AffiliatesUnknown, (await Sources(handler).CommissionAsync("Bearer abc", 6, Ct)).State);
    }

    [Fact]
    public async Task Commission_is_off_when_catalog_says_so_with_no_content()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var commission = await Sources(handler).CommissionAsync("Bearer abc", 6, Ct);

        Assert.Equal(FinancialStatement.AffiliatesOff, commission.State);
        Assert.Empty(commission.Months);
    }

    [Fact]
    public async Task Commission_is_on_with_the_months_catalog_sends()
    {
        var month = new CommissionMonth(new DateOnly(2026, 10, 1), "USD", 12.5m);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new List<CommissionMonth> { month }) });

        var commission = await Sources(handler).CommissionAsync("Bearer abc", 3, Ct);

        Assert.Equal(FinancialStatement.AffiliatesOn, commission.State);
        Assert.Equal(month, Assert.Single(commission.Months));
    }

    [Fact]
    public async Task Commission_is_unknown_when_catalog_fails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        Assert.Equal(FinancialStatement.AffiliatesUnknown, (await Sources(handler).CommissionAsync("Bearer abc", 3, Ct)).State);
    }
}
