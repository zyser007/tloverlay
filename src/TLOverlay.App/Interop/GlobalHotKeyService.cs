using System.Windows.Input;
using System.Windows.Interop;
using Serilog;

namespace TLOverlay.App.Interop;

public enum HotKeyAction
{
    ToggleTranslation,
    TranslateOnce,
    EditRegions,
    ToggleOverlayVisible,
    ToggleClickThrough,
}

/// <summary>
/// System-wide hotkeys, delivered to a message-only window.
///
/// Global rather than window-level because the game has keyboard focus the
/// entire time the player is using this - a WPF key binding would never fire.
/// </summary>
public sealed class GlobalHotKeyService : IDisposable
{
    private const int HwndMessage = -3;

    private readonly Dictionary<int, HotKeyAction> _actions = new();
    private readonly HwndSource _source;
    private int _nextId = 1;
    private bool _disposed;

    public GlobalHotKeyService()
    {
        var parameters = new HwndSourceParameters("TLOverlayHotKeys")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(HwndMessage),
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event EventHandler<HotKeyAction>? Pressed;

    /// <summary>
    /// Registers the default set. Returns the actions that could not be bound,
    /// which happens when another application already owns the combination -
    /// worth telling the player rather than leaving a key that does nothing.
    /// </summary>
    public IReadOnlyList<HotKeyAction> RegisterDefaults()
    {
        var failures = new List<HotKeyAction>();

        (HotKeyAction Action, uint Modifiers, Key Key)[] defaults =
        [
            (HotKeyAction.ToggleTranslation, NativeMethods.ModControl | NativeMethods.ModAlt, Key.T),
            (HotKeyAction.TranslateOnce, NativeMethods.ModControl | NativeMethods.ModAlt, Key.S),
            (HotKeyAction.EditRegions, NativeMethods.ModControl | NativeMethods.ModAlt, Key.R),
            (HotKeyAction.ToggleOverlayVisible, NativeMethods.ModControl | NativeMethods.ModAlt, Key.H),
            (HotKeyAction.ToggleClickThrough, NativeMethods.ModControl | NativeMethods.ModAlt, Key.C),
        ];

        foreach (var (action, modifiers, key) in defaults)
        {
            if (!Register(action, modifiers, key))
            {
                failures.Add(action);
            }
        }

        return failures;
    }

    public bool Register(HotKeyAction action, uint modifiers, Key key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int id = _nextId++;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        // MOD_NOREPEAT stops a held key from firing the action every few
        // milliseconds while the player leans on it.
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.ModNoRepeat, virtualKey))
        {
            Log.Warning("Could not register hotkey for {Action}; another application may own it.", action);
            return false;
        }

        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotKey && _actions.TryGetValue(wParam.ToInt32(), out HotKeyAction action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (int id in _actions.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
