using Notifications.API.Data;
using Notifications.API.Services;

namespace Notifications.UnitTests;

/// <summary>
/// Alerts are held during quiet hours, in the user's time zone, and sent when they end.
/// </summary>
public class QuietHoursTests
{
    private static NotificationPreferences Quiet(int start, int end, string timeZone = "Europe/London") => new()
    {
        UserId = "u",
        QuietHoursStart = new TimeOnly(start, 0),
        QuietHoursEnd = new TimeOnly(end, 0),
        TimeZone = timeZone
    };

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Overnight_quiet_hours_hold_until_the_next_morning()
    {
        // London is on GMT in November.
        Assert.Equal(Utc(11, 4, 7), QuietHours.NextAllowedUtc(Quiet(22, 7), Utc(11, 3, 23)));
        Assert.Equal(Utc(11, 4, 7), QuietHours.NextAllowedUtc(Quiet(22, 7), Utc(11, 4, 3)));
    }

    [Fact]
    public void Outside_quiet_hours_sends_now()
    {
        Assert.Equal(Utc(11, 3, 12), QuietHours.NextAllowedUtc(Quiet(22, 7), Utc(11, 3, 12)));
        Assert.Equal(Utc(11, 3, 7), QuietHours.NextAllowedUtc(Quiet(22, 7), Utc(11, 3, 7)));
    }

    [Fact]
    public void Daytime_quiet_hours_end_the_same_day()
    {
        Assert.Equal(Utc(11, 3, 14), QuietHours.NextAllowedUtc(Quiet(13, 14), Utc(11, 3, 13, 30)));
    }

    [Fact]
    public void Quiet_hours_are_in_the_users_time_zone()
    {
        // 23:00 in New York (EST) is 04:00 UTC; quiet until 07:00 there, 12:00 UTC.
        Assert.Equal(Utc(11, 4, 12), QuietHours.NextAllowedUtc(Quiet(22, 7, "America/New_York"), Utc(11, 4, 4)));
        // 23:00 UTC is 18:00 in New York: not quiet.
        Assert.Equal(Utc(11, 3, 23), QuietHours.NextAllowedUtc(Quiet(22, 7, "America/New_York"), Utc(11, 3, 23)));
    }

    [Fact]
    public void No_quiet_hours_or_an_empty_range_sends_now()
    {
        Assert.Equal(Utc(11, 3, 23), QuietHours.NextAllowedUtc(new NotificationPreferences { UserId = "u" }, Utc(11, 3, 23)));
        Assert.Equal(Utc(11, 3, 23), QuietHours.NextAllowedUtc(Quiet(22, 22), Utc(11, 3, 23)));
    }

    [Fact]
    public void An_unknown_time_zone_is_read_as_UTC()
    {
        Assert.Equal(Utc(11, 4, 7), QuietHours.NextAllowedUtc(Quiet(22, 7, "Nowhere/Atlantis"), Utc(11, 3, 23)));
    }

    [Fact]
    public void An_end_in_a_daylight_saving_gap_sends_an_hour_later()
    {
        // New York skips 02:00-03:00 on 8 March 2026. Quiet 23:00-02:30 ends in the gap, so the alert goes at 03:30 EDT.
        var prefs = Quiet(23, 2, "America/New_York");
        prefs.QuietHoursEnd = new TimeOnly(2, 30);

        Assert.Equal(Utc(3, 8, 7, 30), QuietHours.NextAllowedUtc(prefs, Utc(3, 8, 4, 30)));
    }
}
