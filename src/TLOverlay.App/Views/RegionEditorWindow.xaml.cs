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
/// Full-screen drag-to-select over the game, used to mark where text appears.
///
/// It opens showing the regions the profile already has. An editor that started
/// blank and replaced everything on save meant the only way to add a second
/// region was to lose the first, and there was no way to see where the existing
/// one sat.
///
/// Regions are stored as a fraction of the game's client area rather than as
/// pixels, so a profile keeps working when the player changes resolution or
/// moves to another monitor.
/// </summary>
public partial class RegionEditorWindow : Window
{
    private static readonly Brush RegionStroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush RegionFill = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0x8D, 0xFF));
    private static readonly Brush SelectedStroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x5C));
    private static readonly Brush SelectedFill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xC4, 0x5C));

    private readonly IntPtr _gameWindow;
    private readonly List<CaptureRegion> _regions;

    private Rectangle? _dragRectangle;
    private Point _origin;
    private bool _dragging;
    private int _selected = -1;

    public RegionEditorWindow(IntPtr gameWindow, IReadOnlyList<CaptureRegion>? existing = null)
    {
        InitializeComponent();

        _gameWindow = gameWindow;
        _regions = existing?.Where(static r => r.IsValid).ToList() ?? [];

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => Redraw();
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// The full region list to save, or null when the user cancelled. Returning
    /// the whole list rather than one region is what lets the caller replace the
    /// profile's regions without having to merge.
    /// </summary>
    public IReadOnlyList<CaptureRegion>? Result { get; private set; }

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

    /// <summary>Repaints every saved region. The live drag rectangle is kept separate.</summary>
    private void Redraw()
    {
        Surface.Children.Clear();
        _dragRectangle = null;

        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (int i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            bool selected = i == _selected;

            var box = new Rectangle
            {
                Width = region.Width * width,
                Height = region.Height * height,
                Stroke = selected ? SelectedStroke : RegionStroke,
                StrokeThickness = 2,
                Fill = selected ? SelectedFill : RegionFill,
            };

            Canvas.SetLeft(box, region.X * width);
            Canvas.SetTop(box, region.Y * height);
            Surface.Children.Add(box);

            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x16, 0x1B)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 3),
                Child = new TextBlock
                {
                    Text = region.Name,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Leelawadee UI, Segoe UI"),
                    FontSize = 12,
                },
            };

            Canvas.SetLeft(label, region.X * width);
            Canvas.SetTop(label, Math.Max(0, (region.Y * height) - 22));
            Surface.Children.Add(label);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        Summary.Text = _regions.Count == 0
            ? "ยังไม่มีพื้นที่ · Enter = บันทึก · Esc = ยกเลิกทั้งหมด"
            : $"มี {_regions.Count} พื้นที่: {string.Join(", ", _regions.Select(static r => r.Name))} · Enter = บันทึก · Esc = ยกเลิกทั้งหมด";
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Surface);

        // A click inside an existing region selects it instead of starting a new
        // drag, which is how a region gets deleted or replaced deliberately.
        int hit = HitTest(point);
        if (hit >= 0)
        {
            _selected = hit;
            Redraw();
            Hint.Text = $"เลือก “{_regions[hit].Name}” — กด Delete เพื่อลบ หรือลากใหม่เพื่อเพิ่มพื้นที่อื่น";
            return;
        }

        _selected = -1;
        _origin = point;
        _dragging = true;

        _dragRectangle = new Rectangle
        {
            Stroke = RegionStroke,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            Fill = RegionFill,
        };

        Surface.Children.Add(_dragRectangle);
        UpdateDrag(point);
        Surface.CaptureMouse();
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
        if (!_dragging || _dragRectangle is null)
        {
            return;
        }

        _dragging = false;
        Surface.ReleaseMouseCapture();
        UpdateDrag(e.GetPosition(Surface));

        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;

        if (_dragRectangle.Width < 8 || _dragRectangle.Height < 8 || width <= 0 || height <= 0)
        {
            Hint.Text = "พื้นที่เล็กเกินไป — ลากใหม่อีกครั้ง";
            Redraw();
            return;
        }

        var region = new CaptureRegion(
            CaptureRegion.UniqueName(_regions.Select(static r => r.Name)),
            Canvas.GetLeft(_dragRectangle) / width,
            Canvas.GetTop(_dragRectangle) / height,
            _dragRectangle.Width / width,
            _dragRectangle.Height / height);

        // Added, never replacing: the previous regions are the player's work too.
        _regions.Add(region);
        _selected = _regions.Count - 1;

        Redraw();
        Hint.Text = $"เพิ่ม “{region.Name}” แล้ว — ลากอีกครั้งเพื่อเพิ่ม, Enter เพื่อบันทึก";
    }

    private void UpdateDrag(Point current)
    {
        if (_dragRectangle is null)
        {
            return;
        }

        double left = Math.Max(0, Math.Min(_origin.X, current.X));
        double top = Math.Max(0, Math.Min(_origin.Y, current.Y));

        Canvas.SetLeft(_dragRectangle, left);
        Canvas.SetTop(_dragRectangle, top);
        _dragRectangle.Width = Math.Abs(current.X - _origin.X);
        _dragRectangle.Height = Math.Abs(current.Y - _origin.Y);
    }

    private int HitTest(Point point)
    {
        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return -1;
        }

        // Last drawn wins, so the region on top is the one you select.
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            var region = _regions[i];

            if (point.X >= region.X * width
                && point.X <= (region.X + region.Width) * width
                && point.Y >= region.Y * height
                && point.Y <= (region.Y + region.Height) * height)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Result = null;
                DialogResult = false;
                return;

            case Key.Delete or Key.Back when _selected >= 0 && _selected < _regions.Count:
                string removed = _regions[_selected].Name;
                _regions.RemoveAt(_selected);
                _selected = -1;
                Redraw();
                Hint.Text = $"ลบ “{removed}” แล้ว — ลากเพื่อเพิ่มใหม่, Enter เพื่อบันทึก";
                return;

            case Key.Enter:
                // Saving an empty list is allowed: it is how the player clears
                // every region deliberately.
                Result = _regions.ToList();
                DialogResult = true;
                return;
        }
    }
}
