namespace TLOverlay.Core.Capture;

/// <summary>
/// One captured image as a plain managed BGRA buffer.
///
/// Everything downstream - cropping, change detection, preprocessing - is array
/// work on this, which keeps the graphics interop confined to the single hop
/// that produces it.
/// </summary>
public sealed class CapturedFrame
{
    public CapturedFrame(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * BytesPerPixel);

        if (pixels.Length < ((height - 1) * stride) + (width * BytesPerPixel))
        {
            throw new ArgumentException("Pixel buffer is smaller than the described image.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public const int BytesPerPixel = 4;

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>
    /// Copies out a sub-rectangle, clamped to the frame. Cropping on the CPU is
    /// cheap next to OCR and avoids a second GPU round trip per region.
    /// </summary>
    public CapturedFrame Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);

        int destinationStride = width * BytesPerPixel;
        var destination = new byte[destinationStride * height];

        for (int row = 0; row < height; row++)
        {
            int sourceOffset = ((y + row) * Stride) + (x * BytesPerPixel);
            Buffer.BlockCopy(Pixels, sourceOffset, destination, row * destinationStride, destinationStride);
        }

        return new CapturedFrame(destination, width, height, destinationStride);
    }

    public byte[] Signature() =>
        Pipeline.FrameSignature.FromBgra(Pixels, Width, Height, Stride);
}
