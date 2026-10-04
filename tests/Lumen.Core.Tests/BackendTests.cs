using Anthropic.Models.Beta.Messages;
using Lumen.Core.Chat;
using Lumen.Core.Llm.Claude;
using Lumen.Core.Llm.Local;
using Lumen.Core.Settings;

namespace Lumen.Core.Tests;

public class ClaudeRequestTests
{
    private static Conversation Question(ImageAttachment? image = null)
    {
        var c = new Conversation("system prompt");
        c.Add(ChatMessage.User("What is this?", image));
        return c;
    }

    [Fact]
    public void Builds_a_request_with_model_limits_and_a_cached_system_prompt()
    {
        var options = new ClaudeSettings { Model = "claude-opus-5-5", MaxOutputTokens = 4000, Effort = "low" };

        MessageCreateParams request = ClaudeBackend.BuildRequest(Question(), options);

        Assert.Equal(4000, request.MaxTokens);
        Assert.Single(request.Messages);
        Assert.NotNull(request.System);
        Assert.NotNull(request.OutputConfig);
    }

    [Fact]
    public void Server_side_fallbacks_are_only_requested_for_models_that_support_them()
    {
        MessageCreateParams opus = ClaudeBackend.BuildRequest(Question(), new ClaudeSettings { Model = "claude-opus-5-5" });
        MessageCreateParams haiku = ClaudeBackend.BuildRequest(Question(), new ClaudeSettings { Model = "claude-haiku-4-5", Effort = "" });
        MessageCreateParams disabled = ClaudeBackend.BuildRequest(Question(), new ClaudeSettings { UseServerSideFallbacks = false });

        Assert.NotNull(opus.Fallbacks);
        Assert.NotNull(opus.Betas);
        Assert.Null(haiku.Fallbacks);
        Assert.Null(haiku.OutputConfig);
        Assert.Null(disabled.Fallbacks);
    }

    [Fact]
    public void An_image_becomes_an_image_block_followed_by_the_text()
    {
        MessageCreateParams request = ClaudeBackend.BuildRequest(Question(new ImageAttachment([0x89, 0x50, 0x4E, 0x47])), new ClaudeSettings());

        BetaMessageParam message = Assert.Single(request.Messages);
        Assert.True(message.Content.TryPickBetaContentBlockParams(out IReadOnlyList<BetaContentBlockParam>? blocks));
        Assert.Equal(2, blocks!.Count);
        Assert.True(blocks[0].TryPickImage(out _));
        Assert.True(blocks[1].TryPickText(out _));
    }

    [Theory]
    [InlineData("low", true)]
    [InlineData(" MAX ", true)]
    [InlineData("xhigh", true)]
    [InlineData("", false)]
    [InlineData("extreme", false)]
    public void Parses_effort_levels(string value, bool recognized) =>
        Assert.Equal(recognized, ClaudeBackend.ParseEffort(value) is not null);
}

public class LocalBackendTests
{
    [Theory]
    [InlineData(@"C:\models\Phi-4-mini-instruct-onnx\cpu_and_mobile\cpu-int4-rtn-block-32-acc-level-4", "Phi-4-mini-instruct-onnx")]
    [InlineData(@"C:\models\Phi-4-mini-instruct-onnx\gpu\gpu-int4-rtn-block-32", "Phi-4-mini-instruct-onnx")]
    [InlineData(@"C:\models\Phi-4-mini-instruct-onnx\cpu-int4-rtn-block-32-acc-level-4\", "Phi-4-mini-instruct-onnx")]
    [InlineData(@"C:\models\qwen2.5-1.5b", "qwen2.5-1.5b")]
    [InlineData("", "Local model")]
    public void Describes_the_model_folder(string path, string expected) =>
        Assert.Equal(expected, OnnxGenAiBackend.DescribeFolder(path));

    [Fact]
    public void Reports_a_missing_model_folder_as_unavailable()
    {
        var settings = new TestSettings();
        settings.Current.Local.ModelPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid());
        using var backend = new OnnxGenAiBackend(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxGenAiBackend>.Instance);

        Assert.False(backend.CheckAvailability(new Conversation("")).IsAvailable);
        Assert.False(backend.IsLoaded);
    }
}
