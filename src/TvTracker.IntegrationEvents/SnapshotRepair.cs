namespace TvTracker.IntegrationEvents;

/// <summary>
/// How a service repairs its copy of something a member did (their watched episodes, their follows) from the owning
/// service's snapshot of it (<see cref="TrackingSnapshotIntegrationEvent"/>): one rule for every copy that keeps a row
/// for each thing done. (Recommendations keeps a count per show instead, and sets it from the snapshot whole.)
/// <list type="bullet">
/// <item><b>Missing:</b> in the snapshot but not the copy (an event was lost). Added.</item>
/// <item><b>Gone:</b> in the copy but not the snapshot, and recorded before the snapshot was taken (the removal was
/// lost). Removed. A row recorded after the snapshot was taken is kept: the snapshot can't know about it.</item>
/// <item><b>Both:</b> in each; a copy may refresh what it holds from the snapshot's.</item>
/// </list>
/// "Recorded" is when the owning service heard of it, on its clock, not when the member says it happened: the phone's
/// offline queue backdates a watch, and one synced after the snapshot is newer than it whatever its watch time. So a
/// copy stamps a row from an event with the event's <c>CreatedAt</c> and a row from a snapshot with its
/// <c>TakenAtUtc</c>, never with its own clock, which needn't agree with the owner's. An event for a row the copy
/// already has (watched again) restamps it (<see cref="Recorded"/>), or a snapshot taken before the event would take the
/// row for one whose removal was lost.
/// </summary>
public static class SnapshotRepair
{
    public sealed record Changes<TCopy, TSnapshot>(IReadOnlyList<TSnapshot> Missing, IReadOnlyList<TCopy> Gone,
        IReadOnlyList<(TCopy Copy, TSnapshot Snapshot)> Both);

    /// <param name="copies">What the service holds for the member.</param>
    /// <param name="snapshot">What the owner holds; the first of any repeated key counts.</param>
    /// <param name="recordedAt">When a copy's row was recorded (see the class); null for a row from before that was
    /// kept, which is never taken as gone.</param>
    public static Changes<TCopy, TSnapshot> Of<TCopy, TSnapshot, TKey>(IEnumerable<TCopy> copies, IEnumerable<TSnapshot> snapshot,
        Func<TCopy, TKey> copyKey, Func<TSnapshot, TKey> snapshotKey, Func<TCopy, DateTime?> recordedAt, DateTimeOffset takenAt)
        where TKey : notnull
    {
        var owned = snapshot.DistinctBy(snapshotKey).ToDictionary(snapshotKey);
        var held = new HashSet<TKey>();
        var (gone, both) = (new List<TCopy>(), new List<(TCopy, TSnapshot)>());
        foreach (var copy in copies)
        {
            var key = copyKey(copy);
            held.Add(key);
            if (owned.TryGetValue(key, out var match))
            {
                both.Add((copy, match));
            }
            else if (recordedAt(copy) < takenAt.UtcDateTime)
            {
                gone.Add(copy);
            }
        }
        return new Changes<TCopy, TSnapshot>(owned.Values.Where(s => !held.Contains(snapshotKey(s))).ToList(), gone, both);
    }

    /// <summary>
    /// When a row the copy already holds counts as recorded after an event about it: the later of what it had and the
    /// event's time (an event delivered late doesn't make a row older).
    /// </summary>
    public static DateTime Recorded(DateTime held, DateTimeOffset eventAt) => held > eventAt.UtcDateTime ? held : eventAt.UtcDateTime;
}
