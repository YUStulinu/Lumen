using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Infrastructure;
using Lumen.App.Services;
using Lumen.Core.Chat;
using Lumen.Core.Input;
using Lumen.Core.Llm;
using Lumen.Core.Llm.Claude;
using Lumen.Core.Llm.Local;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.App.ViewModels;

/// <summary>
/// Backs the settings window. Works on a draft copy and writes it back only on Save.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ISecretProtector _protector;
    private readonly ILoggerFactory _loggerFactory;
    private string? _newApiKey;
    private bool _clearApiKey;

    // ----- General -----
    [ObservableProperty] private string _askHotkey = "";
    [ObservableProperty] private string _snipHotkey = "";
    [ObservableProperty] private bool _doubleTapCtrl;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _hideOnDeactivate;
    [ObservableProperty] private string _preferredLanguage = "";
    [ObservableProperty] private RoutingMode _routingMode;

    // ----- Local model -----
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private PromptFormat _promptFormat;
    [ObservableProperty] private int _maxContextTokens;
    [ObservableProperty] private int _localMaxOutputTokens;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private bool _preloadOnStartup;
    [ObservableProperty] private int _unloadAfterIdleMinutes;
    [ObservableProperty] private string _localTestResult = "";

    // ----- Claude -----
    [ObservableProperty] private string _claudeModel = "";
    [ObservableProperty] private string _effort = "";
    [ObservableProperty] private int _claudeMaxOutputTokens;
    [ObservableProperty] private bool _useServerSideFallbacks;
    [ObservableProperty] private bool _sendScreenshots;
    [ObservableProperty] private string _apiKeyStatus = "";
    [ObservableProperty] private string _claudeTestResult = "";

    // ----- Capture -----
    [ObservableProperty] private bool _useClipboardFallback;
    [ObservableProperty] private string _excludedProcesses = "";
    [ObservableProperty] private string _ocrLanguage = "";
    [ObservableProperty] private int _maxCapturedChars;

    [ObservableProperty] private string _validationError = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLocalModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestClaudeCommand))]
    private bool _isTesting;

    public SettingsViewModel(SettingsStore store, ISecretProtector protector, ILoggerFactory loggerFactory)
    {
        _store = store;
        _protector = protector;
        _loggerFactory = loggerFactory;
        LoadFrom(store.Current);
    }

    /// <summary>Raised after a successful save; the window closes itself.</summary>
    public event EventHandler? Saved;

    public static IReadOnlyList<RoutingMode> RoutingModes { get; } = Enum.GetValues<RoutingMode>();

    public static IReadOnlyList<PromptFormat> PromptFormats { get; } = Enum.GetValues<PromptFormat>();

    public static IReadOnlyList<string> EffortLevels { get; } = ["", "low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyList<string> SuggestedClaudeModels { get; } =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5", "claude-fable-5-1"];

    /// <summary>"" means "use the Windows display languages".</summary>
    public IReadOnlyList<string> OcrLanguages { get; } = ["", .. SafeOcrLanguages()];

    public string SettingsFilePath => _store.FilePath;

    /// <summary>Called from the PasswordBox (which cannot be data-bound, by design, so the secret is not kept in a bindable string).</summary>
    public void SetNewApiKey(string key)
    {
        _newApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        _clearApiKey = false;
        ApiKeyStatus = _newApiKey is null ? DescribeStoredKey() : "A new key will be saved (encrypted with DPAPI).";
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        _newApiKey = null;
        _clearApiKey = true;
        ApiKeyStatus = "The saved key will be removed when you click Save.";
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the folder that contains genai_config.json",
            InitialDirectory = Directory.Exists(ModelPath) ? ModelPath : AppPaths.ModelsDirectory,
        };

        if (dialog.ShowDialog() == true)
        {
            ModelPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private static void OpenModelCatalog() =>
        Process.Start(new ProcessStartInfo("https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx") { UseShellExecute = true });

    [RelayCommand]
    private static void OpenApiKeyPage() =>
        Process.Start(new ProcessStartInfo("https://platform.claude.com/settings/keys") { UseShellExecute = true });

    [RelayCommand]
    private void OpenSettingsFile() =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_store.FilePath}\"") { UseShellExecute = true });

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestLocalModelAsync()
    {
        AppSettings draft = BuildDraft();
        IsTesting = true;
        LocalTestResult = "Loading the model and generating a short answer…";
        try
        {
            using var backend = new OnnxGenAiBackend(new FixedSettings(draft), _loggerFactory.CreateLogger<OnnxGenAiBackend>());
            LocalTestResult = await RunTestAsync(backend);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestClaudeAsync()
    {
        AppSettings draft = BuildDraft();
        string? key = SettingsStore.ResolveApiKey(draft, _protector);
        IsTesting = true;
        ClaudeTestResult = "Contacting Claude…";
        try
        {
            using var backend = new ClaudeBackend(new FixedSettings(draft), () => key, _loggerFactory.CreateLogger<ClaudeBackend>());
            ClaudeTestResult = await RunTestAsync(backend);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => !IsTesting;

    [RelayCommand]
    private void Save()
    {
        if (!Validate())
        {
            return;
        }

        AppSettings draft = BuildDraft();
        _store.Save(draft);

        try
        {
            AutostartService.SetEnabled(draft.General.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ValidationError = "Saved, but the Windows startup entry could not be changed: " + ex.Message;
            return;
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }

    private bool Validate()
    {
        foreach ((string name, string value) in new[] { ("Ask", AskHotkey), ("Read screen region", SnipHotkey) })
        {
            if (!string.IsNullOrWhiteSpace(value) && !HotkeyGesture.TryParse(value, out _, out string? error))
            {
                ValidationError = $"{name} shortcut: {error}";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(AskHotkey) && string.Equals(AskHotkey, SnipHotkey, StringComparison.OrdinalIgnoreCase))
        {
            ValidationError = "The two shortcuts must be different.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ModelPath) && !File.Exists(Path.Combine(ModelPath, "genai_config.json")))
        {
            ValidationError = "The local model folder must contain genai_config.json (pick the folder of one model variant).";
            return false;
        }

        ValidationError = "";
        return true;
    }

    /// <summary>Starts from a clone of the saved settings so fields this window does not show are preserved.</summary>
    private AppSettings BuildDraft()
    {
        AppSettings s = _store.Current.Clone();
        s.Hotkeys.Ask = NormalizeHotkey(AskHotkey);
        s.Hotkeys.Snip = NormalizeHotkey(SnipHotkey);
        s.Hotkeys.DoubleTapCtrl = DoubleTapCtrl;
        s.General.StartWithWindows = StartWithWindows;
        s.General.AlwaysOnTop = AlwaysOnTop;
        s.General.HideOnDeactivate = HideOnDeactivate;
        s.General.PreferredLanguage = string.IsNullOrWhiteSpace(PreferredLanguage) ? "English" : PreferredLanguage.Trim();
        s.RoutingMode = RoutingMode;

        s.Local.ModelPath = ModelPath.Trim();
        s.Local.PromptFormat = PromptFormat;
        s.Local.MaxContextTokens = MaxContextTokens;
        s.Local.MaxOutputTokens = LocalMaxOutputTokens;
        s.Local.Temperature = Temperature;
        s.Local.PreloadOnStartup = PreloadOnStartup;
        s.Local.UnloadAfterIdleMinutes = UnloadAfterIdleMinutes;

        s.Claude.Model = ClaudeModel.Trim();
        s.Claude.Effort = Effort;
        s.Claude.MaxOutputTokens = ClaudeMaxOutputTokens;
        s.Claude.UseServerSideFallbacks = UseServerSideFallbacks;
        s.Claude.SendScreenshots = SendScreenshots;
        if (_clearApiKey)
        {
            s.Claude.ProtectedApiKey = null;
        }
        else if (_newApiKey is not null)
        {
            s.Claude.ProtectedApiKey = _protector.Protect(_newApiKey);
        }

        s.Capture.UseClipboardFallback = UseClipboardFallback;
        s.Capture.ClipboardFallbackExcludedProcesses = ExcludedProcesses
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        s.Capture.OcrLanguage = OcrLanguage;
        s.Capture.MaxCapturedChars = MaxCapturedChars;
        return s;
    }

    private void LoadFrom(AppSettings s)
    {
        AskHotkey = s.Hotkeys.Ask;
        SnipHotkey = s.Hotkeys.Snip;
        DoubleTapCtrl = s.Hotkeys.DoubleTapCtrl;
        StartWithWindows = AutostartService.IsEnabled();
        AlwaysOnTop = s.General.AlwaysOnTop;
        HideOnDeactivate = s.General.HideOnDeactivate;
        PreferredLanguage = s.General.PreferredLanguage;
        RoutingMode = s.RoutingMode;

        ModelPath = s.Local.ModelPath;
        PromptFormat = s.Local.PromptFormat;
        MaxContextTokens = s.Local.MaxContextTokens;
        LocalMaxOutputTokens = s.Local.MaxOutputTokens;
        Temperature = s.Local.Temperature;
        PreloadOnStartup = s.Local.PreloadOnStartup;
        UnloadAfterIdleMinutes = s.Local.UnloadAfterIdleMinutes;

        ClaudeModel = s.Claude.Model;
        Effort = s.Claude.Effort;
        ClaudeMaxOutputTokens = s.Claude.MaxOutputTokens;
        UseServerSideFallbacks = s.Claude.UseServerSideFallbacks;
        SendScreenshots = s.Claude.SendScreenshots;
        ApiKeyStatus = DescribeStoredKey();

        UseClipboardFallback = s.Capture.UseClipboardFallback;
        ExcludedProcesses = string.Join(", ", s.Capture.ClipboardFallbackExcludedProcesses);
        OcrLanguage = s.Capture.OcrLanguage;
        MaxCapturedChars = s.Capture.MaxCapturedChars;
    }

    private string DescribeStoredKey()
    {
        if (!string.IsNullOrEmpty(_store.Current.Claude.ProtectedApiKey))
        {
            return _protector.Unprotect(_store.Current.Claude.ProtectedApiKey) is null
                ? "A saved key exists but cannot be decrypted by this Windows account. Enter it again."
                : "A key is saved (encrypted with DPAPI). Leave the box empty to keep it.";
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            ? "No key saved. Claude is unavailable until you add one."
            : "Using the ANTHROPIC_API_KEY environment variable.";
    }

    private static string NormalizeHotkey(string text) =>
        HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out _) ? gesture.ToString() : text.Trim();

    private static async Task<string> RunTestAsync(ILlmBackend backend)
    {
        var conversation = new Conversation("You are a connectivity test. Follow the instruction exactly.");
        conversation.Add(ChatMessage.User("Reply with one short friendly sentence that confirms you work."));

        BackendAvailability availability = backend.CheckAvailability(conversation);
        if (!availability.IsAvailable)
        {
            return "✗ Not available: " + availability.Reason;
        }

        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await foreach (string piece in backend.StreamAsync(conversation, timeout.Token))
            {
                text.Append(piece);
            }

            return $"✓ {backend.DisplayName} answered in {stopwatch.Elapsed.TotalSeconds:F1} s: \"{text.ToString().Trim()}\"";
        }
        catch (OperationCanceledException)
        {
            return "✗ Timed out after 3 minutes.";
        }
        catch (LlmBackendException ex)
        {
            return "✗ " + ex.Message;
        }
    }

    private static IEnumerable<string> SafeOcrLanguages()
    {
        try
        {
            return OcrService.AvailableLanguages();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException)
        {
            return [];
        }
    }

    /// <summary>An ISettingsProvider that always returns the draft being edited.</summary>
    private sealed class FixedSettings(AppSettings settings) : ISettingsProvider
    {
        public AppSettings Current => settings;

        public event EventHandler<AppSettings>? Changed
        {
            add { }
            remove { }
        }
    }
}
