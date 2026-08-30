namespace TLOverlay.App.Interop;

/// <summary>
/// Watches the target game window so the overlay can follow it and get out of
/// the way.
///
/// Two things it catches that polling would miss or be slow about: the player
/// alt-tabbing away (the overlay must vanish immediately, or it sits on top of
/// their browser), and the game window moving or being resized (the overlay must
/// track it, or translations end up over the wrong pixels).
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    // The callback is held in a field because the OS keeps an unmanaged pointer
    // to it; letting it be collected crashes the process on the next event.
    private readonly NativeMethods.WinEventProc _callback;

    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private IntPtr _target;
    private bool _disposed;

    public ForegroundWatcher()
    {
        _callback = OnWinEvent;
    }

    /// <summary>Raised with true when the watched window comes to the foreground.</summary>
    public event EventHandler<bool>? ForegroundChanged;

    public event EventHandler? TargetMoved;

    public bool IsTargetForeground { get; private set; }

    public void Watch(IntPtr targetWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unhook();
        _target = targetWindow;

        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _callback,
            processId: 0,
            threadId: 0,
            NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);

        // Location changes are extremely chatty system-wide, so this hook is
        // scoped to the game's own process.
        NativeMethods.GetWindowThreadProcessId(targetWindow, out uint processId);

        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectLocationChange,
            NativeMethods.EventObjectLocationChange,
            IntPtr.Zero,
            _callback,
            processId,
            threadId: 0,
            NativeMethods.WineventOutOfContext);

        UpdateForeground(NativeMethods.GetForegroundWindow());
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint threadId,
        uint timestamp)
    {
        if (eventType == NativeMethods.EventSystemForeground)
        {
            UpdateForeground(window);
            return;
        }

        if (eventType == NativeMethods.EventObjectLocationChange
            && window == _target
            && objectId == NativeMethods.ObjidWindow)
        {
            TargetMoved?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateForeground(IntPtr foreground)
    {
        bool isTarget = foreground == _target;

        if (isTarget == IsTargetForeground)
        {
            return;
        }

        IsTargetForeground = isTarget;
        ForegroundChanged?.Invoke(this, isTarget);
    }

    private void Unhook()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unhook();
    }
}
