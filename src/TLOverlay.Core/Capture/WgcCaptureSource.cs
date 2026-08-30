using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace TLOverlay.Core.Capture;

/// <summary>
/// Captures a single window through Windows.Graphics.Capture.
///
/// Per-window rather than per-monitor for two reasons: the overlay never appears
/// in the captured image (so OCR cannot read back its own Thai output), and the
/// frames arrive already cropped to the game.
/// </summary>
public sealed class WgcCaptureSource : ICaptureSource
{
    private readonly object _gate = new();

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private TaskCompletionSource<CapturedFrame?>? _pendingGrab;
    private SizeInt32 _lastSize;
    private bool _disposed;

    /// <summary>
    /// False on builds older than Windows 10 2004, where the caller should fall
    /// back to a GDI capture path.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (TypeLoadException)
            {
                return false;
            }
        }
    }

    public bool IsRunning => _session is not null;

    public void Start(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            StopCore();

            _device = CaptureInterop.CreateDirect3DDevice();
            _item = CaptureInterop.CreateItemForWindow(windowHandle);
            _lastSize = _item.Size;

            // Free-threaded so frames do not need a DispatcherQueue; the overlay's
            // UI thread must never be on the capture path.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                _lastSize);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            ApplySessionOptions(_session);

            // If the game exits, the item closes and we release everything rather
            // than leaving a dead session holding a device.
            _item.Closed += OnItemClosed;

            _session.StartCapture();
        }
    }

    /// <summary>
    /// Turns off the cursor and, where the OS supports it, the yellow capture
    /// border. Both properties post-date the base capture API, so they are probed
    /// rather than assumed - on older Windows 10 the border simply stays.
    /// </summary>
    private static void ApplySessionOptions(GraphicsCaptureSession session)
    {
        const string SessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";

        try
        {
            if (ApiInformation.IsPropertyPresent(SessionType, "IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled = false;
            }

            if (ApiInformation.IsPropertyPresent(SessionType, "IsBorderRequired"))
            {
                session.IsBorderRequired = false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Border removal can be denied by policy. Not fatal - capture works,
            // it just looks worse.
        }
    }

    public Task<CapturedFrame?> GrabAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRunning)
        {
            return Task.FromResult<CapturedFrame?>(null);
        }

        var tcs = new TaskCompletionSource<CapturedFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A grab that is superseded before a frame arrives completes as null
        // rather than hanging; the caller polls again anyway.
        Interlocked.Exchange(ref _pendingGrab, tcs)?.TrySetResult(null);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state =>
                ((TaskCompletionSource<CapturedFrame?>)state!).TrySetCanceled(),
                tcs);
        }

        return tcs.Task;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        if (frame.ContentSize.Width != _lastSize.Width || frame.ContentSize.Height != _lastSize.Height)
        {
            // The player resized the game or changed resolution. Recreating the
            // pool is what keeps frames from arriving letterboxed or stretched.
            _lastSize = frame.ContentSize;

            if (_device is not null && _lastSize.Width > 0 && _lastSize.Height > 0)
            {
                sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _lastSize);
            }
        }

        var pending = Interlocked.Exchange(ref _pendingGrab, null);
        if (pending is null)
        {
            // Nobody asked for this frame. Dropping it here is the whole point of
            // the pull model: no GPU-to-CPU copy at display refresh rate.
            frame.Dispose();
            return;
        }

        _ = CompleteGrabAsync(frame, pending);
    }

    private static async Task CompleteGrabAsync(
        Direct3D11CaptureFrame frame,
        TaskCompletionSource<CapturedFrame?> completion)
    {
        try
        {
            using (frame)
            {
                using SoftwareBitmap bitmap = await SoftwareBitmap
                    .CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);

                completion.TrySetResult(SoftwareBitmapInterop.ToFrame(bitmap));
            }
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => Stop();

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        Interlocked.Exchange(ref _pendingGrab, null)?.TrySetResult(null);

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        _session?.Dispose();
        _session = null;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
