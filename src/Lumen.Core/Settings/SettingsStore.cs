using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lumen.Core.Settings;

/// <summary>Encrypts small secrets (the API key) before they are written to disk.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <returns>The decrypted value, or null when the data cannot be decrypted (e.g. copied from another user account).</returns>
    string? Unprotect(string protectedText);
}

/// <summary>Read access to the current settings, plus a notification when they change.</summary>
public interface ISettingsProvider
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// Two details matter for a settings file that is written while the app runs:
/// <list type="bullet">
/// <item>Writes are atomic: we write a temp file and then replace the real one, so a crash
/// or power loss mid-write can never leave a half-written, unreadable file.</item>
/// <item>A corrupt file is not fatal: it is renamed to *.corrupt-{timestamp} and defaults are used.</item>
/// </list>
/// </remarks>
public sealed class SettingsStore : ISettingsProvider
{
    private readonly string _filePath;
    private readonly ISecretProtector _protector;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _gate = new();
    private AppSettings _current = new();

    public SettingsStore(string filePath, ISecretProtector protector, ILogger<SettingsStore> logger)
    {
        _filePath = filePath;
        _protector = protector;
        _logger = logger;
    }

    public event EventHandler<AppSettings>? Changed;

    public string FilePath => _filePath;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public AppSettings Load()
    {
        AppSettings loaded;
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                Normalize(loaded);
            }
            else
            {
                loaded = new AppSettings();
                Save(loaded);
            }
        }
        catch (JsonException ex)
        {
            string backup = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            _logger.LogError(ex, "Settings file is not valid JSON; moving it to {Backup} and using defaults", backup);
            File.Move(_filePath, backup, overwrite: true);
            loaded = new AppSettings();
            Save(loaded);
        }

        lock (_gate)
        {
            _current = loaded;
        }

        return loaded;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(temp, _filePath, overwrite: true);

        lock (_gate)
        {
            _current = settings;
        }

        _logger.LogInformation("Settings saved to {Path}", _filePath);
        Changed?.Invoke(this, settings);
    }

    /// <summary>Returns the Claude API key: the encrypted one from settings, else the ANTHROPIC_API_KEY environment variable.</summary>
    public string? GetApiKey() => ResolveApiKey(Current, _protector);

    public static string? ResolveApiKey(AppSettings settings, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        if (!string.IsNullOrEmpty(settings.Claude.ProtectedApiKey))
        {
            string? key = protector.Unprotect(settings.Claude.ProtectedApiKey);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key.Trim();
            }
        }

        string? env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public void SetApiKey(AppSettings settings, string? plainKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Claude.ProtectedApiKey = string.IsNullOrWhiteSpace(plainKey) ? null : _protector.Protect(plainKey.Trim());
    }

    /// <summary>Clamps values a hand-edited file might have broken, and upgrades old schema versions.</summary>
    internal static void Normalize(AppSettings s)
    {
        s.Hotkeys ??= new HotkeySettings();
        s.Local ??= new LocalModelSettings();
        s.Claude ??= new ClaudeSettings();
        s.Capture ??= new CaptureSettings();
        s.General ??= new GeneralSettings();
        s.CustomActions ??= [];
        s.Capture.ClipboardFallbackExcludedProcesses ??= [];

        s.Local.MaxContextTokens = Math.Clamp(s.Local.MaxContextTokens, 512, 131_072);
        s.Local.MaxOutputTokens = Math.Clamp(s.Local.MaxOutputTokens, 16, s.Local.MaxContextTokens);
        s.Local.Temperature = Math.Clamp(s.Local.Temperature, 0.0, 2.0);
        s.Local.TopP = Math.Clamp(s.Local.TopP, 0.05, 1.0);
        s.Local.UnloadAfterIdleMinutes = Math.Clamp(s.Local.UnloadAfterIdleMinutes, 0, 24 * 60);
        s.Claude.MaxOutputTokens = Math.Clamp(s.Claude.MaxOutputTokens, 256, 128_000);
        s.Capture.MaxCapturedChars = Math.Clamp(s.Capture.MaxCapturedChars, 1_000, 500_000);

        if (string.IsNullOrWhiteSpace(s.Claude.Model))
        {
            s.Claude.Model = new ClaudeSettings().Model;
        }

        s.Claude.Effort = (s.Claude.Effort ?? "").Trim().ToLowerInvariant();
        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }
}
