using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace TLOverlay.Core.Capture;

/// <summary>
/// The COM plumbing Windows.Graphics.Capture needs and the WinRT projection does
/// not expose.
///
/// Two gaps: creating a capture item for a specific HWND (the projected API only
/// offers the system picker dialog, which we do not want in front of a running
/// game), and building the IDirect3DDevice the frame pool requires.
/// </summary>
internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const uint D3D11SdkVersion = 7;

    /// <summary>Creates a capture item bound to one window, with no picker UI.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Window handle is null.", nameof(windowHandle));
        }

        var interop = GetActivationFactory<IGraphicsCaptureItemInterop>("Windows.Graphics.Capture.GraphicsCaptureItem");

        Guid iid = GraphicsCaptureItemIid;
        IntPtr itemPointer = interop.CreateForWindow(windowHandle, ref iid);

        if (itemPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("The window cannot be captured.");
        }

        try
        {
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    /// <summary>
    /// Creates the Direct3D device the frame pool renders into. Falls back to
    /// WARP so the app still runs on machines with no usable hardware adapter,
    /// where the alternative is failing to start at all.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice()
    {
        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverTypeHardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out IntPtr devicePointer,
            out _,
            out IntPtr contextPointer);

        if (hr != 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverTypeWarp,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out devicePointer,
                out _,
                out contextPointer);
        }

        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr graphicsDevice = IntPtr.Zero;

        try
        {
            Guid dxgiIid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(devicePointer, ref dxgiIid, out dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice));

            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            if (graphicsDevice != IntPtr.Zero)
            {
                Marshal.Release(graphicsDevice);
            }

            if (dxgiDevice != IntPtr.Zero)
            {
                Marshal.Release(dxgiDevice);
            }

            if (contextPointer != IntPtr.Zero)
            {
                Marshal.Release(contextPointer);
            }

            if (devicePointer != IntPtr.Zero)
            {
                Marshal.Release(devicePointer);
            }
        }
    }

    private static T GetActivationFactory<T>(string activatableClassId)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(activatableClassId, activatableClassId.Length, out IntPtr classId));

        try
        {
            Guid iid = typeof(T).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref iid, out IntPtr factory));

            try
            {
                return (T)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, ref Guid iid);

    IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
}

/// <summary>
/// Moves pixels between SoftwareBitmap and plain managed arrays.
///
/// Goes through IBuffer and DataReader rather than IMemoryBufferByteAccess. That
/// COM interface is the usual way to get at the pixels without a copy, but a
/// CsWinRT object cannot be cast to a [ComImport] interface - the runtime does
/// no QueryInterface for that, and it fails at runtime with "Invalid cast from
/// WinRT.IInspectable". The projected path costs one extra copy per frame, which
/// at the eight frames a second this pipeline actually pulls is not worth any
/// amount of interop risk.
/// </summary>
internal static class SoftwareBitmapInterop
{
    /// <summary>
    /// DataWriter.WriteBytes writes the whole array it is handed, so this needs
    /// an exactly sized buffer rather than one from ArrayPool. It runs once per
    /// OCR pass on an image that can itself be megabytes, which is often enough
    /// to be worth not allocating.
    /// </summary>
    private static readonly ExactSizeBufferPool TightRows = new();

    public static SoftwareBitmap ToSoftwareBitmap(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        int rowBytes = frame.Width * CapturedFrame.BytesPerPixel;
        byte[] tight = TightRows.Rent(rowBytes * frame.Height);

        try
        {
            // CopyFromBuffer wants exactly one row after another, so any capture
            // padding has to come out here.
            for (int row = 0; row < frame.Height; row++)
            {
                Buffer.BlockCopy(frame.Pixels, row * frame.Stride, tight, row * rowBytes, rowBytes);
            }

            using var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(tight);

            var bitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                frame.Width,
                frame.Height,
                BitmapAlphaMode.Premultiplied);

            bitmap.CopyFromBuffer(writer.DetachBuffer());
            return bitmap;
        }
        finally
        {
            TightRows.Return(tight);
        }
    }
}

/// <summary>
/// Reads captured bitmaps into managed frames, holding on to the one WinRT
/// buffer it copies through.
///
/// The buffer is the reason this is an object rather than another static
/// method. A fresh Windows.Storage.Streams.Buffer per frame is 8 MB of native
/// memory that the GC cannot see, so nothing about allocating it creates any
/// pressure to collect the small managed wrapper that owns it - the app grew
/// past ten gigabytes in under two minutes doing exactly that. One buffer,
/// reused until the capture size changes, has no such cliff.
/// </summary>
internal sealed class FrameReader
{
    private readonly object _gate = new();
    private readonly ExactSizeBufferPool _pixelPool = new();

    private Windows.Storage.Streams.Buffer? _buffer;
    private uint _capacity;

    public CapturedFrame ToFrame(SoftwareBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        SoftwareBitmap source = bitmap;
        SoftwareBitmap? converted = null;

        // CopyToBuffer needs a format it can lay out linearly.
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
            || bitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
        {
            converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            source = converted;
        }

        try
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int needed = width * height * CapturedFrame.BytesPerPixel;

            lock (_gate)
            {
                Windows.Storage.Streams.Buffer buffer = RentBuffer((uint)needed);

                source.CopyToBuffer(buffer);

                // CopyToBuffer does not always set Length, and a reused buffer is
                // often larger than this frame. Either way the reader has to see
                // at least the frame's worth of bytes to hand them over.
                if (buffer.Length < (uint)needed)
                {
                    buffer.Length = (uint)needed;
                }

                byte[] pixels = _pixelPool.Rent(needed);

                try
                {
                    using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
                    reader.ReadBytes(pixels);
                }
                catch
                {
                    _pixelPool.Return(pixels);
                    throw;
                }

                // Rows are tightly packed: the buffer holds exactly one frame.
                return CapturedFrame.Adopt(
                    pixels,
                    width,
                    height,
                    width * CapturedFrame.BytesPerPixel,
                    _pixelPool.Return);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>Lets go of both buffers, for when capture stops.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buffer = null;
            _capacity = 0;
            _pixelPool.Clear();
        }
    }

    private Windows.Storage.Streams.Buffer RentBuffer(uint needed)
    {
        if (_buffer is null || _capacity < needed)
        {
            // Only grows: a game that goes windowed and back should not pay for
            // a new buffer each way.
            _buffer = new Windows.Storage.Streams.Buffer(needed);
            _capacity = needed;
        }

        return _buffer;
    }
}
