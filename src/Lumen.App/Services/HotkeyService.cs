using System.Runtime.InteropServices;
using System.Windows.Interop;
using Lumen.App.Interop;
using Lumen.Core.Input;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

public enum HotkeyAction
{
    Ask = 1,
    Snip = 2,
}

/// <summary>
/// System-wide hotkeys through RegisterHotKey.
/// </summary>
/// <remarks>
/// <para>RegisterHotKey is the polite way to own a global shortcut: Windows itself watches the
/// keyboard and posts WM_HOTKEY to a window of ours. No hook, no per-keystroke cost, and if
/// another application already owns the combination the call fails instead of fighting over it.</para>
/// <para>The receiving window is a message-only window (parent HWND_MESSAGE): it is never
/// visible, never in the taskbar or Alt+Tab, and exists just to receive messages.</para>
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly nint HwndMessage = -3;

    private readonly ILogger<HotkeyService> _logger;
    private readonly HwndSource _source;
    private readonly HashSet<HotkeyAction> _registered = [];

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _source = new HwndSource(new HwndSourceParameters("Lumen.Hotkeys") { ParentWindow = HwndMessage, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Re-registers every hotkey from settings.</summary>
    /// <returns>Human readable problems (empty when everything was registered).</returns>
    public IReadOnlyList<string> Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UnregisterAll();

        var problems = new List<string>();
        TryRegister(HotkeyAction.Ask, settings.Ask, problems);
        TryRegister(HotkeyAction.Snip, settings.Snip, problems);
        return problems;
    }

    private void TryRegister(HotkeyAction action, string text, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return; // the user cleared it on purpose
        }

        if (!HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out string? error))
        {
            problems.Add($"{action} shortcut '{text}': {error}");
            return;
        }

        // MOD_NOREPEAT: holding the keys down fires once, not once per auto-repeat.
        uint modifiers = (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(_source.Handle, (int)action, modifiers, gesture.VirtualKey))
        {
            _registered.Add(action);
            _logger.LogInformation("Registered {Action} hotkey {Gesture}", action, gesture);
            return;
        }

        int win32Error = Marshal.GetLastPInvokeError();
        string reason = win32Error == ErrorHotkeyAlreadyRegistered
            ? "it is already used by another application"
            : $"Windows error {win32Error}";
        problems.Add($"Could not register {gesture} for {action}: {reason}. Choose another shortcut in Settings.");
        _logger.LogWarning("Could not register {Action} hotkey {Gesture}: {Reason}", action, gesture, reason);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var action = (HotkeyAction)(int)wParam;
            if (_registered.Contains(action))
            {
                handled = true;
                Pressed?.Invoke(this, action);
            }
        }

        return 0;
    }

    private void UnregisterAll()
    {
        foreach (HotkeyAction action in _registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, (int)action);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
