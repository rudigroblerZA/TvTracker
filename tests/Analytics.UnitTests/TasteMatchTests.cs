namespace Analytics.UnitTests;

/// <summary>
/// Taste Match's rules: the score and its bounds, that order doesn't matter, and what a result may name.
/// </summary>
public class TasteMatchTests
{
    private static readonly Guid Crime1 = Id(1), Crime2 = Id(2), Comedy1 = Id(3), Comedy2 = Id(4), SciFi1 = Id(5), Reality1 = Id(6),
        Pick1 = Id(11), Pick2 = Id(12), Pick3 = Id(13), Pick4 = Id(14);

    private static readonly Dictionary<Guid, MatchShow> Shows = new()
    {
        [Crime1] = new("Crime One", "c1.jpg", ["Crime", "Drama"]),
        [Crime2] = new("Crime Two", null, ["Crime", "Drama"]),
        [Comedy1] = new("Comedy One", null, ["Comedy"]),
        [Comedy2] = new("Comedy Two", null, ["Comedy"]),
        [SciFi1] = new("Sci-Fi One", null, ["Science-Fiction", "Drama"]),
        [Reality1] = new("Reality One", null, ["Reality"]),
        [Pick1] = new("Pick One", null, ["Crime", "Comedy"]),
        [Pick2] = new("Pick Two", null, ["Comedy"]),
        [Pick3] = new("Pick Three", null, ["Crime"]),
        [Pick4] = new("Pick Four", null, ["Drama"])
    };

    private static IReadOnlyList<string> GenresOf(Guid id) => Shows[id].Genres;

    [Fact]
    public void The_score_is_the_weighted_parts_within_its_bounds()
    {
        // 0.5 of each is 0.5 in all, which the curve lifts: 0.5^0.6.
        Assert.Equal(66, TasteMatch.Score(alike: 0.5, kinds: 0.5, shared: 0.5));
        // 0.4 + 0.21 + 0.04 = 0.65.
        Assert.Equal(77, TasteMatch.Score(alike: 0.8, kinds: 0.7, shared: 0.2));
        Assert.True(TasteMatch.Score(0.3, 0.3, 0) < TasteMatch.Score(0.4, 0.3, 0));
        // Nothing in common is not an insult, and nobody is a perfect match.
        Assert.Equal(TasteMatch.MinScore, TasteMatch.Score(0, 0, 0));
        Assert.Equal(TasteMatch.MaxScore, TasteMatch.Score(1, 1, 1));
    }

    [Fact]
    public void Shared_is_a_share_of_the_smaller_side_so_three_picks_can_match_a_long_history()
    {
        var guest = TasteMatch.Picked([Crime1, Comedy1, SciFi1]);
        var member = Enumerable.Range(100, 300).ToDictionary(Id, _ => 0.5);
        member[Crime1] = 1;
        member[Comedy1] = 0.8;

        // 1 + 0.8 of the guest's 3: the member's other 300 shows don't dilute it.
        Assert.Equal(0.6, TasteMatch.Shared(guest, TasteMatch.Side(member)), 6);
        Assert.Equal(TasteMatch.MaxScored, TasteMatch.Side(member).Count);
        Assert.Equal(0, TasteMatch.Shared(guest, TasteMatch.Picked([Reality1])));
    }

    [Fact]
    public void Kinds_compares_genre_shares_not_counts()
    {
        var crime = TasteMatch.GenreShares(new Dictionary<Guid, double> { [Crime1] = 1 }, GenresOf);
        var moreCrime = TasteMatch.GenreShares(new Dictionary<Guid, double> { [Crime1] = 1, [Crime2] = 0.5 }, GenresOf);
        var reality = TasteMatch.GenreShares(new Dictionary<Guid, double> { [Reality1] = 1 }, GenresOf);

        Assert.Equal(1, crime.Values.Sum(), 6);
        Assert.Equal(1, TasteMatch.Kinds(crime, moreCrime), 6);
        Assert.Equal(0, TasteMatch.Kinds(crime, reality), 6);
        Assert.Equal(0, TasteMatch.Kinds(crime, new Dictionary<string, double>()));
    }

    [Fact]
    public void The_score_the_shared_shows_and_the_genres_are_the_same_whichever_side_is_first()
    {
        var a = new Dictionary<Guid, double> { [Crime1] = 1, [Crime2] = 0.8, [Comedy1] = 0.5 };
        var b = new Dictionary<Guid, double> { [Crime1] = 0.8, [Comedy1] = 1, [Comedy2] = 1, [SciFi1] = 0.5 };

        var forwards = TasteMatch.Of("token", a, b, new HashSet<Guid>(), 0.6, [], [], [], false, Shows);
        var backwards = TasteMatch.Of("token", b, a, new HashSet<Guid>(), 0.6, [], [], [], false, Shows);

        Assert.Equal(forwards.Score, backwards.Score);
        Assert.Equal(forwards.Line, backwards.Line);
        Assert.Equal(forwards.BothLove, backwards.BothLove);
        Assert.Equal(forwards.BothInto, backwards.BothInto);
        Assert.Equal((forwards.Differ!.Inviter, forwards.Differ.Friend), (backwards.Differ!.Friend, backwards.Differ.Inviter));
    }

    [Fact]
    public void A_show_someone_asked_not_to_have_named_still_counts_and_is_never_named()
    {
        var a = new Dictionary<Guid, double> { [Crime1] = 1, [Comedy1] = 1 };
        var b = new Dictionary<Guid, double> { [Crime1] = 1, [Comedy1] = 1 };

        var open = TasteMatch.Of("token", a, b, new HashSet<Guid>(), 0.9, [], [], [], false, Shows);
        var hidden = TasteMatch.Of("token", a, b, new HashSet<Guid> { Crime1 }, 0.9, [], [], [], false, Shows);

        Assert.Equal(open.Score, hidden.Score);
        Assert.Equal(["Comedy One", "Crime One"], open.BothLove.Select(s => s.Title).Order());
        Assert.Equal(["Comedy One"], hidden.BothLove.Select(s => s.Title));
    }

    [Fact]
    public void Only_shared_shows_are_named_never_one_sides_own()
    {
        var a = new Dictionary<Guid, double> { [Crime1] = 1, [Crime2] = 1, [SciFi1] = 1 };
        var b = new Dictionary<Guid, double> { [Crime1] = 1, [Comedy1] = 1, [Reality1] = 1 };

        var match = TasteMatch.Of("token", a, b, new HashSet<Guid>(), 0.5, [], [], [Pick1, Pick2, Pick3], false, Shows);

        Assert.Equal([Crime1], match.BothLove.Select(s => s.ShowId));
        var named = match.BothLove.Select(s => s.ShowId).Concat(match.WatchTogether.Select(p => p.ShowId)).ToHashSet();
        Assert.DoesNotContain(Crime2, named);
        Assert.DoesNotContain(Comedy1, named);
    }

    [Fact]
    public void Suggestions_say_what_they_take_from_each_side_and_too_few_are_not_shown()
    {
        var a = new Dictionary<Guid, double> { [Crime1] = 1 };
        var b = new Dictionary<Guid, double> { [Comedy1] = 1 };

        var match = TasteMatch.Of("token", a, b, new HashSet<Guid>(), 0.3, [Crime1], [Comedy1], [Pick1, Pick2, Pick3, Crime1], true, Shows);
        var few = TasteMatch.Of("token", a, b, new HashSet<Guid>(), 0.3, [Crime1], [Comedy1], [Pick1, Pick2], true, Shows);

        // A show one of them already loves is never a suggestion.
        Assert.Equal([Pick1, Pick2, Pick3], match.WatchTogether.Select(p => p.ShowId));
        Assert.Equal(["Crime"], match.WatchTogether[0].FromInviter);
        Assert.Equal(["Comedy"], match.WatchTogether[0].FromFriend);
        Assert.True(match.Loose);
        Assert.Empty(few.WatchTogether);
    }

    [Theory]
    [InlineData(99, 90)]
    [InlineData(90, 90)]
    [InlineData(89, 75)]
    [InlineData(55, 55)]
    [InlineData(54, 35)]
    [InlineData(15, 0)]
    public void A_scores_line_is_its_bands_and_the_same_every_time_for_a_match(int score, int band)
    {
        var line = TasteMatch.Line(score, "a-token");

        Assert.Equal(line, TasteMatch.Line(score, "a-token"));
        Assert.Equal(line, TasteMatch.Line(band, "a-token"));
        Assert.False(string.IsNullOrWhiteSpace(line));
    }

    [Fact]
    public void Different_matches_can_get_different_lines_of_a_band()
    {
        var lines = Enumerable.Range(0, 40).Select(i => TasteMatch.Line(80, $"token-{i}")).Distinct().ToList();

        Assert.True(lines.Count > 1);
    }

    [Fact]
    public void A_difference_needs_a_clear_gap_on_both_sides()
    {
        var crime = new Dictionary<string, double> { ["Crime"] = 0.7, ["Drama"] = 0.3 };
        var comedy = new Dictionary<string, double> { ["Comedy"] = 0.7, ["Drama"] = 0.3 };

        Assert.Equal(new MatchDifferenceDto("Crime", "Comedy"), TasteMatch.Differ(crime, comedy));
        Assert.Null(TasteMatch.Differ(crime, crime));
    }

    [Fact]
    public void A_member_sees_their_heaviest_shows_as_what_could_be_named()
    {
        var side = Enumerable.Range(1, 20).ToDictionary(Id, i => i <= 2 ? 1.0 : 0.5);

        var nameable = TasteMatch.Nameable(side);

        Assert.Equal(TasteMatch.MaxNameable, nameable.Count);
        // The heaviest first; those that weigh the same by id, so the list is the same every time.
        Assert.Equal(Enumerable.Range(1, TasteMatch.MaxNameable).Select(Id), nameable);
    }

    [Fact]
    public void The_card_pictures_what_both_love_or_else_what_they_could_watch_together_never_ones_own()
    {
        var a = new Dictionary<Guid, double> { [Crime1] = 1, [Crime2] = 1 };
        var shared = TasteMatch.Of("token", a, new Dictionary<Guid, double> { [Crime1] = 1, [Comedy1] = 1 }, new HashSet<Guid>(), 0.5, [], [],
            [Pick1, Pick2, Pick3], false, Shows);
        var apart = TasteMatch.Of("token", a, new Dictionary<Guid, double> { [Comedy1] = 1, [Comedy2] = 1 }, new HashSet<Guid>(), 0.2, [], [],
            [Pick1, Pick2, Pick3, Pick4], false, Shows);

        var (both, bothLabel) = TasteMatch.CardShows(shared);
        var (together, togetherLabel) = TasteMatch.CardShows(apart);

        Assert.Equal([Crime1], both.Select(s => s.ShowId));
        Assert.Equal("You both love", bothLabel);
        Assert.Equal("Watch together", togetherLabel);
        Assert.Equal([Pick1, Pick2, Pick3, Pick4], together.Select(s => s.ShowId));
        Assert.DoesNotContain(together, s => a.ContainsKey(s.ShowId));
    }

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");
}
