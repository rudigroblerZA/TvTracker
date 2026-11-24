using Identity.Data;

namespace Identity.UnitTests;

public class ProfileAvatarTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    public void A_picture_is_known_by_how_its_file_begins(byte[] bytes, string kind) =>
        Assert.Equal(kind, ProfileAvatar.ContentTypeOf(bytes));

    [Theory]
    // SVG can carry script, so it is not taken, whatever a request calls it.
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><body>hello</body></html>")]
    [InlineData("GIF89a")]
    [InlineData("RIFFxxxxWAVE")]
    [InlineData("")]
    public void Anything_else_is_not_a_picture(string content) =>
        Assert.Null(ProfileAvatar.ContentTypeOf(System.Text.Encoding.ASCII.GetBytes(content)));

    [Fact]
    public void An_uploaded_picture_is_shown_before_one_elsewhere_and_its_address_changes_with_each_upload()
    {
        var profile = new UserProfile { UserId = "u1", DisplayName = "Ann" };
        Assert.Null(profile.Avatar);

        profile.AvatarUrl = "https://example.com/ann.png";
        Assert.Equal("https://example.com/ann.png", profile.Avatar);

        profile.AvatarVersion = 100;
        Assert.Equal("avatars/u1?v=100", profile.Avatar);
        profile.AvatarVersion = 200;
        Assert.Equal("avatars/u1?v=200", profile.Avatar);

        // Taking the upload away brings the address back.
        profile.AvatarVersion = null;
        Assert.Equal("https://example.com/ann.png", profile.Avatar);
    }
}
