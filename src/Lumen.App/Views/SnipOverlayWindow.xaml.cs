using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Lumen.App.Interop;
using Lumen.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Lumen.App.Views;

/// <summary>
/// Full-screen overlay for one monitor that lets the user drag a rectangle over a frozen screenshot.
/// </summary>
public partial class SnipOverlayWindow : Window
{
    private const double MinimumSelection = 6; // DIPs; smaller drags are treated as accidental clicks

    private Point? _start;

    public SnipOverlayWindow(MonitorShot shot)
    {
        ArgumentNullException.ThrowIfNull(shot);
        InitializeComponent();
        Shot = shot;
        Screenshot.Source = shot.ToBitmapSource();
        Loaded += (_, _) => UpdateDim(null);
        SizeChanged += (_, _) => UpdateDim(null);
    }

    public MonitorShot Shot { get; }

    /// <summary>The user finished a selection; the rectangle is in the monitor's physical pixels.</summary>
    public event EventHandler<DrawingRectangle>? RegionSelected;

    public event EventHandler? Cancelled;

    /// <summary>
    /// Places the window exactly over its monitor, then shows it maximized.
    /// </summary>
    /// <remarks>
    /// WPF positions windows in device-independent units relative to the primary monitor's DPI,
    /// which gets ambiguous with several monitors at different scales. So we create the native
    /// window first (EnsureHandle), move it onto the right monitor in physical pixels with
    /// SetWindowPos, and only then maximize it: maximizing always fills the monitor the window is on.
    /// </remarks>
    public void ShowOnMonitor()
    {
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        DrawingRectangle b = Shot.Bounds;
        NativeMethods.SetWindowPos(hwnd, 0, b.X + 8, b.Y + 8, Math.Max(100, b.Width / 2), Math.Max(100, b.Height / 2),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        WindowState = WindowState.Maximized;
        Show();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _start = e.GetPosition(Root);
        Hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelection(new Rect(start, e.GetPosition(Root)));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        if (_start is not { } start)
        {
            return;
        }

        _start = null;
        var selection = new Rect(start, e.GetPosition(Root));
        if (selection.Width < MinimumSelection || selection.Height < MinimumSelection)
        {
            UpdateDim(null);
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            return;
        }

        RegionSelected?.Invoke(this, ToPhysicalPixels(selection));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateSelection(Rect selection)
    {
        UpdateDim(selection);

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Margin = new Thickness(selection.X, selection.Y, 0, 0);
        SelectionBorder.Width = selection.Width;
        SelectionBorder.Height = selection.Height;

        DrawingRectangle pixels = ToPhysicalPixels(selection);
        SizeText.Text = $"{pixels.Width} × {pixels.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        double labelTop = selection.Y > 28 ? selection.Y - 26 : selection.Bottom + 6;
        SizeLabel.Margin = new Thickness(selection.X, labelTop, 0, 0);
    }

    private void UpdateDim(Rect? selection)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        Dim.Data = selection is { } s
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(s))
            : full;
    }

    /// <summary>
    /// The window covers the whole monitor, so a point at x DIPs is at x / ActualWidth of the
    /// monitor's width in pixels. This ratio is independent of the monitor's DPI scale.
    /// </summary>
    private DrawingRectangle ToPhysicalPixels(Rect r)
    {
        double sx = Shot.Bounds.Width / Math.Max(1, Root.ActualWidth);
        double sy = Shot.Bounds.Height / Math.Max(1, Root.ActualHeight);
        int x = (int)Math.Floor(r.X * sx);
        int y = (int)Math.Floor(r.Y * sy);
        int right = (int)Math.Ceiling(r.Right * sx);
        int bottom = (int)Math.Ceiling(r.Bottom * sy);
        return new DrawingRectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
}
