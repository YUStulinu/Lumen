using System.Text.Json.Serialization;
using Lumen.Core.Prompts;

namespace Lumen.Core.Settings;

/// <summary>How the router chooses between the local model and Claude.</summary>
public enum RoutingMode
{
    /// <summary>Use the local model; fall back to Claude when it is unavailable or fails.</summary>
    LocalFirst,

    /// <summary>Use Claude; fall back to the local model when Claude is unavailable or fails (offline, no key...).</summary>
    CloudFirst,

    /// <summary>Never send anything to the network.</summary>
    LocalOnly,

    /// <summary>Never load the local model.</summary>
    CloudOnly,
}

/// <summary>Which chat template turns a conversation into the single prompt string a local model reads.</summary>
public enum PromptFormat
{
    /// <summary>Use the template shipped with the model; if it has none, guess from genai_config.json.</summary>
    Auto,
    Phi3,
    Llama3,
    ChatML,
    Gemma,
}

/// <summary>
/// Everything the user can configure. Serialized as JSON to %APPDATA%\Lumen\settings.json.
/// </summary>
/// <remarks>
/// Plain mutable classes keep System.Text.Json and two-way WPF data binding simple.
/// The app never mutates the live instance: the settings window edits a <see cref="Clone"/>
/// and the store swaps it in atomically on save.
/// </remarks>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public HotkeySettings Hotkeys { get; set; } = new();

    public RoutingMode RoutingMode { get; set; } = RoutingMode.LocalFirst;

    public LocalModelSettings Local { get; set; } = new();

    public ClaudeSettings Claude { get; set; } = new();

    public CaptureSettings Capture { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    /// <summary>User defined actions shown after the built-in ones.</summary>
    public List<QuickAction> CustomActions { get; set; } = [];

    public AppSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings),
            SettingsJsonContext.Default.AppSettings)!;
}

public sealed class HotkeySettings
{
    /// <summary>Reads the current selection and opens the assistant.</summary>
    public string Ask { get; set; } = "Ctrl+Shift+Space";

    /// <summary>Opens the screen-region capture overlay and runs OCR on the selected region.</summary>
    public string Snip { get; set; } = "Ctrl+Alt+Shift+S";

    /// <summary>
    /// Pressing and releasing Ctrl twice quickly also triggers "Ask".
    /// Implemented with a low-level keyboard hook, so it is off by default.
    /// </summary>
    public bool DoubleTapCtrl { get; set; }
}

public sealed class LocalModelSettings
{
    /// <summary>Folder containing genai_config.json and the .onnx files.</summary>
    public string ModelPath { get; set; } = "";

    public PromptFormat PromptFormat { get; set; } = PromptFormat.Auto;

    /// <summary>Total tokens (prompt + answer) the model may use. Keeps memory use bounded.</summary>
    public int MaxContextTokens { get; set; } = 4096;

    public int MaxOutputTokens { get; set; } = 1024;

    public double Temperature { get; set; } = 0.3;

    public double TopP { get; set; } = 0.9;

    /// <summary>Load the model at startup instead of on first use (faster first answer, more RAM while idle).</summary>
    public bool PreloadOnStartup { get; set; }

    /// <summary>Free the model's memory after this many idle minutes. 0 keeps it loaded.</summary>
    public int UnloadAfterIdleMinutes { get; set; } = 15;
}

public sealed class ClaudeSettings
{
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>"low", "medium", "high", "xhigh", "max", or "" to omit the parameter (needed for models without effort support).</summary>
    public string Effort { get; set; } = "low";

    public int MaxOutputTokens { get; set; } = 8000;

    /// <summary>The API key encrypted with DPAPI (base64). Never stored in clear text.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>When Claude declines a request for policy reasons, let the API retry it on a recommended fallback model.</summary>
    public bool UseServerSideFallbacks { get; set; } = true;

    /// <summary>Send the captured screenshot itself (not only its OCR text) so Claude can see layout, charts and images.</summary>
    public bool SendScreenshots { get; set; } = true;
}

public sealed class CaptureSettings
{
    /// <summary>Simulate Ctrl+C when UI Automation cannot read the selection.</summary>
    public bool UseClipboardFallback { get; set; } = true;

    /// <summary>
    /// Processes that must never receive a synthetic Ctrl+C: in a terminal it would
    /// interrupt the running program instead of copying.
    /// </summary>
    public List<string> ClipboardFallbackExcludedProcesses { get; set; } =
        ["WindowsTerminal", "conhost", "cmd", "powershell", "pwsh", "OpenConsole", "mintty", "alacritty", "wezterm-gui", "putty"];

    /// <summary>BCP-47 tag such as "en-US" or "ro-RO". Empty uses the Windows display languages.</summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>Hard cap on captured text so a huge selection cannot blow up the prompt.</summary>
    public int MaxCapturedChars { get; set; } = 60_000;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }

    /// <summary>Hide the assistant window when it loses focus (like a launcher).</summary>
    public bool HideOnDeactivate { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Language for actions such as "Translate". Free text, passed to the model.</summary>
    public string PreferredLanguage { get; set; } = "Romanian";
}

/// <summary>
/// Source-generated JSON metadata: no reflection at runtime, faster startup, and it also
/// keeps the serializer working if the app is ever trimmed.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
