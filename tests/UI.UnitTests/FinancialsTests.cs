using TvTracker.UI.Services;

namespace UI.UnitTests;

/// <summary>
/// How the financials page words a figure.
/// </summary>
public class FinancialsTests
{
    [Theory]
    [InlineData(12, "+12.00 USD")]
    [InlineData(-3.5, "−3.50 USD")]
    [InlineData(0, "0.00 USD")]
    public void A_figure_that_can_be_a_loss_says_its_sign(decimal amount, string expected) =>
        Assert.Equal(expected, Financials.Signed(amount, "USD"));
}
