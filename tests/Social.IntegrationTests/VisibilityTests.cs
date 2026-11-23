using Microsoft.EntityFrameworkCore;
using Social.API.Apis;
using Social.Data;

namespace Social.IntegrationTests;

/// <summary>
/// Who can see whom (<see cref="Visibility"/>): blocks either way round, private profiles and the owner's "share my
/// activity" switch, as the queries run on Postgres.
/// </summary>
public sealed class VisibilityTests(SocialDatabase database) : IClassFixture<SocialDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string me = $"me-{Guid.NewGuid():N}";

    private string NewUser() => $"user-{Guid.NewGuid():N}";

    private static Profile ProfileOf(string id, bool isPrivate = false, bool share = true) =>
        new() { UserId = id, DisplayName = id, Private = isPrivate, ShareActivity = share };

    private async Task<Profile> AddAsync(Profile profile, params object[] related)
    {
        await using var db = database.Context();
        db.Profiles.Add(profile);
        db.AddRange(related);
        await db.SaveChangesAsync(Ct);
        return profile;
    }

    private async Task<Visibility.Access> AccessAsync(Profile? owner)
    {
        await using var db = database.Context();
        return await Visibility.AccessAsync(db, me, owner, Ct);
    }

    [Fact]
    public async Task Someone_who_does_not_exist_is_not_visible() =>
        Assert.Equal(new Visibility.Access(false, false, false), await AccessAsync(null));

    [Fact]
    public async Task A_user_sees_all_of_their_own_page_even_when_private_and_not_sharing()
    {
        var owner = new Profile { UserId = me, DisplayName = "me", Private = true, ShareActivity = false };

        Assert.Equal(new Visibility.Access(true, true, true), await AccessAsync(owner));
    }

    [Fact]
    public async Task A_public_sharing_profile_shows_everything()
    {
        var owner = await AddAsync(ProfileOf(NewUser()));

        Assert.Equal(new Visibility.Access(true, true, true), await AccessAsync(owner));
    }

    [Fact]
    public async Task A_public_profile_that_does_not_share_activity_shows_the_page_but_not_the_activity()
    {
        var owner = await AddAsync(ProfileOf(NewUser(), share: false));

        Assert.Equal(new Visibility.Access(true, true, false), await AccessAsync(owner));
    }

    [Fact]
    public async Task A_private_profile_shows_nothing_to_a_stranger()
    {
        var owner = await AddAsync(ProfileOf(NewUser(), isPrivate: true));

        Assert.Equal(new Visibility.Access(true, false, false), await AccessAsync(owner));
    }

    [Fact]
    public async Task A_pending_request_to_a_private_profile_shows_nothing()
    {
        var owner = ProfileOf(NewUser(), isPrivate: true);
        await AddAsync(owner, new UserFollow { FollowerId = me, FolloweeId = owner.UserId, Approved = false });

        Assert.Equal(new Visibility.Access(true, false, false), await AccessAsync(owner));
    }

    [Fact]
    public async Task An_approved_follower_sees_a_private_profile_and_its_activity()
    {
        var owner = ProfileOf(NewUser(), isPrivate: true);
        await AddAsync(owner, new UserFollow { FollowerId = me, FolloweeId = owner.UserId, Approved = true });

        Assert.Equal(new Visibility.Access(true, true, true), await AccessAsync(owner));
    }

    [Fact]
    public async Task An_approved_follower_of_a_private_profile_still_misses_activity_the_owner_does_not_share()
    {
        var owner = ProfileOf(NewUser(), isPrivate: true, share: false);
        await AddAsync(owner, new UserFollow { FollowerId = me, FolloweeId = owner.UserId, Approved = true });

        Assert.Equal(new Visibility.Access(true, true, false), await AccessAsync(owner));
    }

    [Fact]
    public async Task A_block_in_either_direction_hides_the_two_from_each_other()
    {
        var blockedByMe = ProfileOf(NewUser());
        await AddAsync(blockedByMe, new UserBlock { BlockerId = me, BlockedId = blockedByMe.UserId });
        var blocksMe = ProfileOf(NewUser());
        await AddAsync(blocksMe, new UserBlock { BlockerId = blocksMe.UserId, BlockedId = me });

        Assert.Equal(new Visibility.Access(false, false, false), await AccessAsync(blockedByMe));
        Assert.Equal(new Visibility.Access(false, false, false), await AccessAsync(blocksMe));
    }

    [Fact]
    public async Task A_block_wins_over_an_approved_follow()
    {
        var owner = ProfileOf(NewUser(), isPrivate: true);
        await AddAsync(owner, new UserFollow { FollowerId = me, FolloweeId = owner.UserId, Approved = true },
            new UserBlock { BlockerId = owner.UserId, BlockedId = me });

        Assert.Equal(new Visibility.Access(false, false, false), await AccessAsync(owner));
    }

    [Fact]
    public async Task Blocked_with_lists_both_directions()
    {
        var iBlocked = NewUser();
        var blockedMe = NewUser();
        var unrelated = NewUser();
        await AddAsync(ProfileOf(unrelated), new UserBlock { BlockerId = me, BlockedId = iBlocked }, new UserBlock { BlockerId = blockedMe, BlockedId = me });

        await using var db = database.Context();
        var blocked = await Visibility.BlockedWith(db, me).ToListAsync(Ct);

        Assert.Equal(new[] { blockedMe, iBlocked }.Order(), blocked.Order());
    }

    [Fact]
    public async Task The_feeds_authors_are_approved_followees_who_share_and_are_not_blocked()
    {
        var visible = NewUser();
        var notSharing = NewUser();
        var pending = NewUser();
        var blocked = NewUser();
        var noProfile = NewUser();
        await using (var setup = database.Context())
        {
            setup.Profiles.AddRange(ProfileOf(visible), ProfileOf(notSharing, share: false), ProfileOf(pending), ProfileOf(blocked));
            setup.Follows.AddRange(
                new UserFollow { FollowerId = me, FolloweeId = visible, Approved = true },
                new UserFollow { FollowerId = me, FolloweeId = notSharing, Approved = true },
                new UserFollow { FollowerId = me, FolloweeId = pending, Approved = false },
                new UserFollow { FollowerId = me, FolloweeId = blocked, Approved = true },
                new UserFollow { FollowerId = me, FolloweeId = noProfile, Approved = true });
            setup.Blocks.Add(new UserBlock { BlockerId = blocked, BlockedId = me });
            await setup.SaveChangesAsync(Ct);
        }

        await using var db = database.Context();
        var authors = await Visibility.VisibleFollowees(db, me).ToListAsync(Ct);

        Assert.Equal(visible, Assert.Single(authors));
    }
}
