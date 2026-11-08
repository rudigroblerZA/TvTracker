using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.API.Services;

namespace Assistant.UnitTests;

/// <summary>
/// Ask the app's guards, on the mistakes CONTEXT.md records qwen3 0.6B making: discover_list without its list, the list
/// "drama", a made-up id, and links matched as whole words so "Lost" isn't found in "lostness".
/// </summary>
public class GuardTests
{
    private static readonly JsonElement DiscoverList = JsonDocument.Parse("""
        {"type":"object","required":["list"],"properties":{"list":{"type":"string","enum":["hidden-gems","consistent","best-finales"]},"genre":{"type":"string"}}}
        """).RootElement;

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_required_argument_left_out_or_blank_is_named()
    {
        Assert.Equal(["list"], AnswerLoop.Missing(DiscoverList, Args("""{"genre":"Drama"}""")));
        Assert.Equal(["list"], AnswerLoop.Missing(DiscoverList, Args("""{"list":"  "}""")));
        Assert.Equal(["list"], AnswerLoop.Missing(DiscoverList, null));
        Assert.Empty(AnswerLoop.Missing(DiscoverList, Args("""{"list":"hidden-gems"}""")));
    }

    [Fact]
    public void A_value_outside_the_enum_is_refused_with_the_allowed_ones()
    {
        var problem = AnswerLoop.NotAllowed(DiscoverList, Args("""{"list":"drama"}"""));

        Assert.NotNull(problem);
        Assert.Contains("\"drama\"", problem);
        Assert.Contains("hidden-gems, consistent, best-finales", problem);
        Assert.Null(AnswerLoop.NotAllowed(DiscoverList, Args("""{"list":"Hidden-Gems","genre":"anything"}""")));
    }

    [Fact]
    public void An_id_no_tool_returned_is_caught()
    {
        var known = Guid.NewGuid();

        Assert.Equal("12345678-9012-3456-7890-123456789012",
            AnswerLoop.UnknownId(Args("""{"personId":"12345678-9012-3456-7890-123456789012"}"""), [known]));
        Assert.Null(AnswerLoop.UnknownId(Args($$"""{"showId":"{{known}}","q":"Severance"}"""), [known]));
    }

    [Fact]
    public void Only_whole_word_mentions_of_shows_a_tool_named_are_linked_in_answer_order()
    {
        var lost = new ShowRef(Guid.NewGuid(), "Lost");
        var dark = new ShowRef(Guid.NewGuid(), "Dark");
        var severance = new ShowRef(Guid.NewGuid(), "Severance");

        var mentioned = AnswerLoop.Mentioned("A sense of lostness runs through Severance, and Dark too.", [lost, dark, severance]);

        Assert.Equal([severance, dark], mentioned);
    }

    [Fact]
    public void A_compacted_result_drops_ids_urls_nulls_and_empties_but_keeps_the_shows_it_named()
    {
        var id = Guid.NewGuid();
        var output = Toolbox.Compact($$"""
            {"shows":[{"id":"{{id}}","title":"Severance","posterUrl":"https://img/x.jpg","rating":8.4,"network":null,"tags":[],"summary":""}]}
            """);

        Assert.Equal(new ShowRef(id, "Severance"), Assert.Single(output.Shows));
        Assert.DoesNotContain(id.ToString(), output.Text);
        Assert.DoesNotContain("posterUrl", output.Text);
        Assert.DoesNotContain("network", output.Text);
        Assert.DoesNotContain("tags", output.Text);
        Assert.Contains("\"rating\":8.4", output.Text);
        Assert.Contains(id, output.Ids!);
    }
}
