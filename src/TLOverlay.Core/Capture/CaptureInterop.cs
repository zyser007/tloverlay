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

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>Moves pixels between SoftwareBitmap and plain managed arrays.</summary>
internal static unsafe class SoftwareBitmapInterop
{
    public static CapturedFrame ToFrame(SoftwareBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        var description = buffer.GetPlaneDescription(0);
        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* data, out uint capacity);

        int width = description.Width;
        int height = description.Height;
        int sourceStride = description.Stride;
        int destinationStride = width * CapturedFrame.BytesPerPixel;

        var pixels = new byte[destinationStride * height];

        for (int row = 0; row < height; row++)
        {
            int sourceOffset = description.StartIndex + (row * sourceStride);
            if (sourceOffset + destinationStride > capacity)
            {
                break;
            }

            Marshal.Copy(
                (IntPtr)(data + sourceOffset),
                pixels,
                row * destinationStride,
                destinationStride);
        }

        return new CapturedFrame(pixels, width, height, destinationStride);
    }

    public static SoftwareBitmap ToSoftwareBitmap(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            frame.Width,
            frame.Height,
            BitmapAlphaMode.Premultiplied);

        using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            var description = buffer.GetPlaneDescription(0);
            ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* data, out uint capacity);

            int rowBytes = frame.Width * CapturedFrame.BytesPerPixel;

            for (int row = 0; row < frame.Height; row++)
            {
                int destinationOffset = description.StartIndex + (row * description.Stride);
                if (destinationOffset + rowBytes > capacity)
                {
                    break;
                }

                Marshal.Copy(
                    frame.Pixels,
                    row * frame.Stride,
                    (IntPtr)(data + destinationOffset),
                    rowBytes);
            }
        }

        return bitmap;
    }
}
