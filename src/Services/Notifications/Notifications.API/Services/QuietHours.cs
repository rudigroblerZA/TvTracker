using Notifications.API.Data;

namespace Notifications.API.Services;

public static class QuietHours
{
    /// <summary>
    /// The earliest time (UTC) a notification may be sent: <paramref name="nowUtc"/>, or the end of quiet hours
    /// in the user's timezone if they are in them now.
    /// </summary>
    public static DateTime NextAllowedUtc(NotificationPreferences prefs, DateTime nowUtc)
    {
        if (prefs.QuietHoursStart is not { } start || prefs.QuietHoursEnd is not { } end || start == end)
        {
            return nowUtc;
        }

        var tz = FindTimeZone(prefs.TimeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var time = TimeOnly.FromDateTime(local);

        var overnight = start > end; // e.g. 22:00 to 07:00
        var inQuietHours = overnight ? time >= start || time < end : time >= start && time < end;
        if (!inQuietHours)
        {
            return nowUtc;
        }

        var endDate = DateOnly.FromDateTime(local);
        if (overnight && time >= start)
        {
            endDate = endDate.AddDays(1);
        }

        var localEnd = endDate.ToDateTime(end, DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(localEnd, tz);
        }
        catch (ArgumentException)
        {
            // The end time falls in a daylight-saving gap; send an hour later instead.
            return TimeZoneInfo.ConvertTimeToUtc(localEnd.AddHours(1), tz);
        }
    }

    public static TimeZoneInfo FindTimeZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : TimeZoneInfo.Utc;
}
