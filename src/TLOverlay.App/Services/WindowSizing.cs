using System.Windows;

namespace TLOverlay.App.Services;

/// <summary>Keeps a window's starting size inside the screen it opens on.</summary>
public static class WindowSizing
{
    /// <summary>
    /// Shrinks a window to fit the work area if its designed size does not.
    ///
    /// The sizes in XAML suit a normal desktop. A 1366x768 laptop - which is
    /// exactly the kind of machine this app is meant to be gentle on - has less
    /// than 730 usable pixels of height, and a window taller than that opens with
    /// its lower half behind the taskbar or off the screen entirely. Both windows
    /// scroll, so losing some height costs nothing.
    /// </summary>
    public static void ClampToWorkArea(Window window, double margin = 48)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect work = SystemParameters.WorkArea;

        if (!double.IsNaN(window.Height) && window.Height > work.Height - margin)
        {
            window.Height = Math.Max(window.MinHeight, work.Height - margin);
        }

        if (!double.IsNaN(window.Width) && window.Width > work.Width - margin)
        {
            window.Width = Math.Max(window.MinWidth, work.Width - margin);
        }
    }
}
