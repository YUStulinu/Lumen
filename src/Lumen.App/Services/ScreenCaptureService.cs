using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Lumen.App.Services;

/// <summary>A frozen picture of one monitor, in physical pixels.</summary>
public sealed class MonitorShot : IDisposable
{
    public MonitorShot(Rectangle bounds, Bitmap bitmap)
    {
        Bounds = bounds;
        Bitmap = bitmap;
    }

    /// <summary>Position and size of the monitor on the virtual desktop, in physical pixels.</summary>
    public Rectangle Bounds { get; }

    public Bitmap Bitmap { get; }

    /// <summary>Converts the GDI+ bitmap into a WPF image source (frozen, so any thread may read it).</summary>
    public BitmapSource ToBitmapSource()
    {
        var rect = new Rectangle(0, 0, Bitmap.Width, Bitmap.Height);
        BitmapData data = Bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(
                data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * data.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            Bitmap.UnlockBits(data);
        }
    }

    /// <summary>Crops a region (in this monitor's pixel coordinates) and encodes it as PNG.</summary>
    public byte[] CropToPng(Rectangle region)
    {
        region.Intersect(new Rectangle(0, 0, Bitmap.Width, Bitmap.Height));
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("The region does not overlap the screenshot.", nameof(region));
        }

        using Bitmap crop = Bitmap.Clone(region, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        crop.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Takes screenshots of every monitor.</summary>
/// <remarks>
/// Because the app is Per-Monitor-V2 DPI aware (see app.manifest), Screen.Bounds and
/// CopyFromScreen work in real physical pixels on every monitor, whatever its scaling.
/// </remarks>
public static class ScreenCaptureService
{
    public static IReadOnlyList<MonitorShot> CaptureAllMonitors()
    {
        var shots = new List<MonitorShot>();
        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            Rectangle bounds = screen.Bounds;
            var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                // BitBlt from the desktop: copies exactly what is on screen right now.
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            shots.Add(new MonitorShot(bounds, bitmap));
        }

        return shots;
    }
}
