namespace Lumen.Core.Prompts;

/// <summary>
/// A one-click instruction applied to the captured text, e.g. "Summarize".
/// </summary>
/// <param name="Id">Stable identifier (used for keyboard shortcuts and settings).</param>
/// <param name="Label">Button caption.</param>
/// <param name="Instruction">
/// What the model is asked to do. <c>{language}</c> is replaced with the preferred language from settings.
/// </param>
/// <param name="Icon">A Segoe Fluent Icons glyph shown on the button.</param>
public sealed record QuickAction(string Id, string Label, string Instruction, string Icon = "");

/// <summary>The actions every installation starts with.</summary>
public static class BuiltInQuickActions
{
    public static IReadOnlyList<QuickAction> All { get; } =
    [
        new("explain", "Explain",
            "Explain the following clearly and concisely, in {language}. Assume an intelligent reader who is new to the topic.",
            ""),
        new("summarize", "Summarize",
            "Summarize the following in a few bullet points, written in the same language as the text. Keep the key facts, numbers and names.",
            ""),
        new("translate", "Translate",
            "Translate the following into {language}. If it is already in {language}, translate it into English. Output only the translation.",
            ""),
        new("fix", "Fix writing",
            "Fix spelling, grammar and punctuation in the following text. Keep its language, meaning, tone and formatting. Output only the corrected text.",
            ""),
        new("rewrite", "Rewrite",
            "Rewrite the following to be clearer and more concise, keeping its language and meaning. Output only the rewritten text.",
            ""),
        new("code", "Explain code",
            "Explain in {language} what this code does, step by step. Then point out bugs, edge cases or improvements, if any.",
            ""),
        new("reply", "Draft reply",
            "Draft a short, polite reply to the following message, in the same language as the message.",
            ""),
    ];
}
