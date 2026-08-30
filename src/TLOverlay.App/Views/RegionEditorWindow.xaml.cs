using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using TLOverlay.App.Interop;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Profiles;

namespace TLOverlay.App.Views;

/// <summary>
/// Full-screen drag-to-select over the game, used to mark where dialogue
/// appears.
///
/// The result is stored as a fraction of the game's client area rather than as
/// pixels, so the same profile keeps working when the player changes resolution
/// or moves to another monitor.
/// </summary>
public partial class RegionEditorWindow : Window
{
    private readonly IntPtr _gameWindow;
    private Point _origin;
    private bool _dragging;

    public RegionEditorWindow(IntPtr gameWindow, string regionName = "Dialogue")
    {
        InitializeComponent();

        _gameWindow = gameWindow;
        RegionName = regionName;

        SourceInitialized += OnSourceInitialized;
        KeyDown += OnKeyDown;
    }

    public string RegionName { get; }

    /// <summary>The selection, in fractions of the game's client area.</summary>
    public CaptureRegion? Result { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // The editor must not appear in captured frames either - otherwise its
        // dimming layer would be what OCR sees.
        OverlayWindowStyles.ExcludeFromCapture(handle);

        if (WindowFinder.TryGetClientBounds(_gameWindow, out int x, out int y, out int width, out int height))
        {
            OverlayWindowStyles.SetBounds(handle, x, y, width, height);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _origin = e.GetPosition(Surface);
        _dragging = true;

        Selection.Visibility = Visibility.Visible;
        UpdateSelection(_origin);
        Surface.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            UpdateSelection(e.GetPosition(Surface));
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Surface.ReleaseMouseCapture();
        UpdateSelection(e.GetPosition(Surface));

        Hint.Text = Selection.Width < 8 || Selection.Height < 8
            ? "พื้นที่เล็กเกินไป — ลากใหม่อีกครั้ง"
            : "Enter เพื่อบันทึก, Esc เพื่อยกเลิก, หรือลากใหม่";
    }

    private void UpdateSelection(Point current)
    {
        double left = Math.Min(_origin.X, current.X);
        double top = Math.Min(_origin.Y, current.Y);

        Canvas.SetLeft(Selection, left);
        Canvas.SetTop(Selection, top);
        Selection.Width = Math.Abs(current.X - _origin.X);
        Selection.Height = Math.Abs(current.Y - _origin.Y);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            DialogResult = false;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        if (Selection.Width < 8 || Selection.Height < 8)
        {
            Hint.Text = "ยังไม่ได้เลือกพื้นที่ — ลากเมาส์คลุมกล่องบทสนทนาก่อน";
            return;
        }

        double surfaceWidth = Surface.ActualWidth;
        double surfaceHeight = Surface.ActualHeight;

        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            DialogResult = false;
            return;
        }

        Result = new CaptureRegion(
            RegionName,
            Canvas.GetLeft(Selection) / surfaceWidth,
            Canvas.GetTop(Selection) / surfaceHeight,
            Selection.Width / surfaceWidth,
            Selection.Height / surfaceHeight);

        DialogResult = true;
    }
}
