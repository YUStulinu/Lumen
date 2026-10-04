using System.Text;
using Lumen.Core.Chat;
using Lumen.Core.Settings;

namespace Lumen.Core.Llm.Local;

/// <summary>
/// Built-in chat templates.
/// </summary>
/// <remarks>
/// A chat model was fine-tuned on conversations rendered in one exact format, with special
/// tokens marking where each turn starts and ends. Feeding it the wrong format still "works"
/// but noticeably degrades answers, so the format must match the model family.
/// The ending of every template is an open assistant header: the model continues from there,
/// which is how it knows it should now write the assistant's reply.
/// </remarks>
public static class ChatTemplates
{
    /// <summary>Maps the "model.type" value from genai_config.json to a template.</summary>
    public static PromptFormat DetectFromModelType(string? modelType) =>
        (modelType ?? "").Trim().ToLowerInvariant() switch
        {
            var t when t.StartsWith("phi", StringComparison.Ordinal) => PromptFormat.Phi3,
            var t when t.StartsWith("llama", StringComparison.Ordinal) => PromptFormat.Llama3,
            var t when t.StartsWith("qwen", StringComparison.Ordinal) => PromptFormat.ChatML,
            var t when t.StartsWith("gemma", StringComparison.Ordinal) => PromptFormat.Gemma,
            _ => PromptFormat.ChatML, // The most widespread format among open models.
        };

    public static string Render(PromptFormat format, Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return format switch
        {
            PromptFormat.Phi3 => RenderPhi3(conversation),
            PromptFormat.Llama3 => RenderLlama3(conversation),
            PromptFormat.Gemma => RenderGemma(conversation),
            PromptFormat.ChatML or PromptFormat.Auto => RenderChatMl(conversation),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    /// <summary>Text fragments that, if generated, mean the model is trying to start a new turn.</summary>
    public static IReadOnlyList<string> StopMarkers(PromptFormat format) => format switch
    {
        PromptFormat.Phi3 => ["<|end|>", "<|user|>", "<|endoftext|>"],
        PromptFormat.Llama3 => ["<|eot_id|>", "<|start_header_id|>"],
        PromptFormat.Gemma => ["<end_of_turn>", "<start_of_turn>"],
        _ => ["<|im_end|>", "<|im_start|>"],
    };

    // <|system|>
    // You are...<|end|>
    // <|user|>
    // Hello<|end|>
    // <|assistant|>
    private static string RenderPhi3(Conversation c)
    {
        var sb = new StringBuilder();
        sb.Append("<|system|>\n").Append(c.SystemPrompt).Append("<|end|>\n");
        foreach (ChatMessage m in c.Messages)
        {
            sb.Append(m.Role == ChatRole.User ? "<|user|>\n" : "<|assistant|>\n").Append(m.Content).Append("<|end|>\n");
        }

        return sb.Append("<|assistant|>\n").ToString();
    }

    // <|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n...<|eot_id|>
    private static string RenderLlama3(Conversation c)
    {
        var sb = new StringBuilder("<|begin_of_text|>");
        AppendLlama3Turn(sb, "system", c.SystemPrompt);
        foreach (ChatMessage m in c.Messages)
        {
            AppendLlama3Turn(sb, m.Role == ChatRole.User ? "user" : "assistant", m.Content);
        }

        return sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n").ToString();

        static void AppendLlama3Turn(StringBuilder sb, string role, string content) =>
            sb.Append("<|start_header_id|>").Append(role).Append("<|end_header_id|>\n\n").Append(content).Append("<|eot_id|>");
    }

    // <|im_start|>system\n...<|im_end|>\n   (Qwen, many others)
    private static string RenderChatMl(Conversation c)
    {
        var sb = new StringBuilder();
        sb.Append("<|im_start|>system\n").Append(c.SystemPrompt).Append("<|im_end|>\n");
        foreach (ChatMessage m in c.Messages)
        {
            sb.Append("<|im_start|>").Append(m.Role == ChatRole.User ? "user" : "assistant").Append('\n')
              .Append(m.Content).Append("<|im_end|>\n");
        }

        return sb.Append("<|im_start|>assistant\n").ToString();
    }

    // Gemma has no system role: the system prompt is prepended to the first user turn.
    private static string RenderGemma(Conversation c)
    {
        var sb = new StringBuilder("<bos>");
        bool first = true;
        foreach (ChatMessage m in c.Messages)
        {
            string role = m.Role == ChatRole.User ? "user" : "model";
            string content = first && m.Role == ChatRole.User ? c.SystemPrompt + "\n\n" + m.Content : m.Content;
            sb.Append("<start_of_turn>").Append(role).Append('\n').Append(content).Append("<end_of_turn>\n");
            first = false;
        }

        return sb.Append("<start_of_turn>model\n").ToString();
    }
}
