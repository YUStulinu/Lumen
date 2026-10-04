using Lumen.Core.Text;

namespace Lumen.Core.Tests;

public class MarkdownParserTests
{
    [Fact]
    public void Parses_headings_paragraphs_and_rules()
    {
        IReadOnlyList<MdBlock> blocks = MarkdownParser.Parse("# Title\n\nFirst line\nsecond line\n\n---\n## Sub ##");

        Assert.Collection(
            blocks,
            b => Assert.Equal(1, Assert.IsType<MdHeading>(b).Level),
            b => Assert.Equal("First line\nsecond line", Text(Assert.IsType<MdParagraph>(b).Inlines)),
            b => Assert.IsType<MdRule>(b),
            b =>
            {
                MdHeading h = Assert.IsType<MdHeading>(b);
                Assert.Equal(2, h.Level);
                Assert.Equal("Sub", Text(h.Inlines));
            });
    }

    [Fact]
    public void Parses_bulleted_numbered_and_nested_lists()
    {
        IReadOnlyList<MdBlock> blocks = MarkdownParser.Parse("- one\n  - nested\n3. three\n* star");

        MdListItem[] items = blocks.Cast<MdListItem>().ToArray();
        Assert.Equal(4, items.Length);
        Assert.False(items[0].Ordered);
        Assert.Equal(1, items[1].Depth);
        Assert.True(items[2].Ordered);
        Assert.Equal(3, items[2].Number);
        Assert.Equal("star", Text(items[3].Inlines));
    }

    [Fact]
    public void Code_fences_keep_their_content_verbatim()
    {
        IReadOnlyList<MdBlock> blocks = MarkdownParser.Parse("```csharp\nvar x = **1**;\n# not a heading\n```\nafter");

        MdCodeBlock code = Assert.IsType<MdCodeBlock>(blocks[0]);
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = **1**;\n# not a heading", code.Code);
        Assert.True(code.IsClosed);
        Assert.IsType<MdParagraph>(blocks[1]);
    }

    [Fact]
    public void An_unclosed_fence_while_streaming_becomes_an_open_code_block()
    {
        IReadOnlyList<MdBlock> blocks = MarkdownParser.Parse("Here:\n```py\nprint(1)");

        MdCodeBlock code = Assert.IsType<MdCodeBlock>(blocks[^1]);
        Assert.False(code.IsClosed);
        Assert.Equal("print(1)", code.Code);
    }

    [Fact]
    public void Parses_bold_italic_code_and_links()
    {
        IReadOnlyList<MdInline> inlines = MarkdownParser.ParseInlines("a **bold** and *it* with `code` and [link](https://example.com)");

        Assert.Contains(inlines, i => i is MdText { Text: "bold", Bold: true });
        Assert.Contains(inlines, i => i is MdText { Text: "it", Italic: true });
        Assert.Contains(inlines, i => i is MdCode { Code: "code" });
        Assert.Contains(inlines, i => i is MdLink { Text: "link", Url: "https://example.com" });
    }

    [Theory]
    [InlineData("snake_case_name stays")]
    [InlineData("2 * 3 * 4")]
    [InlineData("unclosed **bold")]
    public void Does_not_invent_emphasis(string text)
    {
        IReadOnlyList<MdInline> inlines = MarkdownParser.ParseInlines(text);

        Assert.All(inlines, i => Assert.False(i is MdText { Bold: true } or MdText { Italic: true }));
        Assert.Equal(text, Text(inlines));
    }

    [Fact]
    public void Tables_are_kept_aligned_as_code()
    {
        IReadOnlyList<MdBlock> blocks = MarkdownParser.Parse("| a | b |\n|---|---|\n| 1 | 2 |");

        MdCodeBlock table = Assert.IsType<MdCodeBlock>(Assert.Single(blocks));
        Assert.Equal("table", table.Language);
        Assert.Equal(3, table.Code.Split('\n').Length);
    }

    [Fact]
    public void Never_throws_on_odd_input()
    {
        string[] inputs = ["", "*", "**", "`", "[", "[x](", "#", "1.", "```", "> ", "- ", "\\", "\r\n\r\n"];
        foreach (string input in inputs)
        {
            _ = MarkdownParser.Parse(input);
        }
    }

    private static string Text(IReadOnlyList<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdCode c => c.Code,
        MdLink l => l.Text,
        _ => "",
    }));
}
