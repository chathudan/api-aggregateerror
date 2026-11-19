using System.Text.Json.Nodes;

namespace ApiAggregator.Docs.Tests;

/// <summary>The checker itself: how examples are found and how outputs are compared.</summary>
public sealed class CheckerTests
{
    [Fact]
    public void A_marker_and_the_code_block_after_it_make_one_example()
    {
        string[] lines =
        [
            "Some text.",
            "<!-- check: run customer-overview --route id=C-10042 --query status=open expect=502 partial -->",
            "",
            "```json",
            "{ \"a\": 1 }",
            "```"
        ];

        var example = Assert.Single(DocExample.Parse("guide.md", lines));

        Assert.Equal(2, example.Line);
        Assert.Equal("run", example.Kind);
        Assert.Equal(["customer-overview", "--route", "id=C-10042", "--query", "status=open"], example.PlainArgs);
        Assert.Equal(502, example.ExpectedStatus);
        Assert.True(example.Partial);
        Assert.Equal("json", example.Language);
        Assert.Equal("{ \"a\": 1 }", example.Expected.Trim());
    }

    [Fact]
    public void A_marker_without_a_code_block_is_reported_with_its_line()
    {
        string[] lines = ["<!-- check: validate -->", "Just text."];

        var error = Assert.Throws<FormatException>(() => DocExample.Parse("guide.md", lines).ToList());

        Assert.Contains("guide.md:1", error.Message);
    }

    [Fact]
    public void A_marker_inside_a_code_block_is_only_an_illustration()
    {
        string[] lines =
        [
            "````markdown",
            "<!-- check: run customer-summary --route id=C-10042 -->",
            "```json",
            "{ \"name\": \"Alex Example\", ... }",
            "```",
            "````",
            "<!-- check: validate -->",
            "```text",
            "All good.",
            "```"
        ];

        var example = Assert.Single(DocExample.Parse("guide.md", lines));

        Assert.Equal("validate", example.Kind);
    }

    [Fact]
    public void Correlation_ids_may_differ_but_must_be_present()
    {
        var expected = JsonNode.Parse("""{ "code": "not_found", "correlationId": "3f2a..." }""");

        Assert.Empty(OutputComparer.CompareJson(expected, JsonNode.Parse("""{ "code": "not_found", "correlationId": "9c1b" }"""), partial: false));
        Assert.NotEmpty(OutputComparer.CompareJson(expected, JsonNode.Parse("""{ "code": "not_found" }"""), partial: false));
    }

    [Fact]
    public void An_extra_property_fails_unless_the_example_is_partial()
    {
        var expected = JsonNode.Parse("""{ "a": 1 }""");
        var actual = JsonNode.Parse("""{ "a": 1.0, "b": 2 }""");

        Assert.Single(OutputComparer.CompareJson(expected, actual, partial: false));
        Assert.Empty(OutputComparer.CompareJson(expected, actual, partial: true));
    }

    [Fact]
    public void Lists_must_have_the_same_items_in_the_same_order()
    {
        var differences = OutputComparer.CompareJson(JsonNode.Parse("[1, 2]"), JsonNode.Parse("[2, 1]"), partial: true);

        Assert.Equal(2, differences.Count);
    }

    [Fact]
    public void Text_lines_must_appear_in_order_ignoring_timings_and_spacing()
    {
        var output = "--- run 1: HTTP 200 in 74 ms\n    Succeeded a          200     37 ms  calls=1  GET http://x/a\n";

        Assert.Empty(OutputComparer.CompareText("--- run 1: HTTP 200 in 5 ms\nSucceeded a 200 9 ms calls=1 GET http://x/a", output));
        Assert.Single(OutputComparer.CompareText("Succeeded a 200 9 ms calls=1 GET http://x/a\n--- run 1: HTTP 200 in 5 ms", output));
    }
}
