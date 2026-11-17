using Identity.API.Payments;

namespace Identity.UnitTests;

/// <summary>
/// What a provider's webhook is answered with for each thing the ledger can say: the answer decides whether the
/// provider sends the event again.
/// </summary>
public class LedgerResultTests
{
    [Theory]
    [InlineData(LedgerResult.Applied, 200)]
    [InlineData(LedgerResult.Duplicate, 200)]
    [InlineData(LedgerResult.NotHere, 200)]
    [InlineData(LedgerResult.Unknown, 422)]
    [InlineData(LedgerResult.Refused, 409)]
    public void Only_an_unknown_checkout_asks_the_provider_to_try_again(LedgerResult result, int status) =>
        Assert.Equal(status, result.WebhookStatus());
}
