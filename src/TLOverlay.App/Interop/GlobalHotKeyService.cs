using System.Windows.Input;
using System.Windows.Interop;
using Serilog;

namespace TLOverlay.App.Interop;

/// <summary>One hotkey: what it does, how it is registered, and how it is written.</summary>
public sealed record HotKeyBinding(HotKeyAction Action, uint Modifiers, Key Key, string Gesture);

public enum HotKeyAction
{
    ToggleTranslation,
    TranslateOnce,
    EditRegions,
    ToggleTranslations,
    ToggleRegionOutlines,
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
    /// The bindings the app registers, and how to write them.
    ///
    /// One list, used both to register the keys and to print them in the control
    /// panel, so the two can never drift into telling the player about a key that
    /// was never bound.
    /// </summary>
    public static IReadOnlyList<HotKeyBinding> Defaults { get; } =
    [
        new(HotKeyAction.ToggleTranslation, NativeMethods.ModControl | NativeMethods.ModAlt, Key.T, "Ctrl+Alt+T"),
        new(HotKeyAction.EditRegions, NativeMethods.ModControl | NativeMethods.ModAlt, Key.R, "Ctrl+Alt+R"),
        new(HotKeyAction.ToggleTranslations, NativeMethods.ModControl | NativeMethods.ModAlt, Key.H, "Ctrl+Alt+H"),
        new(HotKeyAction.ToggleRegionOutlines, NativeMethods.ModControl | NativeMethods.ModAlt, Key.G, "Ctrl+Alt+G"),
        new(HotKeyAction.ToggleClickThrough, NativeMethods.ModControl | NativeMethods.ModAlt, Key.C, "Ctrl+Alt+C"),
        new(HotKeyAction.TranslateOnce, NativeMethods.ModControl | NativeMethods.ModAlt, Key.S, "Ctrl+Alt+S"),
    ];

    /// <summary>
    /// Registers the default set. Returns the bindings that could not be bound,
    /// which happens when another application already owns the combination -
    /// worth telling the player rather than leaving a key that does nothing.
    /// </summary>
    public IReadOnlyList<HotKeyBinding> RegisterDefaults()
    {
        var failures = new List<HotKeyBinding>();

        foreach (var binding in Defaults)
        {
            if (!Register(binding.Action, binding.Modifiers, binding.Key))
            {
                failures.Add(binding);
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
