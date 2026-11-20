using Ratings.API.Apis;

namespace Ratings.UnitTests;

public class CharacterVotesTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void Most_votes_come_first_and_ties_keep_one_order()
    {
        var ranked = CharacterVotesApi.Ranked([new(C, 2), new(A, 2), new(B, 5)]);

        Assert.Equal([B, A, C], ranked.Select(r => r.PersonId));
    }

    [Fact]
    public void Nobody_is_listed_without_a_vote()
    {
        Assert.Empty(CharacterVotesApi.Ranked([new(A, 0)]));
    }
}
