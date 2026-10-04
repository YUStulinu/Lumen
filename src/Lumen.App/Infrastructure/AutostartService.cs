using Microsoft.Win32;

namespace Lumen.App.Infrastructure;

/// <summary>
/// Starts Lumen when the user signs in, through the per-user "Run" registry key.
/// </summary>
/// <remarks>
/// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run is what Task Manager's
/// "Startup apps" page lists, so users can see and disable the entry there too. Being under
/// HKCU it needs no administrator rights.
/// </remarks>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Lumen";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Contains(AppPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            // Quoted: the install path may contain spaces.
            key.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\" --background");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
        }
    }
}
