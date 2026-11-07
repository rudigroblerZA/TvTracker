using Catalog.API.Apis;

namespace Catalog.UnitTests;

/// <summary>
/// The show-page facts found in the catalog's own data, with CONTEXT.md's cases: title formulas (Friends' "The One",
/// numbered chapters, placeholders), characters' names (titles and role words out) and episode-length verdicts (a sitcom
/// from 22 to 26 minutes stays steady).
/// </summary>
public class ShowFactsTests
{
    private static readonly Guid Show = Guid.NewGuid();

    private static TitleFormulaDto? Formula(params string[] titles) =>
        TitleFormulasApi.Formula(Show, titles.Select((t, i) => new TitleFormulasApi.TitleRow(Guid.NewGuid(), Show, 1, i + 1, t)));

    [Fact]
    public void Friends_follows_the_one_rather_than_the_or_the_one_where()
    {
        var titles = Enumerable.Range(1, 10).Select(i => $"The One Where Ross Does Thing {i}")
            .Concat(Enumerable.Range(1, 9).Select(i => $"The One With the Thing {i}"))
            .Append("The Last One")
            .ToArray();

        var formula = Formula(titles)!;

        // "The" alone is an article and weighs half; "The One Where" covers only half the titles.
        Assert.Equal("phrase", formula.Kind);
        Assert.Equal("The One", formula.Prefix);
        Assert.Equal(19, formula.Matching);
        Assert.Equal("The Last One", Assert.Single(formula.Exceptions).Title);
    }

    [Fact]
    public void Chapters_numbered_in_hyphenated_words_are_numbered_and_a_possessive_follows_its_name()
    {
        string[] numbers = ["One", "Two", "Three", "Four", "Five", "Twenty-One", "Twenty-Two"];
        var chapters = Formula(numbers.Select(n => $"Chapter {n}: Something Happens").ToArray())!;
        Assert.Equal("numbered", chapters.Kind);
        Assert.Equal("Chapter", chapters.Prefix);

        var monk = Formula("Mr. Monk and the Candidate", "Mr. Monk Goes to the Carnival", "Mr. Monk Meets Dale", "Mr. Monk's 100th Case",
            "Mr. Monk Takes a Vacation", "Mr. Monk and the Airplane")!;
        Assert.Equal(6, monk.Matching);
        Assert.Equal("Mr. Monk", monk.Prefix);
    }

    [Fact]
    public void Placeholders_are_not_titles_and_too_few_real_ones_have_no_formula()
    {
        // Five real titles that follow, and TVmaze's stand-ins that would otherwise make a sixth and a seventh.
        Assert.Null(Formula("The Thing A", "The Thing B", "The Thing C", "The Thing D", "The Thing E", "Episode 6", "TBA", "Untitled"));
        Assert.Null(Formula("Pilot", "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"));
    }

    [Theory]
    [InlineData("Dr. Gregory House", "Gregory", "House")]
    [InlineData("DCI John Luther", "John", "Luther")]
    [InlineData("Assistant District Attorney Alexandra Cabot", "Alexandra", "Cabot")]
    [InlineData("Jimmy \"Slippin' Jimmy\" McGill / Saul Goodman", "Jimmy", "McGill")]
    [InlineData("Robert Baratheon Jr.", "Robert", "Baratheon")]
    [InlineData("Mary-Kate O'Brien", "Mary-Kate", "O'Brien")]
    [InlineData("MARY-KATE O'BRIEN", "Mary-Kate", "O'Brien")]    // shouted, then written as names are
    [InlineData("KA'ALA D'ANGELO", "Ka'ala", "D'Angelo")]        // a capital after an apostrophe only behind one letter
    public void A_character_reads_as_first_and_last_name(string character, string first, string last) =>
        Assert.Equal((first, last), CharacterNamesApi.Parse(character));

    [Fact]
    public void A_name_is_shown_as_most_credits_spell_it()
    {
        // A credit in capitals reads "Deandre"; most write "DeAndre", and that's the one spelling.
        var spellings = CharacterNamesApi.Spellings(["Deandre", "DeAndre", "DeAndre", "Jimmy"]);
        Assert.Equal("DeAndre", spellings[CharacterNamesApi.Key("DEANDRE")]);
        Assert.Equal(2, spellings.Count);
        // A tie is settled the same way every time, whatever order the rows came in.
        Assert.Equal(CharacterNamesApi.Spellings(["McNulty", "Mcnulty"])["mcnulty"], CharacterNamesApi.Spellings(["Mcnulty", "McNulty"])["mcnulty"]);
    }

    [Theory]
    [InlineData("Bartender")]       // one word can't be told from a name
    [InlineData("Eleven")]
    [InlineData("Security Guard")]  // a role word first
    [InlineData("Unnamed Woman")]
    [InlineData("Doctor Who")]      // a title and then one word
    [InlineData("the narrator")]
    public void A_role_is_not_a_name(string character) => Assert.Null(CharacterNamesApi.Parse(character));

    private static EpisodeLengthsDto? Lengths(params (int Season, double Median, int Timed)[] seasons) =>
        EpisodeLengthsApi.Build(Show, seasons.Select(s => new EpisodeLengthsApi.SeasonRow(Show, s.Season, 10, s.Timed, s.Median, 0)).ToList());

    [Fact]
    public void A_length_change_needs_a_fifth_and_eight_minutes()
    {
        // The Office, 26 to 30: under eight minutes, so steady. A half-hour sitcom from 22 to 26 likewise.
        Assert.Equal("steady", Lengths((1, 26, 10), (9, 30, 10))!.Verdict);
        Assert.Equal("steady", Lengths((1, 22, 10), (2, 26, 10))!.Verdict);
        // Stranger Things, 52 to 70.
        Assert.Equal("longer", Lengths((1, 52, 8), (4, 70, 9))!.Verdict);
        Assert.Equal("shorter", Lengths((1, 60, 10), (2, 44, 10))!.Verdict);
    }

    [Fact]
    public void Seasons_with_too_few_runtimes_dont_count_and_the_biggest_jump_is_named_from_ten_minutes()
    {
        // Game of Thrones' last season, 60 to 79; a two-episode season with runtimes isn't counted.
        var got = Lengths((1, 57, 10), (2, 55, 2), (7, 60, 7), (8, 79, 6))!;
        Assert.Equal(57, got.FirstMinutes);
        Assert.Equal(new SeasonJumpDto(7, 8, 60, 79), got.Jump);
        Assert.False(got.Seasons.Single(s => s.Number == 2).Counted);

        Assert.Null(Lengths((1, 30, 10), (2, 45, 2)));
        Assert.Null(Lengths((1, 30, 10), (2, 35, 10))!.Jump);
    }
}
