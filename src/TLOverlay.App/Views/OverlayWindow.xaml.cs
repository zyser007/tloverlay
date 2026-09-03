using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TLOverlay.App.Interop;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Ocr;
using TLOverlay.Core.Pipeline;
using TLOverlay.Core.Profiles;

namespace TLOverlay.App.Views;

/// <summary>
/// The window that paints Thai over the game.
///
/// Inert by default: click-through, never activated, excluded from capture, and
/// re-asserted topmost on a timer. All of that is about staying out of the way
/// of the game rather than about drawing.
///
/// Turning click-through off flips it into an interactive mode where the panel
/// can be dragged and resized. That mode is announced on screen, because a mode
/// the player cannot see is one they will forget they are in.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double MinimumPanelWidth = 120;
    private const double MinimumPanelHeight = 44;

    /// <summary>
    /// Grown a little beyond the OCR rectangle. Those rectangles clip ascenders
    /// and antialiased edges, and the whole point of an opaque box is that no
    /// English shows around it.
    /// </summary>
    private const double LabelPadding = 2;

    private const double MinimumLabelSize = 10;
    private const double MinimumLabelFontSize = 9;

    /// <summary>How much wider than its English a label may grow before shrinking instead.</summary>
    private const double MaxLabelGrowth = 1.6;

    private readonly DispatcherTimer _topmostTimer;

    private Border _panel = null!;
    private TextBlock _text = null!;
    private Thumb _moveThumb = null!;
    private Thumb _resizeThumb = null!;
    private Border _outline = null!;
    private Border _modeBadge = null!;
    private Border _busyBadge = null!;
    private Canvas _screenLayer = null!;

    private IReadOnlyList<ScreenLine> _screenLines = [];
    private IntPtr _handle;
    private IntPtr _gameWindow;
    private GameProfile _profile = GameProfile.CreateDefault("Default");
    private bool _clickThrough = true;
    private bool _hasText;
    private int _lastClientWidth;
    private int _lastClientHeight;

    public OverlayWindow()
    {
        InitializeComponent();
        BuildVisuals();

        // Some games re-assert their own z-order periodically, which quietly
        // buries the overlay. Cheap to push back once a second.
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _topmostTimer.Tick += (_, _) => OverlayWindowStyles.AssertTopmost(_handle);

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => LayoutAll();
        Closed += (_, _) => _topmostTimer.Stop();
    }

    /// <summary>Raised when the player finishes dragging or resizing the panel.</summary>
    public event EventHandler<RelativeRect>? PanelPlacementChanged;

    /// <summary>
    /// Raised when full-screen labels had to be dropped because the game window
    /// changed size, so the player can be told rather than left wondering.
    /// </summary>
    public event EventHandler? ScreenTranslationInvalidated;

    /// <summary>
    /// False when the OS refused to hide the overlay from capture, which means
    /// screenshots will include it. The pipeline is unaffected because capture is
    /// scoped to the game window.
    /// </summary>
    public bool IsHiddenFromCapture { get; private set; }

    /// <summary>
    /// The translated text and the capture region are toggled separately. They
    /// answer different questions - "is the translation in my way" versus "is my
    /// area in the right place".
    /// </summary>
    public bool TranslationsVisible { get; private set; } = true;

    public bool RegionVisible { get; private set; }

    private void BuildVisuals()
    {
        _text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            // A soft shadow keeps Thai readable over a bright scene even when the
            // panel behind it is fairly transparent.
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.9,
            },
        };

        TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Ideal);

        _moveThumb = new Thumb
        {
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Visibility = Visibility.Collapsed,
            Template = TransparentThumbTemplate(),
        };
        _moveThumb.DragDelta += OnMoveDelta;
        _moveThumb.DragCompleted += (_, _) => PersistPlacement();

        _resizeThumb = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = System.Windows.Input.Cursors.SizeNWSE,
            Visibility = Visibility.Collapsed,
            Template = GripThumbTemplate(),
        };
        _resizeThumb.DragDelta += OnResizeDelta;
        _resizeThumb.DragCompleted += (_, _) => PersistPlacement();

        var content = new Grid();
        content.Children.Add(_text);
        content.Children.Add(_moveThumb);     // above the text, so drags land on it
        content.Children.Add(_resizeThumb);   // above everything, in the corner

        _panel = new Border
        {
            Padding = new Thickness(12, 8, 12, 10),
            CornerRadius = new CornerRadius(6),
            Child = content,
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
        };

        _outline = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x8D, 0xFF)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4C, 0x8D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "พื้นที่การแปล",
                Margin = new Thickness(5, 2, 5, 2),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xCF, 0xE0, 0xFF)),
            },
        };

        _modeBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1A, 0x4E, 0x8A)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 7, 16, 8),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "โหมดโต้ตอบ — ลากกรอบเพื่อย้าย, มุมขวาล่างเพื่อปรับขนาด · Ctrl+Alt+C เพื่อกลับไปคลิกทะลุ",
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };

        _busyBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1A, 0x4E, 0x8A)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 7, 16, 8),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "กำลังแปลทั้งจอ…",
                Foreground = Brushes.White,
                FontSize = 13,
            },
        };

        // Its own layer, so one visibility flip hides every label and one Clear
        // drops the lot without disturbing the panel or the region outline.
        _screenLayer = new Canvas { IsHitTestVisible = false };

        // Above the panel, not below it: a full-screen label has to cover the
        // English it replaces, and a leftover subtitle panel painting over the
        // labels is what that ordering produced.
        Surface.Children.Add(_outline);
        Surface.Children.Add(_panel);
        Surface.Children.Add(_screenLayer);
        Surface.Children.Add(_modeBadge);
        Surface.Children.Add(_busyBadge);
    }

    private static ControlTemplate TransparentThumbTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }

    /// <summary>
    /// The classic three diagonal ridges. A plain filled square said nothing
    /// about what it was for; this is the shape Windows itself uses for a resize
    /// corner, so it needs no explaining.
    /// </summary>
    private static ControlTemplate GripThumbTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0x4C, 0x8D, 0xFF)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4, 0, 5, 0));

        var grip = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        grip.SetValue(
            System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 3,15 L 15,3 M 7,15 L 15,7 M 11,15 L 15,11"));
        grip.SetValue(
            System.Windows.Shapes.Path.StrokeProperty,
            new SolidColorBrush(Color.FromArgb(0xFF, 0xDC, 0xEA, 0xFF)));
        grip.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.8);
        grip.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        grip.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        grip.SetValue(FrameworkElement.MarginProperty, new Thickness(1));

        border.AppendChild(grip);

        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        OverlayWindowStyles.ApplyOverlayStyles(_handle, _clickThrough);
        IsHiddenFromCapture = OverlayWindowStyles.ExcludeFromCapture(_handle);
        OverlayWindowStyles.AssertTopmost(_handle);

        _topmostTimer.Start();
    }

    public void Attach(IntPtr gameWindow, GameProfile profile)
    {
        _gameWindow = gameWindow;
        _profile = profile;
        _hasText = false;

        _text.Text = string.Empty;
        _panel.Visibility = Visibility.Collapsed;

        AlignToGame();
        LayoutAll();
    }

    /// <summary>
    /// Snaps the overlay onto the game's client area, in physical pixels. The
    /// client area rather than the window rect, so a title bar cannot offset
    /// every translation by its height.
    /// </summary>
    public void AlignToGame()
    {
        if (_gameWindow == IntPtr.Zero
            || !WindowFinder.TryGetClientBounds(_gameWindow, out int x, out int y, out int width, out int height))
        {
            return;
        }

        OverlayWindowStyles.SetBounds(_handle, x, y, width, height);

        // Moving the game does not move its text relative to its own window, so
        // the labels stay. Resizing does: the game re-flows its UI and every
        // label is now over the wrong words, which is worse than showing none.
        if ((_lastClientWidth != 0 || _lastClientHeight != 0)
            && (width != _lastClientWidth || height != _lastClientHeight)
            && _screenLines.Count > 0)
        {
            ClearScreenTranslation();
            ScreenTranslationInvalidated?.Invoke(this, EventArgs.Empty);
        }

        _lastClientWidth = width;
        _lastClientHeight = height;

        LayoutAll();
    }

    /// <summary>
    /// Lets the player interact with the overlay to move and resize the panel,
    /// and puts it back afterwards.
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        OverlayWindowStyles.ApplyOverlayStyles(_handle, clickThrough);

        bool interactive = !clickThrough;

        _panel.IsHitTestVisible = interactive;
        _moveThumb.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
        _resizeThumb.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
        _modeBadge.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;

        // A dashed edge while interactive, so the draggable area is obvious even
        // when the panel is empty.
        _panel.BorderThickness = new Thickness(interactive ? 1.5 : 0);
        _panel.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x7F, 0xB2, 0xFF));

        if (interactive)
        {
            // Something has to be grabbable before the first line arrives.
            EnsurePanelVisibleForEditing();
        }
        else if (!_hasText)
        {
            _panel.Visibility = Visibility.Collapsed;
        }

        LayoutAll();
    }

    public void SetTranslationsVisible(bool visible)
    {
        TranslationsVisible = visible;
        _panel.Visibility = visible && (_hasText || !_clickThrough)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Hidden, not dropped: pressing the key twice has to bring the same
        // labels back without paying for the sweep again.
        _screenLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRegionVisible(bool visible)
    {
        RegionVisible = visible;
        LayoutAll();
    }

    public void ShowTranslation(RegionTranslation translation)
    {
        ClearScreenTranslation();

        _hasText = true;
        _text.Text = translation.TranslatedText;
        _text.FontSize = _profile.FontSize;

        _panel.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(Math.Clamp(_profile.BackgroundOpacity, 0, 1) * 255),
            0x14, 0x16, 0x1B));

        LayoutPanel(translation);
        _panel.Visibility = TranslationsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Draws one opaque Thai label over every line the sweep found.
    ///
    /// Opaque by decision: the point is to replace the English, not to sit
    /// beside it. The boxes are inflated slightly because OCR rectangles clip
    /// ascenders and antialiased edges, and an English sliver poking out from
    /// under Thai reads as broken.
    /// </summary>
    public void ShowScreenTranslation(ScreenTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);

        _screenLines = translation.Lines;

        // The two presentations are alternatives, not layers: showing both means
        // the same line translated in two places. Whether the player can click
        // through the overlay has nothing to do with that, so this is not
        // conditional on it.
        _hasText = false;
        _text.Text = string.Empty;
        _panel.Visibility = Visibility.Collapsed;

        LayoutScreenLabels();
    }

    /// <summary>Drops every full-screen label, leaving the region panel alone.</summary>
    public void ClearScreenTranslation()
    {
        _screenLines = [];
        _screenLayer.Children.Clear();
    }

    /// <summary>Shows or hides the "working on it" badge over the game.</summary>
    public void SetScreenPassBusy(bool busy) =>
        _busyBadge.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Repaints the label backgrounds at a new opacity, without re-translating
    /// or re-measuring anything - the slider has to answer while it is dragged.
    /// </summary>
    public void SetScreenOpacity(double opacity)
    {
        _profile.ScreenOverlayOpacity = Math.Clamp(opacity, 0, 1);

        SolidColorBrush brush = ScreenLabelBrush();

        foreach (Border label in _screenLayer.Children.OfType<Border>())
        {
            label.Background = brush;
        }
    }

    private SolidColorBrush ScreenLabelBrush() => new(Color.FromArgb(
        (byte)(Math.Clamp(_profile.ScreenOverlayOpacity, 0, 1) * 255),
        0x10,
        0x12,
        0x16));

    private void LayoutScreenLabels()
    {
        _screenLayer.Children.Clear();

        if (ActualWidth <= 0 || ActualHeight <= 0 || _screenLines.Count == 0)
        {
            return;
        }

        SolidColorBrush background = ScreenLabelBrush();
        var typeface = new Typeface(_text.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (ScreenLine line in _screenLines)
        {
            double width = Math.Max(MinimumLabelSize, line.Bounds.Width * ActualWidth) + (LabelPadding * 2);
            double height = Math.Max(MinimumLabelSize, line.Bounds.Height * ActualHeight) + (LabelPadding * 2);
            double left = (line.Bounds.X * ActualWidth) - LabelPadding;
            double top = (line.Bounds.Y * ActualHeight) - LabelPadding;

            double fontSize = FitFontSize(line.TranslatedText, typeface, pixelsPerDip, width, height, out double needed);

            // A little growth beats shrinking to unreadable: Thai is almost
            // always longer than the English it replaces.
            if (needed > width)
            {
                double grown = Math.Min(needed, width * MaxLabelGrowth);
                left -= (grown - width) / 2;   // about the centre: game text is often centred
                width = grown;
            }

            left = Math.Clamp(left, 0, Math.Max(0, ActualWidth - width));
            top = Math.Clamp(top, 0, Math.Max(0, ActualHeight - height));

            var label = new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(2),
                Width = width,
                MinHeight = height,
                IsHitTestVisible = false,

                // No drop shadow here, unlike the single panel: forty shadowed
                // labels is forty render passes, and the boxes are opaque anyway.
                Child = new TextBlock
                {
                    Text = line.TranslatedText,
                    Foreground = Brushes.White,
                    FontSize = fontSize,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 3, 0),
                },
            };

            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            _screenLayer.Children.Add(label);
        }

        _screenLayer.Visibility = TranslationsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The largest size this line fits its box at, and how wide it wants to be at
    /// that size.
    ///
    /// Measured rather than laid out: one FormattedText and one correction, for
    /// forty labels, instead of forty layout passes.
    /// </summary>
    private double FitFontSize(
        string text,
        Typeface typeface,
        double pixelsPerDip,
        double boxWidth,
        double boxHeight,
        out double needed)
    {
        double size = Math.Clamp(boxHeight * 0.68, MinimumLabelFontSize, _profile.FontSize);
        needed = Measure(text, typeface, pixelsPerDip, size);

        double usable = Math.Max(1, boxWidth - 6);

        if (needed > usable)
        {
            size = Math.Max(MinimumLabelFontSize, size * usable / needed);
            needed = Measure(text, typeface, pixelsPerDip, size);
        }

        return size;
    }

    private static double Measure(string text, Typeface typeface, double pixelsPerDip, double fontSize) =>
        new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            pixelsPerDip).Width + 6;

    public void ClearText()
    {
        _hasText = false;
        _text.Text = string.Empty;

        if (_clickThrough)
        {
            _panel.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsurePanelVisibleForEditing()
    {
        if (_text.Text.Length == 0)
        {
            _text.Text = "ตัวอย่างข้อความแปล";
            _text.FontSize = _profile.FontSize;
        }

        _panel.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(Math.Clamp(_profile.BackgroundOpacity, 0, 1) * 255),
            0x14, 0x16, 0x1B));

        _panel.Visibility = TranslationsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LayoutAll()
    {
        LayoutRegion();
        LayoutBadge();
        LayoutPanel(translation: null);
        LayoutScreenLabels();
    }

    private void LayoutRegion()
    {
        var region = _profile.Region;

        if (region is null || ActualWidth <= 0)
        {
            _outline.Visibility = Visibility.Collapsed;
            return;
        }

        // Region coordinates are fractions and the window already matches the
        // game's client area, so no DPI conversion is needed - both sides are in
        // device-independent units.
        Canvas.SetLeft(_outline, region.X * ActualWidth);
        Canvas.SetTop(_outline, region.Y * ActualHeight);
        _outline.Width = Math.Max(2, region.Width * ActualWidth);
        _outline.Height = Math.Max(2, region.Height * ActualHeight);
        _outline.Visibility = RegionVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LayoutBadge()
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        _modeBadge.Measure(new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(_modeBadge, Math.Max(0, (ActualWidth - _modeBadge.DesiredSize.Width) / 2));
        Canvas.SetTop(_modeBadge, 24);

        _busyBadge.Measure(new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(_busyBadge, Math.Max(0, (ActualWidth - _busyBadge.DesiredSize.Width) / 2));

        // Below the mode badge, so an interactive-mode sweep shows both.
        Canvas.SetTop(_busyBadge, 24 + _modeBadge.DesiredSize.Height + 8);
    }

    /// <summary>
    /// Places the panel. A placement the player dragged always wins; otherwise
    /// it follows the profile's display mode, and inline mode follows the text
    /// OCR actually found.
    /// </summary>
    private void LayoutPanel(RegionTranslation? translation)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        RelativeRect placement = _profile.PanelBounds?.Clamped() ?? DefaultPlacement(translation);

        Canvas.SetLeft(_panel, placement.X * ActualWidth);
        Canvas.SetTop(_panel, placement.Y * ActualHeight);
        _panel.Width = Math.Max(MinimumPanelWidth, placement.Width * ActualWidth);

        // Height is a floor, not a cap: a long line must never be clipped by a
        // size the player chose before they saw it.
        _panel.MinHeight = Math.Max(MinimumPanelHeight, placement.Height * ActualHeight);
    }

    private RelativeRect DefaultPlacement(RegionTranslation? translation)
    {
        if (_profile.DisplayMode == OverlayDisplayMode.Inline
            && translation is { TextBoundsInWindow.Width: > 0 })
        {
            TextRect bounds = translation.TextBoundsInWindow;
            double scale = DpiHelper.ScaleFor(this);

            return new RelativeRect(
                (bounds.X / scale) / ActualWidth,
                (bounds.Y / scale) / ActualHeight,
                (bounds.Width / scale) / ActualWidth,
                (bounds.Height / scale) / ActualHeight).Clamped();
        }

        return _profile.DisplayMode switch
        {
            OverlayDisplayMode.SidePanel => new RelativeRect(0.70, 0.10, 0.28, 0.30),
            OverlayDisplayMode.Inline => _profile.Region?.Bounds ?? new RelativeRect(0.10, 0.78, 0.80, 0.16),
            _ => new RelativeRect(0.10, 0.78, 0.80, 0.16),
        };
    }

    private void OnMoveDelta(object sender, DragDeltaEventArgs e)
    {
        Canvas.SetLeft(_panel, Canvas.GetLeft(_panel) + e.HorizontalChange);
        Canvas.SetTop(_panel, Canvas.GetTop(_panel) + e.VerticalChange);
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        _panel.Width = Math.Max(MinimumPanelWidth, _panel.Width + e.HorizontalChange);
        _panel.MinHeight = Math.Max(MinimumPanelHeight, _panel.MinHeight + e.VerticalChange);
    }

    /// <summary>
    /// Converts the panel back to fractions and hands it out to be saved, so the
    /// placement survives a restart - and the restart that editing the region
    /// triggers.
    /// </summary>
    private void PersistPlacement()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var placement = new RelativeRect(
            Canvas.GetLeft(_panel) / ActualWidth,
            Canvas.GetTop(_panel) / ActualHeight,
            _panel.ActualWidth / ActualWidth,
            _panel.ActualHeight / ActualHeight).Clamped();

        _profile.PanelBounds = placement;
        LayoutPanel(translation: null);

        PanelPlacementChanged?.Invoke(this, placement);
    }

    /// <summary>Forgets a manual placement and goes back to the display mode.</summary>
    public void ResetPanelPlacement()
    {
        _profile.PanelBounds = null;
        LayoutPanel(translation: null);
    }
}
