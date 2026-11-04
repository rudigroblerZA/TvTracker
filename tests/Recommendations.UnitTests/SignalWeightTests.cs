using Recommendations.API.Data;
using Recommendations.API.Services;

namespace Recommendations.UnitTests;

/// <summary>
/// How much one of a user's shows counts towards their taste: a follow is 1, every 10 episodes watched another 1 (up to
/// 20 episodes), and a rating scales it, 5 stars doubling it and 2 stars or fewer removing it.
/// </summary>
public class SignalWeightTests
{
    private static UserShowSignal Signal(bool followed = false, int episodes = 0, int? rating = null) =>
        new() { UserId = "u", ShowId = Guid.NewGuid(), Followed = followed, EpisodesWatched = episodes, Rating = rating };

    [Fact]
    public void Follows_and_episodes_add_up()
    {
        Assert.Equal(1, RecommendationEngine.Weight(Signal(followed: true)), 6);
        Assert.Equal(2, RecommendationEngine.Weight(Signal(followed: true, episodes: 10)), 6);
        Assert.Equal(0.5, RecommendationEngine.Weight(Signal(episodes: 5)), 6);
    }

    [Fact]
    public void Episodes_count_up_to_twenty()
    {
        Assert.Equal(RecommendationEngine.Weight(Signal(followed: true, episodes: 20)),
            RecommendationEngine.Weight(Signal(followed: true, episodes: 200)), 6);
    }

    [Fact]
    public void A_rating_scales_the_weight()
    {
        Assert.Equal(2, RecommendationEngine.Weight(Signal(followed: true, rating: 5)), 6);
        Assert.Equal(2.0 / 3, RecommendationEngine.Weight(Signal(followed: true, rating: 3)), 6);
        Assert.Equal(0, RecommendationEngine.Weight(Signal(followed: true, rating: 2)), 6);
        Assert.True(RecommendationEngine.Weight(Signal(followed: true, rating: 1)) < 0);
    }
}
