using System.IO;

namespace Lumen.App.Infrastructure;

/// <summary>Where Lumen keeps its files. Everything is per-user; nothing needs administrator rights.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\Lumen — roams with the user profile: settings.</summary>
    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumen");

    /// <summary>%LOCALAPPDATA%\Lumen — machine specific: logs and downloaded models.</summary>
    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");

    public static string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(LocalDirectory, "logs");

    /// <summary>Suggested place for downloaded ONNX models.</summary>
    public static string ModelsDirectory => Path.Combine(LocalDirectory, "models");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Lumen.exe");
}
