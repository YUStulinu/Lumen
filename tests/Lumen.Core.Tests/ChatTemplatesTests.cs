using Lumen.Core.Chat;
using Lumen.Core.Llm.Local;
using Lumen.Core.Settings;

namespace Lumen.Core.Tests;

public class ChatTemplatesTests
{
    private static Conversation TwoTurnConversation()
    {
        var c = new Conversation("Be brief.");
        c.Add(ChatMessage.User("Hi"));
        c.Add(ChatMessage.Assistant("Hello!"));
        c.Add(ChatMessage.User("Bye"));
        return c;
    }

    [Fact]
    public void Phi3_renders_every_turn_and_opens_the_assistant_turn()
    {
        string prompt = ChatTemplates.Render(PromptFormat.Phi3, TwoTurnConversation());

        Assert.Equal(
            "<|system|>\nBe brief.<|end|>\n<|user|>\nHi<|end|>\n<|assistant|>\nHello!<|end|>\n<|user|>\nBye<|end|>\n<|assistant|>\n",
            prompt);
    }

    [Fact]
    public void ChatML_uses_im_start_markers()
    {
        string prompt = ChatTemplates.Render(PromptFormat.ChatML, TwoTurnConversation());

        Assert.StartsWith("<|im_start|>system\nBe brief.<|im_end|>\n<|im_start|>user\nHi<|im_end|>\n", prompt, StringComparison.Ordinal);
        Assert.EndsWith("<|im_start|>assistant\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Llama3_starts_with_begin_of_text_and_ends_with_assistant_header()
    {
        string prompt = ChatTemplates.Render(PromptFormat.Llama3, TwoTurnConversation());

        Assert.StartsWith("<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\nBe brief.<|eot_id|>", prompt, StringComparison.Ordinal);
        Assert.EndsWith("<|start_header_id|>assistant<|end_header_id|>\n\n", prompt, StringComparison.Ordinal);
        Assert.Contains("<|start_header_id|>assistant<|end_header_id|>\n\nHello!<|eot_id|>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Gemma_has_no_system_role_so_it_is_merged_into_the_first_user_turn()
    {
        string prompt = ChatTemplates.Render(PromptFormat.Gemma, TwoTurnConversation());

        Assert.StartsWith("<bos><start_of_turn>user\nBe brief.\n\nHi<end_of_turn>\n<start_of_turn>model\nHello!<end_of_turn>\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("system", prompt, StringComparison.Ordinal);
        Assert.EndsWith("<start_of_turn>model\n", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("phi3", PromptFormat.Phi3)]
    [InlineData("phi4", PromptFormat.Phi3)]
    [InlineData("llama", PromptFormat.Llama3)]
    [InlineData("qwen2", PromptFormat.ChatML)]
    [InlineData("gemma3_text", PromptFormat.Gemma)]
    [InlineData("something-new", PromptFormat.ChatML)]
    [InlineData(null, PromptFormat.ChatML)]
    public void Detects_the_template_from_the_model_type(string? modelType, PromptFormat expected) =>
        Assert.Equal(expected, ChatTemplates.DetectFromModelType(modelType));

    [Fact]
    public void Every_format_defines_stop_markers()
    {
        foreach (PromptFormat format in Enum.GetValues<PromptFormat>())
        {
            Assert.NotEmpty(ChatTemplates.StopMarkers(format));
        }
    }
}
