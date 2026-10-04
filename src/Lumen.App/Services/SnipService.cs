using Lumen.App.Interop;
using Lumen.App.Views;
using Microsoft.Extensions.Logging;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Lumen.App.Services;

/// <summary>The region the user selected, already cropped and PNG-encoded.</summary>
public sealed record SnipResult(byte[] Png, int Width, int Height);

/// <summary>
/// Runs the "select a screen region" interaction across all monitors.
/// </summary>
/// <remarks>
/// The overlays raise events; a <see cref="TaskCompletionSource{TResult}"/> turns "whichever
/// overlay finishes first" into a single awaitable result, so the caller can simply write
/// <c>var snip = await snipService.SelectRegionAsync();</c>.
/// </remarks>
public sealed class SnipService
{
    private readonly ILogger<SnipService> _logger;
    private bool _active;

    public SnipService(ILogger<SnipService> logger) => _logger = logger;

    /// <returns>The selected region, or null if the user cancelled.</returns>
    public async Task<SnipResult?> SelectRegionAsync()
    {
        if (_active)
        {
            return null; // the hotkey was pressed again while the overlay is open
        }

        _active = true;
        IReadOnlyList<MonitorShot> shots = ScreenCaptureService.CaptureAllMonitors();
        var windows = new List<SnipOverlayWindow>();
        try
        {
            var completion = new TaskCompletionSource<(MonitorShot Shot, DrawingRectangle Region)?>();
            foreach (MonitorShot shot in shots)
            {
                var window = new SnipOverlayWindow(shot);
                window.RegionSelected += (_, region) => completion.TrySetResult((window.Shot, region));
                window.Cancelled += (_, _) => completion.TrySetResult(null);
                window.Closed += (_, _) => completion.TrySetResult(null);
                windows.Add(window);
                window.ShowOnMonitor();
            }

            ActivateWindowUnderCursor(windows);

            (MonitorShot Shot, DrawingRectangle Region)? selection = await completion.Task;
            if (selection is not { } chosen)
            {
                _logger.LogInformation("Screen region selection cancelled");
                return null;
            }

            byte[] png = chosen.Shot.CropToPng(chosen.Region);
            _logger.LogInformation("Selected a {W}x{H} screen region", chosen.Region.Width, chosen.Region.Height);
            return new SnipResult(png, chosen.Region.Width, chosen.Region.Height);
        }
        finally
        {
            foreach (SnipOverlayWindow window in windows)
            {
                window.Close();
            }

            foreach (MonitorShot shot in shots)
            {
                shot.Dispose();
            }

            _active = false;
        }
    }

    /// <summary>Gives keyboard focus (for Esc) to the overlay on the monitor where the mouse is.</summary>
    private static void ActivateWindowUnderCursor(List<SnipOverlayWindow> windows)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
        SnipOverlayWindow? target = windows.FirstOrDefault(w => w.Shot.Bounds.Contains(cursor.X, cursor.Y)) ?? windows.FirstOrDefault();
        target?.Activate();
        target?.Focus();
    }
}
