using Lumen.Core.Chat;
using Lumen.Core.Prompts;

namespace Lumen.Core.Tests;

public class ConversationTests
{
    [Fact]
    public void Messages_must_alternate()
    {
        var c = new Conversation("s");
        c.Add(ChatMessage.User("a"));

        Assert.Throws<InvalidOperationException>(() => c.Add(ChatMessage.User("b")));
        c.Add(ChatMessage.Assistant("ok"));
        Assert.Equal(2, c.Messages.Count);
    }

    [Fact]
    public void System_messages_are_rejected_in_the_message_list() =>
        Assert.Throws<ArgumentException>(() => new Conversation("s").Add(ChatMessage.System("x")));

    [Fact]
    public void RemoveTrailingUserMessage_only_removes_an_unanswered_question()
    {
        var c = new Conversation("s");
        c.Add(ChatMessage.User("a"));
        c.Add(ChatMessage.Assistant("b"));

        c.RemoveTrailingUserMessage();
        Assert.Equal(2, c.Messages.Count);

        c.Add(ChatMessage.User("c"));
        c.RemoveTrailingUserMessage();
        Assert.Equal(2, c.Messages.Count);
    }
}

public class PromptBuilderTests
{
    [Fact]
    public void Wraps_the_captured_text_and_names_the_source_app()
    {
        var context = new CapturedContext("Some text", CaptureSource.UiAutomation, ProcessName: "notepad");

        ChatMessage message = PromptBuilder.BuildFirstMessage("Summarize this.", context, "Romanian", includeScreenshot: true);

        Assert.Equal(ChatRole.User, message.Role);
        Assert.Contains("<captured_text source=\"selection\" app=\"notepad\">\nSome text\n</captured_text>", message.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.EndsWith("Summarize this.", message.Content, StringComparison.Ordinal);
        Assert.Null(message.Image);
    }

    [Fact]
    public void Replaces_the_language_placeholder()
    {
        ChatMessage message = PromptBuilder.BuildFirstMessage("Translate into {language}.", CapturedContext.Empty, "Romanian", false);

        Assert.Equal("Translate into Romanian.", message.Content);
    }

    [Fact]
    public void Attaches_the_screenshot_only_when_allowed()
    {
        var image = new ImageAttachment([1, 2, 3]);
        var context = new CapturedContext("OCR text", CaptureSource.ScreenOcr, Screenshot: image);

        Assert.Same(image, PromptBuilder.BuildFirstMessage("Explain", context, "English", includeScreenshot: true).Image);
        Assert.Null(PromptBuilder.BuildFirstMessage("Explain", context, "English", includeScreenshot: false).Image);
        Assert.Contains("OCR", PromptBuilder.BuildFirstMessage("Explain", context, "English", false).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_quotes_in_attributes()
    {
        var context = new CapturedContext("x", CaptureSource.Clipboard, ProcessName: "a\"b<c");

        string content = PromptBuilder.BuildFirstMessage("Go", context, "English", false).Content;

        Assert.Contains("app=\"a&quot;b&lt;c\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_instruction_gets_a_sensible_default() =>
        Assert.False(string.IsNullOrWhiteSpace(
            PromptBuilder.BuildFirstMessage("  ", new CapturedContext("text", CaptureSource.Clipboard), "English", false).Content));

    [Fact]
    public void Truncate_keeps_the_beginning_and_the_end()
    {
        string text = new string('a', 5000) + new string('z', 5000);

        string truncated = PromptBuilder.Truncate(text, 1000);

        Assert.True(truncated.Length <= 1000);
        Assert.StartsWith("aaa", truncated, StringComparison.Ordinal);
        Assert.EndsWith("zzz", truncated, StringComparison.Ordinal);
        Assert.Contains("truncated", truncated, StringComparison.Ordinal);
        Assert.Equal("short", PromptBuilder.Truncate("short", 1000));
    }

    [Fact]
    public void Built_in_quick_actions_have_unique_ids()
    {
        IReadOnlyList<QuickAction> actions = BuiltInQuickActions.All;
        Assert.Equal(actions.Count, actions.Select(a => a.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(CaptureSource.UiAutomation, "notepad", "Selection from notepad")]
    [InlineData(CaptureSource.Clipboard, null, "Selection")]
    [InlineData(CaptureSource.None, null, "No selection")]
    public void Describes_the_capture_for_the_UI(CaptureSource source, string? process, string expected) =>
        Assert.Equal(expected, new CapturedContext("t", source, process).Describe());
}
