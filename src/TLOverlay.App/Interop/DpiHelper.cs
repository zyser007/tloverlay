using System.Windows;
using System.Windows.Media;

namespace TLOverlay.App.Interop;

/// <summary>
/// Converts between the physical pixels the capture and Win32 layers speak and
/// the device-independent units WPF lays out in.
///
/// The app is PerMonitorV2, so the ratio is a property of whichever monitor the
/// overlay is currently on and changes when the player drags the game between
/// screens. Reading it from the live visual rather than caching a global is what
/// keeps mixed-DPI setups correct.
/// </summary>
internal static class DpiHelper
{
    public static double ScaleFor(Visual visual)
    {
        double scale = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        return scale <= 0 ? 1.0 : scale;
    }

    public static double ScaleForWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return 1.0;
        }

        uint dpi = NativeMethods.GetDpiForWindow(handle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static Point PointToDip(double physicalX, double physicalY, double scale) =>
        new(physicalX / scale, physicalY / scale);

    public static Size SizeToDip(double physicalWidth, double physicalHeight, double scale) =>
        new(physicalWidth / scale, physicalHeight / scale);
}
