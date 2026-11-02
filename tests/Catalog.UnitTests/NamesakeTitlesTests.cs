using Catalog.SyncWorker.Namesakes;

namespace Catalog.UnitTests;

/// <summary>
/// Same name, and history imports, compare titles by <see cref="NamesakeTitles.Key"/>.
/// </summary>
public class NamesakeTitlesTests
{
    [Theory]
    [InlineData("Ghosts (US)", "Ghosts")]
    [InlineData("Ghosts (2021)", "Ghosts")]
    [InlineData("Law & Order", "Law and Order")]
    [InlineData("Grey's Anatomy", "GREY'S ANATOMY")]
    [InlineData("  The   Office  ", "The Office")]
    [InlineData("Doctor Who!", "doctor who")]
    public void Same_title(string a, string b) => Assert.Equal(NamesakeTitles.Key(a), NamesakeTitles.Key(b));

    [Theory]
    [InlineData("Doctor Who: The Movie", "Doctor Who")]
    [InlineData("The Office", "Office")]
    [InlineData("Ghosts (US) Revisited", "Ghosts")]
    public void Different_title(string a, string b) => Assert.NotEqual(NamesakeTitles.Key(a), NamesakeTitles.Key(b));
}
