using System.Windows;
using System.Windows.Interop;
using Lumen.App.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Lumen.App.Infrastructure;

public static class WindowHelpers
{
    /// <summary>True when Windows is set to dark mode for apps.</summary>
    public static bool IsSystemDarkMode()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    /// <summary>Dark title bar when Windows is in dark mode, and rounded corners on Windows 11.</summary>
    public static void ApplySystemChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        int dark = IsSystemDarkMode() ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int corners = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int)); // ignored on Windows 10
    }

    /// <summary>
    /// Moves a window next to the mouse cursor, on the cursor's monitor, fully inside its work area.
    /// </summary>
    /// <remarks>
    /// Works in physical pixels with SetWindowPos. The window is first moved onto the target
    /// monitor so it adopts that monitor's DPI; only then is its pixel size known reliably.
    /// </remarks>
    public static void MoveNearCursor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
        System.Drawing.Rectangle work = Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;

        const uint flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        NativeMethods.SetWindowPos(hwnd, 0, work.X, work.Y, 0, 0, flags);

        double scale = Math.Max(NativeMethods.GetDpiForWindow(hwnd), 96) / 96.0;
        int width = (int)Math.Ceiling((double.IsNaN(window.Width) ? window.ActualWidth : window.Width) * scale);
        int height = (int)Math.Ceiling((double.IsNaN(window.Height) ? window.ActualHeight : window.Height) * scale);

        // Prefer below-right of the cursor; clamp so the whole window stays visible.
        int offset = (int)(16 * scale);
        int x = Math.Clamp(cursor.X + offset, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(cursor.Y + offset, work.Top, Math.Max(work.Top, work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, 0, x, y, 0, 0, flags);
    }

    /// <summary>Shows and focuses a window, even when another application is in the foreground.</summary>
    public static void ShowAndActivate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        // Windows only lets the foreground process (or one that just received input, such as a
        // hotkey) steal focus. A hotkey qualifies; this makes it work from the tray menu too.
        window.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);
        window.Focus();
    }
}
