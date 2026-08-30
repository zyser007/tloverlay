using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace TLOverlay.App.Interop;

/// <summary>
/// Applies the window styles that make a WPF window behave like an overlay:
/// invisible to input, invisible to capture, always on top, never focused.
/// </summary>
internal static class OverlayWindowStyles
{
    public static IntPtr HandleOf(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>
    /// Layered + tool window + no-activate. Tool window keeps it out of Alt-Tab;
    /// no-activate keeps a click near the overlay from pulling focus off the
    /// game, which would minimise some fullscreen titles.
    /// </summary>
    public static void ApplyOverlayStyles(IntPtr handle, bool clickThrough)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();

        style |= NativeMethods.WsExLayered
            | NativeMethods.WsExToolWindow
            | NativeMethods.WsExNoActivate;

        if (clickThrough)
        {
            style |= NativeMethods.WsExTransparent;
        }
        else
        {
            style &= ~NativeMethods.WsExTransparent;
        }

        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    /// <summary>
    /// Excludes the window from every capture path - our own, OBS, and the
    /// Snipping Tool. Returns false on builds older than Windows 10 2004, where
    /// the caller must fall back to capturing the game window only and accept
    /// that screenshots will show the overlay.
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture))
        {
            return true;
        }

        Log.Warning(
            "SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed with {Error}; the overlay will be visible to capture.",
            Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>
    /// Re-asserts topmost. Called on a timer because some games push themselves
    /// back to the top of the z-order periodically, which silently buries the
    /// overlay behind the game.
    /// </summary>
    public static void AssertTopmost(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    /// <summary>Moves and resizes the overlay in physical pixels.</summary>
    public static void SetBounds(IntPtr handle, int x, int y, int width, int height)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            x, y, width, height,
            NativeMethods.SwpNoActivate);
    }
}
