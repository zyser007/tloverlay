using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TLOverlay.App.Interop;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Pipeline;
using TLOverlay.Core.Profiles;

namespace TLOverlay.App.Views;

/// <summary>
/// The window that paints Thai over the game.
///
/// It is deliberately inert: click-through, never activated, excluded from
/// capture, and re-asserted topmost on a timer. All of that is about staying out
/// of the way of the game rather than about drawing.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly Dictionary<string, Border> _panels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _outlines = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _topmostTimer;

    private IntPtr _handle;
    private IntPtr _gameWindow;
    private GameProfile _profile = GameProfile.CreateDefault("Default");
    private bool _clickThrough = true;

    public OverlayWindow()
    {
        InitializeComponent();

        // Some games re-assert their own z-order periodically, which quietly
        // buries the overlay. Cheap to push back once a second.
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _topmostTimer.Tick += (_, _) => OverlayWindowStyles.AssertTopmost(_handle);

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _topmostTimer.Stop();
    }

    /// <summary>
    /// False when the OS refused to hide the overlay from capture, which means
    /// screenshots will include it. The pipeline is unaffected because capture is
    /// scoped to the game window.
    /// </summary>
    public bool IsHiddenFromCapture { get; private set; }

    /// <summary>
    /// The translated text, and the capture outlines, are toggled separately.
    /// They answer different questions - "is the translation in my way" versus
    /// "is my box in the right place" - and a player tuning a region usually
    /// wants the outlines on and the text off.
    /// </summary>
    public bool TranslationsVisible { get; private set; } = true;

    public bool RegionOutlinesVisible { get; private set; }

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

        Surface.Children.Clear();
        _panels.Clear();
        _outlines.Clear();

        AlignToGame();
        ShowRegions(profile.Regions);
    }

    /// <summary>
    /// Draws a dashed outline per capture region, so the player can see where
    /// the pipeline is actually reading from without opening the editor.
    /// </summary>
    public void ShowRegions(IEnumerable<CaptureRegion> regions)
    {
        foreach (var outline in _outlines.Values)
        {
            Surface.Children.Remove(outline);
        }

        _outlines.Clear();

        foreach (var region in regions.Where(static r => r.IsValid))
        {
            var outline = CreateOutline(region.Name);
            _outlines[region.Name] = outline;
            Surface.Children.Add(outline);
        }

        LayoutOutlines();
    }

    private void LayoutOutlines()
    {
        // Region coordinates are fractions, and the window already matches the
        // game's client area, so no DPI conversion is needed here - both sides
        // are in device-independent units.
        foreach (var region in _profile.Regions)
        {
            if (!_outlines.TryGetValue(region.Name, out Border? outline))
            {
                continue;
            }

            Canvas.SetLeft(outline, region.X * ActualWidth);
            Canvas.SetTop(outline, region.Y * ActualHeight);
            outline.Width = Math.Max(2, region.Width * ActualWidth);
            outline.Height = Math.Max(2, region.Height * ActualHeight);
            outline.Visibility = RegionOutlinesVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static Border CreateOutline(string name) =>
        new()
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x8D, 0xFF)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4C, 0x8D, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = name,
                Margin = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xCF, 0xE0, 0xFF)),
            },
        };

    public void SetTranslationsVisible(bool visible)
    {
        TranslationsVisible = visible;

        foreach (var panel in _panels.Values)
        {
            // Only re-show panels that hold something; an empty one would flash
            // an empty box over the game.
            if (!visible)
            {
                panel.Visibility = Visibility.Collapsed;
            }
            else if (((TextBlock)panel.Child).Text.Length > 0)
            {
                panel.Visibility = Visibility.Visible;
            }
        }
    }

    public void SetRegionOutlinesVisible(bool visible)
    {
        RegionOutlinesVisible = visible;
        LayoutOutlines();
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
        LayoutOutlines();
    }

    /// <summary>
    /// Lets the player interact with the overlay (to drag a panel out of the way,
    /// for instance) and puts it back afterwards.
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        OverlayWindowStyles.ApplyOverlayStyles(_handle, clickThrough);
    }

    public void ShowTranslation(RegionTranslation translation)
    {
        double scale = DpiHelper.ScaleFor(this);

        if (!_panels.TryGetValue(translation.RegionName, out Border? panel))
        {
            panel = CreatePanel();
            _panels[translation.RegionName] = panel;
            Surface.Children.Add(panel);
        }

        var text = (TextBlock)panel.Child;
        text.Text = translation.TranslatedText;
        text.FontSize = _profile.FontSize;

        panel.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(Math.Clamp(_profile.BackgroundOpacity, 0, 1) * 255),
            0x14, 0x16, 0x1B));

        Position(panel, translation, scale);
        panel.Visibility = TranslationsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ClearRegion(string regionName)
    {
        if (_panels.TryGetValue(regionName, out Border? panel))
        {
            panel.Visibility = Visibility.Collapsed;
        }
    }

    public void ClearAll()
    {
        foreach (var panel in _panels.Values)
        {
            panel.Visibility = Visibility.Collapsed;
        }
    }

    private void Position(Border panel, RegionTranslation translation, double scale)
    {
        // Inline mode covers the original text exactly; the other modes only need
        // the region, and deliberately avoid depending on OCR's bounding boxes
        // being tight.
        TextRectangle target = _profile.DisplayMode switch
        {
            OverlayDisplayMode.Inline when translation.TextBoundsInWindow.Width > 0 =>
                Expand(translation.TextBoundsInWindow, 8),
            OverlayDisplayMode.Inline => Expand(translation.RegionBoundsInWindow, 0),
            OverlayDisplayMode.Subtitle => SubtitleBand(),
            _ => SidePanelBand(),
        };

        Canvas.SetLeft(panel, target.X / scale);
        Canvas.SetTop(panel, target.Y / scale);
        panel.Width = Math.Max(40, target.Width / scale);
        panel.MaxHeight = Math.Max(40, target.Height / scale);
    }

    private static TextRectangle Expand(TLOverlay.Core.Ocr.TextRect rect, double margin) =>
        new(rect.X - margin, rect.Y - margin, rect.Width + (margin * 2), rect.Height + (margin * 2));

    private TextRectangle SubtitleBand()
    {
        double width = ActualWidth * DpiHelper.ScaleFor(this);
        double height = ActualHeight * DpiHelper.ScaleFor(this);

        double bandWidth = width * 0.8;
        return new TextRectangle((width - bandWidth) / 2, height * 0.78, bandWidth, height * 0.18);
    }

    private TextRectangle SidePanelBand()
    {
        double width = ActualWidth * DpiHelper.ScaleFor(this);
        double height = ActualHeight * DpiHelper.ScaleFor(this);

        double bandWidth = width * 0.28;
        return new TextRectangle(width - bandWidth - 16, height * 0.1, bandWidth, height * 0.5);
    }

    private static Border CreatePanel()
    {
        var text = new TextBlock
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

        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);

        return new Border
        {
            Padding = new Thickness(12, 8, 12, 10),
            CornerRadius = new CornerRadius(6),
            Child = text,
            Visibility = Visibility.Collapsed,
        };
    }

    private readonly record struct TextRectangle(double X, double Y, double Width, double Height);
}
