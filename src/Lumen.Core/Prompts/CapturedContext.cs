using Lumen.Core.Chat;

namespace Lumen.Core.Prompts;

/// <summary>Where the captured text came from.</summary>
public enum CaptureSource
{
    /// <summary>Nothing was captured: the user opened the assistant to ask a free question.</summary>
    None,

    /// <summary>Read directly through UI Automation (no clipboard involved).</summary>
    UiAutomation,

    /// <summary>Copied with a simulated Ctrl+C; the user's clipboard was restored afterwards.</summary>
    Clipboard,

    /// <summary>Recognized from a screen region with Windows OCR.</summary>
    ScreenOcr,
}

/// <summary>
/// Everything Lumen captured when the hotkey was pressed.
/// </summary>
/// <param name="Text">The selected or recognized text (may be empty).</param>
/// <param name="Source">How the text was obtained.</param>
/// <param name="ProcessName">Process that owned the foreground window, e.g. "notepad".</param>
/// <param name="WindowTitle">Title of that window.</param>
/// <param name="Screenshot">The captured screen region, for <see cref="CaptureSource.ScreenOcr"/>.</param>
/// <param name="TargetWindow">Native handle of the source window, so the answer can be pasted back.</param>
public sealed record CapturedContext(
    string Text,
    CaptureSource Source,
    string? ProcessName = null,
    string? WindowTitle = null,
    ImageAttachment? Screenshot = null,
    nint TargetWindow = 0)
{
    public static CapturedContext Empty { get; } = new("", CaptureSource.None);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Short description for the UI, e.g. "Selection from notepad".</summary>
    public string Describe() => Source switch
    {
        CaptureSource.UiAutomation or CaptureSource.Clipboard when ProcessName is not null => $"Selection from {ProcessName}",
        CaptureSource.UiAutomation or CaptureSource.Clipboard => "Selection",
        CaptureSource.ScreenOcr => HasText ? "Text recognized from screen region" : "Screen region (no text recognized)",
        _ => "No selection",
    };
}
