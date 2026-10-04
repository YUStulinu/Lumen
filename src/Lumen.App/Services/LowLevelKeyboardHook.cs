using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Lumen.App.Interop;
using Lumen.Core.Input;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>
/// A WH_KEYBOARD_LL hook that detects a double tap of the Ctrl key.
/// </summary>
/// <remarks>
/// <para>Unlike RegisterHotKey, a low-level hook sees every key press in the whole session
/// before any application does. That power comes with rules:</para>
/// <list type="bullet">
/// <item>The callback runs on the thread that installed the hook, which therefore needs a
///   message loop: we install it from the WPF UI thread, whose Dispatcher pumps messages.</item>
/// <item>The callback must return quickly. Windows waits for it on every keystroke and
///   silently removes hooks that are too slow (LowLevelHooksTimeout, about 1 second).
///   So it only updates a few integers and posts the real work to the Dispatcher.</item>
/// <item>It must always call CallNextHookEx so other hooks in the chain keep working,
///   and it must never block or swallow keys it does not own.</item>
/// <item>Injected events (from SendInput, including our own synthetic Ctrl+C) are ignored,
///   otherwise Lumen copying a selection could trigger itself.</item>
/// </list>
/// <para>The callback is a static method marked <c>[UnmanagedCallersOnly]</c> and passed to
/// Windows as a raw function pointer. With a classic delegate we would have to keep the
/// delegate alive manually; if the GC collected it, Windows would call freed memory.</para>
/// </remarks>
public sealed unsafe class LowLevelKeyboardHook : IDisposable
{
    // There is at most one hook per process, so the static callback can find its instance here.
    private static LowLevelKeyboardHook? s_current;

    private readonly ILogger<LowLevelKeyboardHook> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DoubleTapDetector _detector = new();
    private nint _hook;

    public LowLevelKeyboardHook(ILogger<LowLevelKeyboardHook> logger)
    {
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Raised on the UI thread after Ctrl was tapped twice.</summary>
    public event EventHandler? DoubleTapCtrl;

    public bool IsInstalled => _hook != 0;

    public void Install()
    {
        if (IsInstalled)
        {
            return;
        }

        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one low-level keyboard hook may be installed.");
        }

        s_current = this;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, &HookProc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            s_current = null;
            int error = Marshal.GetLastPInvokeError();
            _logger.LogError("SetWindowsHookEx failed with Win32 error {Error}", error);
            return;
        }

        _logger.LogInformation("Low-level keyboard hook installed (double-tap Ctrl enabled)");
    }

    public void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
        s_current = null;
        _logger.LogInformation("Low-level keyboard hook removed");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        // An exception must never escape into native code: it would terminate the process.
        try
        {
            LowLevelKeyboardHook? self = s_current;
            if (nCode >= 0 && self is not null)
            {
                var data = (NativeMethods.KBDLLHOOKSTRUCT*)lParam;
                if ((data->flags & NativeMethods.LLKHF_INJECTED) == 0)
                {
                    int message = (int)wParam;
                    bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                    bool isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                    if ((isDown || isUp) && self._detector.OnKey(data->vkCode, isDown, data->time))
                    {
                        // Do the work later, on the Dispatcher, and return to Windows immediately.
                        self._dispatcher.BeginInvoke(() => self.DoubleTapCtrl?.Invoke(self, EventArgs.Empty));
                    }
                }
            }
        }
        catch
        {
            // Swallow everything: see above.
        }

        return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
