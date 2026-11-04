using Notifications.API.Data;
using Notifications.API.Services;

namespace Notifications.UnitTests;

/// <summary>
/// The weekly digest: due on the user's day from their hour, in their time zone, and written as text and HTML.
/// </summary>
public class DigestTests
{
    private static NotificationPreferences Prefs(string timeZone) =>
        new() { UserId = "u", WeeklyDigest = true, DigestDay = DayOfWeek.Sunday, DigestHour = 9, TimeZone = timeZone };

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Due_on_their_day_from_their_hour()
    {
        // Sunday 8 November 2026, 09:00 in New York (EST) is 14:00 UTC.
        Assert.True(DigestWorker.IsDue(Prefs("America/New_York"), Utc(8, 14)));
        Assert.True(DigestWorker.IsDue(Prefs("America/New_York"), Utc(8, 23)));
        Assert.False(DigestWorker.IsDue(Prefs("America/New_York"), Utc(8, 13, 59)));
    }

    [Fact]
    public void The_day_is_theirs_not_UTCs()
    {
        // Sunday 03:00 UTC is still Saturday evening in New York.
        Assert.False(DigestWorker.IsDue(Prefs("America/New_York"), Utc(8, 3)));
        // Sunday 22:00 UTC is Monday morning in Sydney.
        Assert.False(DigestWorker.IsDue(Prefs("Australia/Sydney"), Utc(8, 22)));
        Assert.True(DigestWorker.IsDue(Prefs("Australia/Sydney"), Utc(7, 22)));
    }

    [Fact]
    public void Html_encodes_text_and_links_to_the_web_app()
    {
        var digest = new Digest("Your week", [
            new DigestSection("Coming up this week", [
                new DigestLine("Tue 3 Nov: Law & Order S25E05 \"<Untitled>\"", "/shows/1"),
                new DigestLine("and 2 more", null)
            ])
        ]);

        var html = digest.Html("https://example.test/");

        Assert.Contains("<a href=\"https://example.test/shows/1\">Tue 3 Nov: Law &amp; Order S25E05 &quot;&lt;Untitled&gt;&quot;</a>", html);
        Assert.Contains("<li>and 2 more</li>", html);
        Assert.Equal("Coming up this week\n- Tue 3 Nov: Law & Order S25E05 \"<Untitled>\"\n- and 2 more", digest.Text());
        Assert.False(digest.IsEmpty);
        Assert.True(new Digest("Your week", []).IsEmpty);
    }
}
