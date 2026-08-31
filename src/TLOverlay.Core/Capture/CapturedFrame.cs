using System.Buffers;

namespace TLOverlay.Core.Capture;

/// <summary>
/// One captured image as a plain managed BGRA buffer.
///
/// Everything downstream - cropping, change detection, preprocessing - is array
/// work on this, which keeps the graphics interop confined to the single hop
/// that produces it.
///
/// A frame at 1080p is 8 MB, which lands on the large object heap and is
/// produced several times a second. Allocating one per frame cost gigabytes of
/// working set within a minute, so frames that live for one pipeline pass are
/// rented from a pool and returned by <see cref="Dispose"/>. A rented buffer is
/// usually larger than the image; every read here goes through Stride and
/// Height rather than the array's length, so the slack is never touched.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    private readonly Action<byte[]>? _return;
    private byte[]? _pixels;

    public CapturedFrame(byte[] pixels, int width, int height, int stride)
        : this(pixels, width, height, stride, onDispose: null)
    {
    }

    private CapturedFrame(byte[] pixels, int width, int height, int stride, Action<byte[]>? onDispose)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * BytesPerPixel);

        if (pixels.Length < ((height - 1) * stride) + (width * BytesPerPixel))
        {
            throw new ArgumentException("Pixel buffer is smaller than the described image.", nameof(pixels));
        }

        _pixels = pixels;
        _return = onDispose;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public const int BytesPerPixel = 4;

    /// <summary>
    /// Takes a tightly packed frame buffer from the shared pool. The caller
    /// fills <paramref name="buffer"/>, and disposing the frame gives it back.
    /// </summary>
    public static CapturedFrame Rent(int width, int height, out byte[] buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        int stride = width * BytesPerPixel;
        buffer = ArrayPool<byte>.Shared.Rent(stride * height);

        return new CapturedFrame(buffer, width, height, stride, ReturnToSharedPool);
    }

    /// <summary>
    /// Wraps a buffer the caller owns, handing back its lifetime: the frame
    /// calls <paramref name="onDispose"/> once, when it is disposed.
    ///
    /// Exists because one caller cannot use the shared pool - DataReader fills
    /// every byte of the array it is given, so that path needs a buffer of
    /// exactly the frame's size, and ArrayPool rounds up.
    /// </summary>
    public static CapturedFrame Adopt(
        byte[] buffer,
        int width,
        int height,
        int stride,
        Action<byte[]>? onDispose) =>
        new(buffer, width, height, stride, onDispose);

    /// <summary>
    /// The pixels. Throws once the frame has been disposed rather than handing
    /// back a buffer somebody else is now filling - a silent read of a recycled
    /// array would show up as garbled OCR long after the mistake.
    /// </summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>
    /// Copies out a sub-rectangle, clamped to the frame. Cropping on the CPU is
    /// cheap next to OCR and avoids a second GPU round trip per region.
    /// </summary>
    public CapturedFrame Crop(int x, int y, int width, int height)
    {
        byte[] source = Pixels;

        x = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);

        CapturedFrame crop = Rent(width, height, out byte[] destination);
        int destinationStride = crop.Stride;

        for (int row = 0; row < height; row++)
        {
            int sourceOffset = ((y + row) * Stride) + (x * BytesPerPixel);
            Buffer.BlockCopy(source, sourceOffset, destination, row * destinationStride, destinationStride);
        }

        return crop;
    }

    public byte[] Signature() =>
        Pipeline.FrameSignature.FromBgra(Pixels, Width, Height, Stride);

    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);

        if (pixels is not null)
        {
            _return?.Invoke(pixels);
        }
    }

    // Not cleared: the next tenant overwrites every byte it goes on to read.
    private static void ReturnToSharedPool(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
}
