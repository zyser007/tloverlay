namespace TLOverlay.Core.Capture;

/// <summary>
/// A pull-based source of frames from one window.
///
/// Pull rather than push on purpose: the compositor can hand us frames at the
/// display's refresh rate, but we only want one every hundred milliseconds or
/// so. Discarding the rest at the source keeps the GPU-to-CPU copy off the hot
/// path.
/// </summary>
public interface ICaptureSource : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Begins capturing the given top-level window.</summary>
    void Start(IntPtr windowHandle);

    void Stop();

    /// <summary>
    /// Waits for the next frame. Returns null if capture stopped before one
    /// arrived - for instance because the game exited.
    /// </summary>
    Task<CapturedFrame?> GrabAsync(CancellationToken cancellationToken = default);
}
