using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Lumen.App.Services;

/// <summary>
/// Text recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr).
/// </summary>
/// <remarks>
/// <para>Nothing to install or ship: the engine is part of Windows and works offline. It recognizes
/// the languages whose OCR components are installed (they come with Windows display/input
/// languages; more can be added in Settings → Time &amp; language → Language).</para>
/// <para>This is a WinRT API. Targeting net9.0-windows10.0.19041.0 lets C# call it directly,
/// and <c>await</c> works on WinRT IAsyncOperation thanks to the projection's extension methods.</para>
/// </remarks>
public sealed class OcrService
{
    /// <summary>Small crops are upscaled to at least this width: OCR accuracy drops sharply on tiny glyphs.</summary>
    private const uint MinimumWidthForOcr = 1200;

    private readonly ILogger<OcrService> _logger;

    public OcrService(ILogger<OcrService> logger) => _logger = logger;

    /// <summary>Languages the OCR engine can recognize on this PC, as BCP-47 tags (e.g. "en-US").</summary>
    public static IReadOnlyList<string> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public async Task<string> RecognizeAsync(byte[] png, string? languageTag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        OcrEngine engine = CreateEngine(languageTag);

        // PNG bytes → WinRT stream → decoder → SoftwareBitmap (the input type OCR wants).
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

        (uint width, uint height) = ChooseOcrSize(decoder.PixelWidth, decoder.PixelHeight, OcrEngine.MaxImageDimension);
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant, // high quality resampling
        };

        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        string text = string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));

        _logger.LogInformation(
            "OCR ({Language}) on {W}x{H} image: {Lines} lines, {Chars} chars",
            engine.RecognizerLanguage.LanguageTag, width, height, result.Lines.Count, text.Length);
        return text;
    }

    /// <summary>Upscales small images and keeps large ones within the engine's limit, preserving the aspect ratio.</summary>
    internal static (uint Width, uint Height) ChooseOcrSize(uint width, uint height, uint maxDimension)
    {
        double scale = 1.0;
        if (width < MinimumWidthForOcr)
        {
            scale = Math.Min(3.0, (double)MinimumWidthForOcr / Math.Max(width, 1));
        }

        double largest = Math.Max(width, height) * scale;
        if (largest > maxDimension)
        {
            scale *= maxDimension / largest;
        }

        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private OcrEngine CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var language = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is { } specific)
                {
                    return specific;
                }

                _logger.LogWarning("OCR language {Language} is not installed; using the user profile languages", languageTag);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning("'{Language}' is not a valid language tag", languageTag);
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows has no OCR language installed. Add a language with OCR support in Windows Settings → Time & language → Language & region.");
    }
}
