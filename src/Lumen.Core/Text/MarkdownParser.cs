using System.Text;

namespace Lumen.Core.Text;

/// <summary>A block-level Markdown element.</summary>
public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <param name="Ordered">Numbered ("1.") rather than bulleted ("-").</param>
/// <param name="Number">The number of an ordered item.</param>
/// <param name="Depth">Nesting level, 0 for top-level items.</param>
public sealed record MdListItem(bool Ordered, int Number, int Depth, IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <param name="IsClosed">False while the closing ``` has not been streamed yet.</param>
public sealed record MdCodeBlock(string Language, string Code, bool IsClosed) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>An inline Markdown element.</summary>
public abstract record MdInline;

public sealed record MdText(string Text, bool Bold = false, bool Italic = false) : MdInline;

public sealed record MdCode(string Code) : MdInline;

public sealed record MdLink(string Text, string Url) : MdInline;

/// <summary>
/// A small, forgiving Markdown parser for the subset language models actually produce.
/// </summary>
/// <remarks>
/// The answer is re-rendered many times per second while it streams in, so the parser
/// must cope with incomplete input: an unclosed code fence simply becomes an open
/// code block, an unmatched "**" stays literal text. Tables are kept as monospaced
/// code blocks so their columns stay aligned. It never throws on any input.
/// </remarks>
public static class MarkdownParser
{
    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var blocks = new List<MdBlock>();
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MdParagraph(ParseInlines(string.Join('\n', paragraph))));
                paragraph.Clear();
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Fenced code block: everything until the closing fence is literal.
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                string fence = trimmed[..3];
                string language = trimmed[3..].Trim();
                var code = new StringBuilder();
                bool closed = false;
                for (i++; i < lines.Length; i++)
                {
                    if (lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
                    {
                        closed = true;
                        break;
                    }

                    if (code.Length > 0)
                    {
                        code.Append('\n');
                    }

                    code.Append(lines[i]);
                }

                blocks.Add(new MdCodeBlock(language, code.ToString(), closed));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // Table: consecutive lines starting with '|' are kept verbatim.
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var table = new StringBuilder(trimmed);
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('|'))
                {
                    table.Append('\n').Append(lines[++i].Trim());
                }

                blocks.Add(new MdCodeBlock("table", table.ToString(), IsClosed: true));
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MdRule());
                continue;
            }

            int hashes = CountLeading(trimmed, '#');
            if (hashes is >= 1 and <= 6 && trimmed.Length > hashes && trimmed[hashes] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MdHeading(hashes, ParseInlines(trimmed[(hashes + 1)..].Trim().TrimEnd('#').TrimEnd())));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MdQuote(ParseInlines(trimmed.TrimStart('>').Trim())));
                continue;
            }

            if (TryParseListItem(line, out MdListItem? item))
            {
                FlushParagraph();
                blocks.Add(item);
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    public static IReadOnlyList<MdInline> ParseInlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<MdInline>();
        ParseInlines(text, bold: false, italic: false, result);
        return result;
    }

    private static void ParseInlines(string text, bool bold, bool italic, List<MdInline> output)
    {
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length > 0)
            {
                output.Add(new MdText(plain.ToString(), bold, italic));
                plain.Clear();
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    FlushPlain();
                    output.Add(new MdCode(text[(i + 1)..close]));
                    i = close + 1;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                string delimiter = new(c, 2);
                int close = text.IndexOf(delimiter, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    ParseInlines(text[(i + 2)..close], bold: true, italic, output);
                    i = close + 2;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && IsItalicOpener(text, i))
            {
                int close = FindItalicCloser(text, i + 1, c);
                if (close > 0)
                {
                    FlushPlain();
                    ParseInlines(text[(i + 1)..close], bold, italic: true, output);
                    i = close + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int closeText = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                int closeUrl = closeText > 0 ? text.IndexOf(')', closeText + 2) : -1;
                if (closeText > i && closeUrl > closeText)
                {
                    FlushPlain();
                    output.Add(new MdLink(text[(i + 1)..closeText], text[(closeText + 2)..closeUrl]));
                    i = closeUrl + 1;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
    }

    private static bool IsItalicOpener(string text, int i)
    {
        // "*word" opens emphasis, "* " or "2 * 3" does not; "_" inside snake_case_names does not either.
        if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
        {
            return false;
        }

        return text[i] == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]);
    }

    private static int FindItalicCloser(string text, int start, char delimiter)
    {
        for (int j = start + 1; j < text.Length; j++)
        {
            if (text[j] != delimiter || char.IsWhiteSpace(text[j - 1]))
            {
                continue;
            }

            bool doubled = j + 1 < text.Length && text[j + 1] == delimiter;
            bool wordContinues = delimiter == '_' && j + 1 < text.Length && char.IsLetterOrDigit(text[j + 1]);
            if (!doubled && !wordContinues)
            {
                return j;
            }
        }

        return -1;
    }

    private static bool TryParseListItem(string line, out MdListItem item)
    {
        item = null!;
        int indent = line.Length - line.TrimStart().Length;
        string trimmed = line.TrimStart();
        int depth = Math.Min(indent / 2, 4);

        if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            item = new MdListItem(false, 0, depth, ParseInlines(trimmed[2..].Trim()));
            return true;
        }

        int digits = 0;
        while (digits < trimmed.Length && digits < 4 && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && trimmed.Length > digits + 1 && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
        {
            int number = int.Parse(trimmed.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture);
            item = new MdListItem(true, number, depth, ParseInlines(trimmed[(digits + 2)..].Trim()));
            return true;
        }

        return false;
    }

    private static bool IsRule(string trimmed) =>
        trimmed.Length >= 3 && (trimmed.All(ch => ch == '-') || trimmed.All(ch => ch == '*') || trimmed.All(ch => ch == '_'));

    private static int CountLeading(string s, char c)
    {
        int n = 0;
        while (n < s.Length && s[n] == c)
        {
            n++;
        }

        return n;
    }

    private static bool IsEscapable(char c) => "\\`*_[]()#+-.!>|~".Contains(c, StringComparison.Ordinal);
}
