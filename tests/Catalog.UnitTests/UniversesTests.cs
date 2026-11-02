using Catalog.API.Services;

namespace Catalog.UnitTests;

/// <summary>
/// The matching rules behind universes, the character trail and reunions (see CONTEXT.md, Universes).
/// </summary>
public class UniversesTests
{
    [Theory]
    [InlineData("Frasier Crane", "Dr. Frasier Crane")]
    [InlineData("Frank Castle / The Punisher", "Frank Castle")]
    [InlineData("Kelly Severide", "Lt. Kelly Severide")]
    [InlineData("Jethro \"Gibbs\" Gibbs", "Jethro Gibbs")]
    [InlineData("Matt Murdock / Daredevil", "Daredevil")]
    public void Same_character(string a, string b)
    {
        Assert.True(Universes.SameCharacter(a, b));
        Assert.True(Universes.SameCharacter(b, a));
    }

    [Theory]
    // One word inside a fuller name isn't enough: "Frasier" alone could be anyone.
    [InlineData("Frasier", "Frasier Crane")]
    [InlineData("Jesse Pinkman", "Jane Margolis")]
    [InlineData("", "Jesse Pinkman")]
    public void Different_character(string a, string b) => Assert.False(Universes.SameCharacter(a, b));

    [Theory]
    [InlineData("NCIS: Sydney", "NCIS", 0)]
    [InlineData("NCIS: Origins", "NCIS: Hawai'i", 0)]
    [InlineData("Star Trek: Picard", "Star Trek: Lower Decks", 0)]
    [InlineData("Chicago Fire", "Chicago Med", 2)]
    public void Same_franchise(string a, string b, int sharedMakers)
    {
        Assert.True(Universes.SameFranchise(a, b, sharedMakers));
        Assert.True(Universes.SameFranchise(b, a, sharedMakers));
    }

    [Theory]
    // One shared maker is a producer's brand, not a franchise.
    [InlineData("Chicago Fire", "Chicago Hope", 1)]
    [InlineData("Chicago Fire", "Chicago Hope", 0)]
    // Same-title pairs are left to the cast: two unrelated shows can share a title.
    [InlineData("Ghosts", "Ghosts", 5)]
    [InlineData("Fargo", "Fargo", 0)]
    public void Different_franchise(string a, string b, int sharedMakers) => Assert.False(Universes.SameFranchise(a, b, sharedMakers));

    [Theory]
    [InlineData("Himself", "Jimmy Fallon")]
    [InlineData("Host", "Kelly Clarkson")]
    [InlineData("Jimmy Fallon", "Jimmy Fallon")]
    public void Self_role(string character, string actor) => Assert.True(Universes.IsSelfRole(character, actor));

    [Theory]
    [InlineData("Walter White", "Bryan Cranston")]
    [InlineData("Jimmy McGill", "Bob Odenkirk")]
    public void Not_a_self_role(string character, string actor) => Assert.False(Universes.IsSelfRole(character, actor));
}
