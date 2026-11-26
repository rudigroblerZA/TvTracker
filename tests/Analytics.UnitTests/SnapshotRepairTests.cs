using TvTracker.IntegrationEvents;

namespace Analytics.UnitTests;

/// <summary>
/// The one rule every service repairs its copy of a member's watches and follows by (<see cref="SnapshotRepair"/>).
/// </summary>
public class SnapshotRepairTests
{
    private static readonly DateTimeOffset TakenAt = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    private sealed record Copy(string Key, DateTime RecordedAtUtc, string Value = "");
    private sealed record Owned(string Key, string Value = "");

    private static SnapshotRepair.Changes<Copy, Owned> Repair(IEnumerable<Copy> copies, params Owned[] snapshot) =>
        SnapshotRepair.Of(copies, snapshot, c => c.Key, s => s.Key, c => c.RecordedAtUtc, TakenAt);

    private static Copy Before(string key) => new(key, TakenAt.UtcDateTime.AddHours(-1));

    [Fact]
    public void What_the_owner_has_and_the_copy_lacks_is_missing()
    {
        var changes = Repair([Before("a")], new Owned("a"), new Owned("b"));

        Assert.Equal(["b"], changes.Missing.Select(m => m.Key));
        Assert.Empty(changes.Gone);
    }

    [Fact]
    public void What_the_copy_has_and_the_owner_lacks_is_gone_when_it_was_recorded_before_the_snapshot()
    {
        var changes = Repair([Before("a"), Before("b")], new Owned("a"));

        Assert.Equal(["b"], changes.Gone.Select(g => g.Key));
        Assert.Empty(changes.Missing);
    }

    [Fact]
    public void A_row_recorded_after_the_snapshot_was_taken_is_kept()
    {
        // A watch synced a minute after the snapshot was read, and one recorded at that very moment.
        var changes = Repair([new Copy("late", TakenAt.UtcDateTime.AddMinutes(1)), new Copy("same", TakenAt.UtcDateTime)]);

        Assert.Empty(changes.Gone);
    }

    [Fact]
    public void What_both_have_is_paired_so_the_copy_can_refresh_what_it_holds()
    {
        var changes = Repair([new Copy("a", TakenAt.UtcDateTime.AddDays(-2), "old")], new Owned("a", "new"));

        var (copy, owned) = Assert.Single(changes.Both);
        Assert.Equal(("old", "new"), (copy.Value, owned.Value));
        Assert.Empty(changes.Missing);
        Assert.Empty(changes.Gone);
    }

    [Fact]
    public void A_key_the_snapshot_repeats_counts_once_as_its_first()
    {
        var changes = Repair([], new Owned("a", "first"), new Owned("a", "second"));

        Assert.Equal("first", Assert.Single(changes.Missing).Value);
    }

    [Fact]
    public void An_empty_snapshot_empties_an_old_copy()
    {
        var changes = Repair([Before("a"), Before("b")]);

        Assert.Equal(2, changes.Gone.Count);
    }
}
