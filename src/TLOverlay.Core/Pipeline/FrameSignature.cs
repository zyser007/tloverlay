namespace TLOverlay.Core.Pipeline;

/// <summary>
/// Reduces a captured region to a tiny grayscale fingerprint. Comparing
/// fingerprints is how the pipeline avoids running OCR on frames that did not
/// change - which is the overwhelming majority of them.
/// </summary>
public static class FrameSignature
{
    /// <summary>Width and height, in cells, of a signature.</summary>
    public const int Size = 16;

    /// <summary>Number of bytes in a signature.</summary>
    public const int Length = Size * Size;

    /// <summary>
    /// Box-averages a BGRA buffer down to a <see cref="Size"/>x<see cref="Size"/>
    /// grayscale signature.
    /// </summary>
    /// <param name="pixels">BGRA8 pixel data.</param>
    /// <param name="width">Pixel width of the region.</param>
    /// <param name="height">Pixel height of the region.</param>
    /// <param name="stride">Bytes per row, which may exceed <c>width * 4</c>.</param>
    public static byte[] FromBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);

        if (pixels.Length < ((height - 1) * stride) + (width * 4))
        {
            throw new ArgumentException("Pixel buffer is smaller than the described image.", nameof(pixels));
        }

        var signature = new byte[Length];

        // Map each cell back to a source rectangle rather than mapping pixels
        // forward into cells. Forward mapping leaves cells unsampled whenever the
        // region is under 16 pixels on an axis - which a subtitle strip routinely
        // is - and those empty cells then read as pure black in every comparison.
        for (int cellY = 0; cellY < Size; cellY++)
        {
            int y0 = cellY * height / Size;
            int y1 = Math.Max(((cellY + 1) * height / Size), y0 + 1);
            y1 = Math.Min(y1, height);

            for (int cellX = 0; cellX < Size; cellX++)
            {
                int x0 = cellX * width / Size;
                int x1 = Math.Max(((cellX + 1) * width / Size), x0 + 1);
                x1 = Math.Min(x1, width);

                long total = 0;
                int samples = 0;

                for (int y = y0; y < y1; y++)
                {
                    int rowStart = y * stride;

                    for (int x = x0; x < x1; x++)
                    {
                        int p = rowStart + (x * 4);

                        // BT.601 luma, integer form. Good enough for change
                        // detection and far cheaper than a float conversion.
                        total += ((pixels[p + 2] * 299) + (pixels[p + 1] * 587) + (pixels[p] * 114)) / 1000;
                        samples++;
                    }
                }

                signature[(cellY * Size) + cellX] = samples == 0 ? (byte)0 : (byte)(total / samples);
            }
        }

        return signature;
    }

    /// <summary>
    /// Mean absolute difference between two signatures, on the same 0-255 scale
    /// as the underlying luma values.
    /// </summary>
    public static double Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Signatures must be the same length.", nameof(b));
        }

        if (a.Length == 0)
        {
            return 0;
        }

        long total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return (double)total / a.Length;
    }
}
