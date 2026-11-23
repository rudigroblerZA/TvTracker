using Ratings.API.Apis;
using Ratings.Data;
using Ratings.API.Services;

namespace Ratings.UnitTests;

public class CommentEditsTests
{
    [Fact]
    public void An_edit_never_lifts_what_flags_hid()
    {
        Assert.Equal(HiddenReasons.Flags, CommentsApi.HiddenAfterEdit(HiddenReasons.Flags, TextVerdict.Ok));
    }

    [Fact]
    public void New_words_that_need_review_hold_a_visible_comment()
    {
        Assert.Equal(HiddenReasons.Held, CommentsApi.HiddenAfterEdit(null, TextVerdict.Held));
    }

    [Fact]
    public void A_held_comment_shows_once_its_words_pass()
    {
        Assert.Null(CommentsApi.HiddenAfterEdit(HiddenReasons.Held, TextVerdict.Ok));
    }
}
