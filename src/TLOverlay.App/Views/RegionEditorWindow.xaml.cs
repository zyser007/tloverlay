using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TLOverlay.App.Interop;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Profiles;

namespace TLOverlay.App.Views;

/// <summary>
/// Full-screen drag-to-select over the game, marking the one area text is read
/// from.
///
/// It opens showing the area already set, so a redraw is a deliberate
/// replacement rather than a surprise. Escape abandons the edit entirely and the
/// previous area survives.
///
/// The area is stored as a fraction of the game's client area rather than as
/// pixels, so a profile keeps working when the player changes resolution or
/// moves to another monitor.
/// </summary>
public partial class RegionEditorWindow : Window
{
    private static readonly Brush Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0x8D, 0xFF));

    private readonly IntPtr _gameWindow;

    private Rectangle? _rectangle;
    private RelativeRect? _current;
    private Point _origin;
    private bool _dragging;

    public RegionEditorWindow(IntPtr gameWindow, CaptureRegion? existing = null)
    {
        // Before InitializeComponent: XAML raises events as it parses, and those
        // handlers read these fields.
        _gameWindow = gameWindow;
        _current = existing?.IsValid == true ? existing.Bounds : null;

        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => Redraw();
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// The area to save when the dialog returns true. Null there means the player
    /// deliberately cleared it.
    /// </summary>
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

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        Surface.Children.Clear();
        _rectangle = null;

        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;

        if (_current is null || width <= 0 || height <= 0)
        {
            UpdateSummary();
            return;
        }

        _rectangle = new Rectangle
        {
            Width = _current.Width * width,
            Height = _current.Height * height,
            Stroke = Stroke,
            StrokeThickness = 2,
            Fill = Fill,
        };

        Canvas.SetLeft(_rectangle, _current.X * width);
        Canvas.SetTop(_rectangle, _current.Y * height);
        Surface.Children.Add(_rectangle);

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        Summary.Text = _current is null
            ? "ยังไม่ได้เลือกพื้นที่ · Enter = บันทึก · Esc = ยกเลิก"
            : $"พื้นที่ปัจจุบัน {_current.Width:P0} x {_current.Height:P0} ของหน้าจอ · Enter = บันทึก · Esc = ยกเลิก";
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _origin = e.GetPosition(Surface);
        _dragging = true;

        Surface.Children.Clear();

        _rectangle = new Rectangle
        {
            Stroke = Stroke,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            Fill = Fill,
        };

        Surface.Children.Add(_rectangle);
        UpdateDrag(_origin);
        Surface.CaptureMouse();

        Hint.Text = "ลากคลุมกล่องบทสนทนา…";
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            UpdateDrag(e.GetPosition(Surface));
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _rectangle is null)
        {
            return;
        }

        _dragging = false;
        Surface.ReleaseMouseCapture();
        UpdateDrag(e.GetPosition(Surface));

        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;

        if (_rectangle.Width < 8 || _rectangle.Height < 8 || width <= 0 || height <= 0)
        {
            Hint.Text = "พื้นที่เล็กเกินไป — ลากใหม่อีกครั้ง";
            Redraw();
            return;
        }

        _current = new RelativeRect(
            Canvas.GetLeft(_rectangle) / width,
            Canvas.GetTop(_rectangle) / height,
            _rectangle.Width / width,
            _rectangle.Height / height).Clamped();

        Redraw();
        Hint.Text = "ลากใหม่เพื่อเปลี่ยน · Delete เพื่อล้าง · Enter เพื่อบันทึก";
    }

    private void UpdateDrag(Point current)
    {
        if (_rectangle is null)
        {
            return;
        }

        double left = Math.Max(0, Math.Min(_origin.X, current.X));
        double top = Math.Max(0, Math.Min(_origin.Y, current.Y));

        Canvas.SetLeft(_rectangle, left);
        Canvas.SetTop(_rectangle, top);
        _rectangle.Width = Math.Abs(current.X - _origin.X);
        _rectangle.Height = Math.Abs(current.Y - _origin.Y);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                return;

            case Key.Delete or Key.Back:
                _current = null;
                Redraw();
                Hint.Text = "ล้างพื้นที่แล้ว — ลากเพื่อเลือกใหม่ หรือ Enter เพื่อบันทึกแบบไม่มีพื้นที่";
                return;

            case Key.Enter:
                Result = _current is null ? null : CaptureRegion.FromBounds(_current);
                DialogResult = true;
                return;
        }
    }
}
