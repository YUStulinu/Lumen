using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Lumen.App.Interop;
using Lumen.Core.Prompts;
using Lumen.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Services;

/// <summary>
/// Finds out what the user has selected in whatever application is in the foreground.
/// </summary>
/// <remarks>
/// Strategy, cheapest and least intrusive first:
/// <list type="number">
/// <item><b>UI Automation</b>: the accessibility API screen readers use. If the focused control
///   implements the Text pattern (Notepad, Word, Edge/Chrome documents, Windows Terminal, most
///   WPF/WinUI apps...) we can read its selection directly, without touching the clipboard.</item>
/// <item><b>Simulated Ctrl+C</b>: works almost everywhere else (Electron apps, many editors),
///   at the cost of briefly using the clipboard, which we restore.</item>
/// </list>
/// </remarks>
public sealed class SelectionCaptureService
{
    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromMilliseconds(700);

    private readonly ClipboardService _clipboard;
    private readonly ISettingsProvider _settings;
    private readonly ILogger<SelectionCaptureService> _logger;

    public SelectionCaptureService(ClipboardService clipboard, ISettingsProvider settings, ILogger<SelectionCaptureService> logger)
    {
        _clipboard = clipboard;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Describes the foreground window. Call this first, before any Lumen window is shown.</summary>
    public static (nint Window, string? ProcessName, string? Title) GetForegroundInfo()
    {
        nint hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return (0, null, null);
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string? processName = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // The process exited in the meantime.
        }

        return (hwnd, processName, NativeMethods.GetWindowTitle(hwnd));
    }

    public async Task<CapturedContext> CaptureAsync(CancellationToken cancellationToken = default)
    {
        (nint hwnd, string? processName, string? title) = GetForegroundInfo();
        CaptureSettings options = _settings.Current.Capture;

        if (hwnd == 0 || processName is null || IsOwnProcess(hwnd))
        {
            return CapturedContext.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        string? text = await ReadWithUiAutomationAsync();
        CaptureSource source = CaptureSource.UiAutomation;

        if (string.IsNullOrWhiteSpace(text) && options.UseClipboardFallback)
        {
            bool excluded = options.ClipboardFallbackExcludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase);
            if (excluded)
            {
                _logger.LogInformation("Not sending Ctrl+C to {Process}: it is on the exclusion list (terminals)", processName);
            }
            else
            {
                text = await _clipboard.CopySelectionAsync(cancellationToken);
                source = CaptureSource.Clipboard;
            }
        }

        _logger.LogInformation(
            "Selection capture from {Process}: {Chars} chars via {Source} in {Ms} ms",
            processName, text?.Length ?? 0, string.IsNullOrWhiteSpace(text) ? "nothing" : source, stopwatch.ElapsedMilliseconds);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new CapturedContext("", CaptureSource.None, processName, title, TargetWindow: hwnd);
        }

        return new CapturedContext(PromptBuilder.Truncate(text, options.MaxCapturedChars), source, processName, title, TargetWindow: hwnd);
    }

    /// <summary>
    /// Runs on a thread-pool (MTA) thread with a timeout: UI Automation talks to the target
    /// process over cross-process COM, and a hung application must not freeze Lumen.
    /// </summary>
    private async Task<string?> ReadWithUiAutomationAsync()
    {
        Task<string?> read = Task.Run(ReadSelectionFromFocusedElement);
        Task finished = await Task.WhenAny(read, Task.Delay(UiAutomationTimeout));
        if (finished != read)
        {
            _logger.LogInformation("UI Automation did not answer within {Timeout} ms", UiAutomationTimeout.TotalMilliseconds);
            return null;
        }

        return await read;
    }

    private string? ReadSelectionFromFocusedElement()
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return null;
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out object patternObject) || patternObject is not TextPattern textPattern)
            {
                return null;
            }

            if (textPattern.SupportedTextSelection == SupportedTextSelection.None)
            {
                return null;
            }

            TextPatternRange[] ranges = textPattern.GetSelection();
            var sb = new StringBuilder();
            foreach (TextPatternRange range in ranges)
            {
                // -1 = no length limit; the capture is truncated later anyway.
                string part = range.GetText(-1);
                if (!string.IsNullOrEmpty(part))
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }

                    sb.Append(part);
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _logger.LogDebug("UI Automation could not read the selection: {Message}", ex.Message);
            return null;
        }
    }

    private static bool IsOwnProcess(nint hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
