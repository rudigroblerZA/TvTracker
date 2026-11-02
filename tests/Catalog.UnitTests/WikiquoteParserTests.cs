using Catalog.SyncWorker.Quotes;

namespace Catalog.UnitTests;

/// <summary>Memorable lines from Wikiquote's wikitext (see CONTEXT.md, Memorable lines).</summary>
public class WikiquoteParserTests
{
    private const string Page = """
        {{TOCright}}
        '''Breaking Bad''' is an American crime drama.

        == Season 1 ==
        === ''Pilot'' [1.01] ===
        :'''Walter White''': Chemistry is the study of change.
        :'''Jesse Pinkman''': Yeah, [[science]]!
        :''[Walt turns away.]''
        <hr width="50%"/>
        * I am not in danger. I am the danger.
        ** Said to Skyler.
        === ''Cat's in the Bag...'' [1.02] ===
        :'''Walter:''' Say my name.

        == Dialogue ==
        :'''Hank''': Ding dong.
        :'''Marie''': Hank!

        == Cast ==
        * '''Bryan Cranston''' - Walter White

        == About Breaking Bad ==
        === Dialogue ===
        :'''Interviewer''': Why chemistry?
        :'''Vince Gilligan''': Change.
        """;

    [Fact]
    public void Episode_exchanges_carry_their_numbers()
    {
        var quotes = WikiquoteParser.Parse(Page);

        var pilot = quotes.Where(q => q is { Season: 1, Episode: 1 }).ToList();
        Assert.Equal(2, pilot.Count);
        Assert.Equal([("Walter White", "Chemistry is the study of change."), ("Jesse Pinkman", "Yeah, science!"), ("", "[Walt turns away.]")],
            pilot[0].Lines);
        Assert.Equal([("", "I am not in danger. I am the danger.")], pilot[1].Lines);

        Assert.Equal([("Walter", "Say my name.")], Assert.Single(quotes, q => q is { Season: 1, Episode: 2 }).Lines);
    }

    [Fact]
    public void Show_dialogue_counts_but_cast_lists_and_interviews_do_not()
    {
        var quotes = WikiquoteParser.Parse(Page);

        var show = Assert.Single(quotes, q => q.Season is null);
        Assert.Equal([("Hank", "Ding dong."), ("Marie", "Hank!")], show.Lines);
        Assert.DoesNotContain(quotes, q => q.Lines.Any(l => l.Speaker is "Interviewer" or "Vince Gilligan" || l.Text.Contains("Bryan Cranston")));
    }

    [Fact]
    public void Long_exchanges_are_left_out()
    {
        var page = $"""
            === ''Pilot'' [1.01] ===
            :'''Walter''': {new string('a', WikiquoteParser.MaxCharacters)}
            """;

        Assert.Empty(WikiquoteParser.Parse(page));
    }

    [Fact]
    public void Episodes_headed_by_title_alone_are_numbered_in_page_order()
    {
        const string page = """
            == Series 2 ==
            === A Scandal in Belgravia ===
            :'''Sherlock''': Brainy's the new sexy.
            === The Hounds of Baskerville ===
            :'''John''': Fine.
            """;

        var quotes = WikiquoteParser.Parse(page);

        Assert.Equal([(2, 1), (2, 2)], quotes.Select(q => (q.Season!.Value, q.Episode!.Value)));
    }

    [Theory]
    [InlineData("[[Walter White|Walt]] said ''no''", "Walt said no")]
    [InlineData("Tight<ref>Season 1 DVD</ref> &amp; clean", "Tight & clean")]
    [InlineData("A{{citation needed}} line<br />two", "A line two")]
    public void Clean_leaves_plain_text(string wikitext, string text) => Assert.Equal(text, WikiquoteParser.Clean(wikitext));

    [Fact]
    public void Season_pages_are_the_shows_own()
    {
        const string page = "See [[Breaking Bad (season 1)]], [[Breaking_Bad_(season_2)|season 2]], [[Better Call Saul (season 1)]] and [[Breaking Bad (season 1)]].";

        Assert.Equal(["Breaking Bad (season 1)", "Breaking Bad (season 2)"], WikiquoteParser.SeasonPages(page, "Breaking_Bad"));
    }
}
