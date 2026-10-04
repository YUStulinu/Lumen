using System.Windows;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Core.Chat;
using Lumen.Core.Llm.Local;
using Lumen.Core.Prompts;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>
/// The application's conductor: turns hotkeys and tray clicks into captures, and captures
/// into an open assistant window. Every other class does one thing; this one wires them up.
/// </summary>
public sealed class AssistantCoordinator : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkeys;
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly TrayIconService _tray;
    private readonly SelectionCaptureService _selection;
    private readonly SnipService _snip;
    private readonly OcrService _ocr;
    private readonly OnnxGenAiBackend _localModel;
    private readonly AssistantViewModel _assistant;
    private readonly AssistantWindow _window;
    private readonly Func<SettingsWindow> _settingsWindowFactory;
    private readonly ILogger<AssistantCoordinator> _logger;

    private SettingsWindow? _settingsWindow;
    private bool _capturing;
    private bool _hasContent;
    private string _loadedModelPath = "";

    public AssistantCoordinator(
        SettingsStore settings,
        HotkeyService hotkeys,
        LowLevelKeyboardHook keyboardHook,
        TrayIconService tray,
        SelectionCaptureService selection,
        SnipService snip,
        OcrService ocr,
        OnnxGenAiBackend localModel,
        AssistantViewModel assistant,
        AssistantWindow window,
        Func<SettingsWindow> settingsWindowFactory,
        ILogger<AssistantCoordinator> logger)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _keyboardHook = keyboardHook;
        _tray = tray;
        _selection = selection;
        _snip = snip;
        _ocr = ocr;
        _localModel = localModel;
        _assistant = assistant;
        _window = window;
        _settingsWindowFactory = settingsWindowFactory;
        _logger = logger;
    }

    public void Start(bool firstRun)
    {
        _hotkeys.Pressed += async (_, action) => await RunSafelyAsync(action == HotkeyAction.Ask ? AskAboutSelectionAsync : ReadScreenRegionAsync);
        _keyboardHook.DoubleTapCtrl += async (_, _) => await RunSafelyAsync(AskAboutSelectionAsync);

        _tray.OpenRequested += (_, _) => OpenAssistant();
        _tray.AskRequested += (_, _) => OpenAssistant();
        _tray.SnipRequested += async (_, _) => await RunSafelyAsync(ReadScreenRegionAsync);
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += (_, _) => Application.Current.Shutdown();
        _tray.RoutingModeSelected += (_, mode) => ChangeRoutingMode(mode);
        _window.SettingsRequested += (_, _) => OpenSettings();
        _settings.Changed += (_, s) => Application.Current.Dispatcher.Invoke(() => ApplySettings(s));

        _tray.Show();
        _loadedModelPath = _settings.Current.Local.ModelPath;
        ApplySettings(_settings.Current);

        if (_settings.Current.Local.PreloadOnStartup && _localModel.CheckAvailability(new Conversation("")).IsAvailable)
        {
            _ = PreloadModelAsync();
        }

        AppSettings s = _settings.Current;
        if (firstRun)
        {
            _tray.ShowNotification(
                "Lumen is running",
                $"Select text anywhere and press {s.Hotkeys.Ask}. Press {s.Hotkeys.Snip} to read text from the screen.");

            bool nothingConfigured = string.IsNullOrWhiteSpace(s.Local.ModelPath) && _settings.GetApiKey() is null;
            if (nothingConfigured)
            {
                OpenSettings();
            }
        }
    }

    /// <summary>Shows the window as it was (tray click, second launch of Lumen.exe).</summary>
    public void OpenAssistant()
    {
        if (!_hasContent)
        {
            _assistant.Load(CapturedContext.Empty);
            _hasContent = true;
        }

        _window.ShowNearCursor();
    }

    /// <summary>Hotkey: read the selection of the foreground app, then open the assistant on it.</summary>
    private async Task AskAboutSelectionAsync()
    {
        if (_window.IsActive)
        {
            // Pressing the hotkey while Lumen itself is focused just toggles it away.
            _window.Hide();
            return;
        }

        CapturedContext context = await _selection.CaptureAsync();
        ShowWith(context);
    }

    /// <summary>Hotkey: let the user pick a screen region, OCR it, then open the assistant on the text.</summary>
    private async Task ReadScreenRegionAsync()
    {
        if (_window.IsVisible)
        {
            _window.Hide();
            await Task.Delay(150); // let the compositor remove the window before taking the screenshot
        }

        SnipResult? snip = await _snip.SelectRegionAsync();
        if (snip is null)
        {
            return;
        }

        string text;
        try
        {
            text = await _ocr.RecognizeAsync(snip.Png, _settings.Current.Capture.OcrLanguage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "OCR failed");
            _tray.ShowNotification("Text recognition failed", ex.Message, isError: true);
            text = "";
        }

        var context = new CapturedContext(
            PromptBuilder.Truncate(text, _settings.Current.Capture.MaxCapturedChars),
            CaptureSource.ScreenOcr,
            Screenshot: new ImageAttachment(snip.Png));
        ShowWith(context);
    }

    private void ShowWith(CapturedContext context)
    {
        _assistant.Load(context);
        _hasContent = true;
        _window.ShowNearCursor();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // While the settings window is open the global hotkeys are released, so the
        // shortcut boxes can record any combination (including the current ones).
        _hotkeys.Apply(new HotkeySettings { Ask = "", Snip = "" });
        _keyboardHook.Uninstall();

        _settingsWindow = _settingsWindowFactory();
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ApplySettings(_settings.Current);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ChangeRoutingMode(RoutingMode mode)
    {
        AppSettings updated = _settings.Current.Clone();
        updated.RoutingMode = mode;
        _settings.Save(updated);
        _tray.ShowNotification("Lumen", $"Assistant mode: {mode}");
    }

    private void ApplySettings(AppSettings settings)
    {
        if (_settingsWindow is not null)
        {
            return; // re-applied when the settings window closes
        }

        IReadOnlyList<string> problems = _hotkeys.Apply(settings.Hotkeys);
        if (problems.Count > 0)
        {
            _tray.ShowNotification("Shortcut problem", string.Join("\n", problems), isError: true);
        }

        if (settings.Hotkeys.DoubleTapCtrl)
        {
            _keyboardHook.Install();
        }
        else
        {
            _keyboardHook.Uninstall();
        }

        // A different model folder: free the old model now instead of waiting for the idle timer.
        if (!string.Equals(_loadedModelPath, settings.Local.ModelPath, StringComparison.OrdinalIgnoreCase))
        {
            _loadedModelPath = settings.Local.ModelPath;
            _ = _localModel.UnloadAsync();
        }
    }

    private async Task PreloadModelAsync()
    {
        try
        {
            await _localModel.PreloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preloading the local model failed");
            _tray.ShowNotification("Local model", "The local model could not be loaded: " + ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Hotkey handlers are 'async void' event handlers in disguise: an exception escaping them
    /// would crash the app. Funnel them through one place that logs and reports instead.
    /// </summary>
    private async Task RunSafelyAsync(Func<Task> action)
    {
        if (_capturing)
        {
            return; // ignore hotkey repeats while a capture is still running
        }

        _capturing = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture failed");
            _tray.ShowNotification("Lumen", "Something went wrong: " + ex.Message, isError: true);
        }
        finally
        {
            _capturing = false;
        }
    }

    public void Dispose()
    {
        _keyboardHook.Uninstall();
    }
}
