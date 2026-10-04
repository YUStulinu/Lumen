using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Core.Text;

namespace Lumen.App.Controls;

/// <summary>
/// Attached property that renders Markdown into a read-only <see cref="RichTextBox"/>:
/// <c>&lt;RichTextBox controls:MarkdownRender.Markdown="{Binding Text}" /&gt;</c>.
/// </summary>
/// <remarks>
/// <para>An attached property lets any RichTextBox gain the behavior from XAML without subclassing.</para>
/// <para>While an answer streams in, Text changes dozens of times per second. Rebuilding the
/// FlowDocument on every change would waste time, so a change only schedules a render at
/// <see cref="DispatcherPriority.Background"/>: all changes that arrive before the UI thread
/// becomes idle are coalesced into a single render of the latest text.</para>
/// </remarks>
public static class MarkdownRender
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(MarkdownRender), new PropertyMetadata("", OnMarkdownChanged));

    private static readonly ConditionalWeakTable<RichTextBox, StrongBox<bool>> PendingRenders = [];

    public static string GetMarkdown(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(MarkdownProperty);
    }

    public static void SetMarkdown(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box)
        {
            return;
        }

        StrongBox<bool> pending = PendingRenders.GetValue(box, _ => new StrongBox<bool>());
        if (pending.Value)
        {
            return; // a render is already scheduled; it will pick up the newest text
        }

        pending.Value = true;
        box.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            pending.Value = false;
            box.Document = BuildDocument(box, GetMarkdown(box) ?? "");
        });
    }

    private static FlowDocument BuildDocument(FrameworkElement owner, string markdown)
    {
        Brush codeBackground = owner.TryFindResource("SubtleFillColorSecondaryBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
        Brush mutedText = owner.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        Brush divider = owner.TryFindResource("DividerStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        var mono = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, Courier New");

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = owner.GetValue(TextElement.FontFamilyProperty) as FontFamily ?? new FontFamily("Segoe UI"),
            FontSize = 14,
            LineHeight = 21,
        };

        foreach (MdBlock block in MarkdownParser.Parse(markdown))
        {
            switch (block)
            {
                case MdHeading heading:
                    var h = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = heading.Level switch { 1 => 20, 2 => 17, _ => 15 }, Margin = new Thickness(0, 10, 0, 4) };
                    AddInlines(h.Inlines, heading.Inlines, mono, codeBackground);
                    document.Blocks.Add(h);
                    break;

                case MdParagraph paragraph:
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    AddInlines(p.Inlines, paragraph.Inlines, mono, codeBackground);
                    document.Blocks.Add(p);
                    break;

                case MdQuote quote:
                    var q = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(10, 2, 0, 2),
                        BorderBrush = divider,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Foreground = mutedText,
                    };
                    AddInlines(q.Inlines, quote.Inlines, mono, codeBackground);
                    document.Blocks.Add(q);
                    break;

                case MdListItem item:
                    // A hanging indent keeps wrapped lines aligned after the bullet.
                    double indent = 18 + item.Depth * 18;
                    var li = new Paragraph { Margin = new Thickness(indent, 0, 0, 3), TextIndent = -14 };
                    li.Inlines.Add(new Run(item.Ordered ? $"{item.Number}. " : "•  ") { Foreground = mutedText });
                    AddInlines(li.Inlines, item.Inlines, mono, codeBackground);
                    document.Blocks.Add(li);
                    break;

                case MdCodeBlock code:
                    var c = new Paragraph
                    {
                        FontFamily = mono,
                        FontSize = 12.5,
                        LineHeight = 18,
                        Background = codeBackground,
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 2, 0, 10),
                    };
                    AddText(c.Inlines, code.Code, null);
                    document.Blocks.Add(c);
                    break;

                case MdRule:
                    document.Blocks.Add(new BlockUIContainer(new Separator { Margin = new Thickness(0, 6, 0, 6), Background = divider }));
                    break;
            }
        }

        return document;
    }

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, FontFamily mono, Brush codeBackground)
    {
        foreach (MdInline inline in inlines)
        {
            switch (inline)
            {
                case MdText text:
                    AddText(target, text.Text, run =>
                    {
                        if (text.Bold)
                        {
                            run.FontWeight = FontWeights.SemiBold;
                        }

                        if (text.Italic)
                        {
                            run.FontStyle = FontStyles.Italic;
                        }
                    });
                    break;

                case MdCode code:
                    target.Add(new Run(code.Code) { FontFamily = mono, FontSize = 12.5, Background = codeBackground });
                    break;

                case MdLink link when Uri.TryCreate(link.Url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp):
                    var hyperlink = new Hyperlink(new Run(link.Text)) { NavigateUri = uri, ToolTip = uri.ToString() };
                    hyperlink.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    target.Add(hyperlink);
                    break;

                case MdLink link:
                    target.Add(new Run(link.Text));
                    break;
            }
        }
    }

    /// <summary>Adds text as Runs separated by explicit LineBreaks, so "\n" always starts a new line.</summary>
    private static void AddText(InlineCollection target, string text, Action<Run>? style)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                target.Add(new LineBreak());
            }

            if (lines[i].Length > 0)
            {
                var run = new Run(lines[i].TrimEnd('\r'));
                style?.Invoke(run);
                target.Add(run);
            }
        }
    }
}