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
        Span<long> sums = stackalloc long[Length];
        Span<int> counts = stackalloc int[Length];

        for (int y = 0; y < height; y++)
        {
            int cellY = y * Size / height;
            int rowStart = y * stride;

            for (int x = 0; x < width; x++)
            {
                int cellX = x * Size / width;
                int p = rowStart + (x * 4);

                // BT.601 luma, integer form. Good enough for change detection and
                // far cheaper than a float conversion per pixel.
                int luma = ((pixels[p + 2] * 299) + (pixels[p + 1] * 587) + (pixels[p] * 114)) / 1000;

                int cell = (cellY * Size) + cellX;
                sums[cell] += luma;
                counts[cell]++;
            }
        }

        for (int i = 0; i < Length; i++)
        {
            signature[i] = counts[i] == 0 ? (byte)0 : (byte)(sums[i] / counts[i]);
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
