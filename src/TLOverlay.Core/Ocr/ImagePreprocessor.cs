using TLOverlay.Core.Capture;

namespace TLOverlay.Core.Ocr;

public sealed class PreprocessOptions
{
    /// <summary>
    /// Target height, in pixels, for a line of text. Windows OCR is trained on
    /// document-scale text and reads game fonts far better when the region is
    /// scaled up to something document-like.
    /// </summary>
    public int TargetHeight { get; set; } = 480;

    public int MaxDimension { get; set; } = 2600;

    /// <summary>
    /// Flip light-on-dark text to dark-on-light. Nearly all game dialogue is
    /// light text over a dark panel, and the recogniser is measurably better on
    /// the document-like polarity.
    /// </summary>
    public bool AutoInvert { get; set; } = true;

    public bool StretchContrast { get; set; } = true;

    public static PreprocessOptions Default { get; } = new();
}

/// <summary>
/// Cleans a captured region up before OCR.
///
/// This step earns its keep: game fonts are thin, anti-aliased, often outlined
/// or shadowed, and sit at HUD scale. Feeding the raw crop to the recogniser
/// gives noticeably worse text than feeding it an upscaled, contrast-stretched,
/// correctly-polarised version of the same crop.
/// </summary>
public static class ImagePreprocessor
{
    public static CapturedFrame Prepare(CapturedFrame frame, PreprocessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options ??= PreprocessOptions.Default;

        byte[] luma = ExtractLuma(frame);

        if (options.AutoInvert && MeanOf(luma) < 128)
        {
            Invert(luma);
        }

        if (options.StretchContrast)
        {
            StretchContrast(luma);
        }

        double scale = ChooseScale(frame.Width, frame.Height, options);

        return Math.Abs(scale - 1.0) < 0.001
            ? FromLuma(luma, frame.Width, frame.Height)
            : ResampleLuma(luma, frame.Width, frame.Height, scale);
    }

    internal static double ChooseScale(int width, int height, PreprocessOptions options)
    {
        if (width <= 0 || height <= 0)
        {
            return 1;
        }

        int longest = Math.Max(width, height);

        // Windows OCR rejects images past its maximum dimension outright, so an
        // oversized region (a whole-screen sweep at 4K) has to come down rather
        // than go up.
        if (longest > options.MaxDimension)
        {
            return (double)options.MaxDimension / longest;
        }

        // Past about 4x the text is already larger than anything the recogniser
        // benefits from, and the cost is quadratic.
        double scale = Math.Clamp((double)options.TargetHeight / height, 1.0, 4.0);

        // Never let the upscale push us over the limit we just guarded against.
        double ceiling = (double)options.MaxDimension / longest;
        return Math.Min(scale, Math.Max(1.0, ceiling));
    }

    internal static byte[] ExtractLuma(CapturedFrame frame)
    {
        var luma = new byte[frame.Width * frame.Height];

        for (int y = 0; y < frame.Height; y++)
        {
            int rowStart = y * frame.Stride;
            int outRow = y * frame.Width;

            for (int x = 0; x < frame.Width; x++)
            {
                int p = rowStart + (x * CapturedFrame.BytesPerPixel);
                luma[outRow + x] = (byte)(
                    ((frame.Pixels[p + 2] * 299) + (frame.Pixels[p + 1] * 587) + (frame.Pixels[p] * 114)) / 1000);
            }
        }

        return luma;
    }

    private static double MeanOf(byte[] luma)
    {
        if (luma.Length == 0)
        {
            return 0;
        }

        long total = 0;
        foreach (byte value in luma)
        {
            total += value;
        }

        return (double)total / luma.Length;
    }

    private static void Invert(byte[] luma)
    {
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = (byte)(255 - luma[i]);
        }
    }

    /// <summary>
    /// Stretches the 2nd..98th percentile to full range. Percentiles rather than
    /// min/max so a single blown-out highlight or a black outline pixel cannot
    /// flatten the whole region.
    /// </summary>
    internal static void StretchContrast(byte[] luma)
    {
        if (luma.Length == 0)
        {
            return;
        }

        Span<int> histogram = stackalloc int[256];
        foreach (byte value in luma)
        {
            histogram[value]++;
        }

        int lowTarget = (int)(luma.Length * 0.02);
        int highTarget = (int)(luma.Length * 0.98);

        int low = 0;
        int high = 255;
        int running = 0;

        for (int i = 0; i < 256; i++)
        {
            running += histogram[i];
            if (running >= lowTarget)
            {
                low = i;
                break;
            }
        }

        running = 0;
        for (int i = 0; i < 256; i++)
        {
            running += histogram[i];
            if (running >= highTarget)
            {
                high = i;
                break;
            }
        }

        if (high - low < 16)
        {
            // Nearly flat region - stretching would only amplify noise.
            return;
        }

        int range = high - low;
        Span<byte> map = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
        {
            int scaled = (i - low) * 255 / range;
            map[i] = (byte)Math.Clamp(scaled, 0, 255);
        }

        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = map[luma[i]];
        }
    }

    private static CapturedFrame FromLuma(byte[] luma, int width, int height)
    {
        int stride = width * CapturedFrame.BytesPerPixel;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = luma[(y * width) + x];
                int p = (y * stride) + (x * CapturedFrame.BytesPerPixel);
                pixels[p] = value;
                pixels[p + 1] = value;
                pixels[p + 2] = value;
                pixels[p + 3] = 255;
            }
        }

        return new CapturedFrame(pixels, width, height, stride);
    }

    /// <summary>
    /// Bilinear resample. Nearest-neighbour is faster but leaves the staircase
    /// edges that thin game fonts can least afford.
    /// </summary>
    private static CapturedFrame ResampleLuma(byte[] luma, int width, int height, double scale)
    {
        int newWidth = Math.Max(1, (int)Math.Round(width * scale));
        int newHeight = Math.Max(1, (int)Math.Round(height * scale));
        int stride = newWidth * CapturedFrame.BytesPerPixel;
        var pixels = new byte[stride * newHeight];

        for (int y = 0; y < newHeight; y++)
        {
            double sourceY = ((y + 0.5) / scale) - 0.5;
            int y0 = (int)Math.Floor(sourceY);
            double fy = sourceY - y0;
            int y0c = Math.Clamp(y0, 0, height - 1);
            int y1c = Math.Clamp(y0 + 1, 0, height - 1);

            for (int x = 0; x < newWidth; x++)
            {
                double sourceX = ((x + 0.5) / scale) - 0.5;
                int x0 = (int)Math.Floor(sourceX);
                double fx = sourceX - x0;
                int x0c = Math.Clamp(x0, 0, width - 1);
                int x1c = Math.Clamp(x0 + 1, 0, width - 1);

                double top = (luma[(y0c * width) + x0c] * (1 - fx)) + (luma[(y0c * width) + x1c] * fx);
                double bottom = (luma[(y1c * width) + x0c] * (1 - fx)) + (luma[(y1c * width) + x1c] * fx);
                byte value = (byte)Math.Clamp((top * (1 - fy)) + (bottom * fy), 0, 255);

                int p = (y * stride) + (x * CapturedFrame.BytesPerPixel);
                pixels[p] = value;
                pixels[p + 1] = value;
                pixels[p + 2] = value;
                pixels[p + 3] = 255;
            }
        }

        return new CapturedFrame(pixels, newWidth, newHeight, stride);
    }
}
