using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TLOverlay.Core.Capture;

/// <summary>
/// Enumerates candidate game windows and tracks the chosen one's position.
/// </summary>
public static class WindowFinder
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsExToolWindow = 0x00000080L;
    private const int DwmaCloaked = 14;

    /// <summary>
    /// Lists visible top-level windows large enough to be a game, most recently
    /// interacted with first is not available cheaply, so results are ordered by
    /// area - the game is nearly always the biggest thing on screen.
    /// </summary>
    public static IReadOnlyList<GameWindow> EnumerateCandidates(int minimumWidth = 640, int minimumHeight = 360)
    {
        var results = new List<GameWindow>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                return true;
            }

            // UWP windows that are not on screen are still enumerable; DWM knows
            // they are cloaked and they would capture as a blank rectangle.
            if (IsCloaked(handle))
            {
                return true;
            }

            long exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if ((exStyle & WsExToolWindow) != 0)
            {
                return true;
            }

            if (!TryGetClientSize(handle, out int width, out int height))
            {
                return true;
            }

            if (width < minimumWidth || height < minimumHeight)
            {
                return true;
            }

            string title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out int processId);
            results.Add(new GameWindow(handle, title, GetProcessName(processId), processId, width, height));
            return true;
        },
        IntPtr.Zero);

        return results
            .OrderByDescending(static w => (long)w.Width * w.Height)
            .ToList();
    }

    /// <summary>
    /// Whether the window has no caption and no resize frame, which is what
    /// borderless (windowed fullscreen) looks like from the outside. Used to warn
    /// the player when a game is in exclusive fullscreen, where capture fails.
    /// </summary>
    public static bool IsBorderless(IntPtr handle)
    {
        long style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        return (style & WsCaption) == 0 && (style & WsThickFrame) == 0;
    }

    public static bool IsAlive(IntPtr handle) => handle != IntPtr.Zero && IsWindow(handle);

    /// <summary>Client-area size in physical pixels.</summary>
    public static bool TryGetClientSize(IntPtr handle, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!GetClientRect(handle, out RECT rect))
        {
            return false;
        }

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Screen position of the window's client area, in physical pixels. This is
    /// what the overlay aligns itself to - using the window rect instead would
    /// leave the overlay offset by the border on non-borderless windows.
    /// </summary>
    public static bool TryGetClientBounds(IntPtr handle, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;

        if (!GetClientRect(handle, out RECT rect))
        {
            return false;
        }

        var topLeft = new POINT { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(handle, ref topLeft))
        {
            return false;
        }

        x = topLeft.X;
        y = topLeft.Y;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    public static string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    private static bool IsCloaked(IntPtr handle)
    {
        if (DwmGetWindowAttribute(handle, DwmaCloaked, out int cloaked, sizeof(int)) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr handle, ref POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
