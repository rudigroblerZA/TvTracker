using Assistant.API.Alerts;

namespace Assistant.UnitTests;

/// <summary>
/// Ask's queue alert: ten minutes for the oldest question, or ten waiting.
/// </summary>
public class AskQueueAlertTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fires_on_an_old_question_or_a_long_queue()
    {
        Assert.False(AskQueueAlert.Evaluate(0, null, Now).Firing);
        Assert.False(AskQueueAlert.Evaluate(3, Now.AddMinutes(-4), Now).Firing);
        Assert.True(AskQueueAlert.Evaluate(1, Now.AddMinutes(-11), Now).Firing);
        Assert.True(AskQueueAlert.Evaluate(10, Now.AddMinutes(-1), Now).Firing);
    }
}
