using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Serilog;
using TLOverlay.App.Interop;
using TLOverlay.App.Services;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Profiles;

namespace TLOverlay.App.Views;

public partial class ControlPanelWindow : Window
{
    private readonly ProfileStore _profiles = new(AppPaths.ProfilesDirectory);
    private readonly AppSettings _settings = SettingsStore.Load(App.DataDirectory);
    private readonly GlobalHotKeyService _hotKeys = new();
    private readonly DispatcherTimer _metricsTimer;

    private TranslationSession? _session;
    private GameProfile _profile = GameProfile.CreateDefault("Default");
    private bool _clickThrough = true;
    private bool _busy;

    public ControlPanelWindow()
    {
        InitializeComponent();

        WindowList.ItemsSource = Windows;

        _hotKeys.Pressed += OnHotKey;
        var failed = _hotKeys.RegisterDefaults();

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _metricsTimer.Start();

        Loaded += (_, _) => Refresh();
        Closed += OnClosed;

        if (failed.Count > 0)
        {
            StatusText.Text = $"คีย์ลัดบางตัวถูกโปรแกรมอื่นใช้อยู่: {string.Join(", ", failed)}";
        }
    }

    public ObservableCollection<GameWindow> Windows { get; } = [];

    public GameWindow? SelectedWindow => WindowList.SelectedItem as GameWindow;

    private void Refresh()
    {
        var previous = SelectedWindow?.Handle;
        Windows.Clear();

        foreach (var window in WindowFinder.EnumerateCandidates())
        {
            Windows.Add(window);
        }

        if (previous is { } handle)
        {
            WindowList.SelectedItem = Windows.FirstOrDefault(w => w.Handle == handle);
        }

        if (_session?.IsRunning != true)
        {
            StatusText.Text = Windows.Count == 0
                ? "ไม่พบหน้าต่างที่จับภาพได้ — เปิดเกมก่อนแล้วกดรีเฟรช"
                : $"พบ {Windows.Count} หน้าต่าง";
        }
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedWindow;

        EditRegionButton.IsEnabled = selected is not null;
        StartButton.IsEnabled = selected is not null;

        if (selected is null)
        {
            return;
        }

        // Load whichever profile matches this game, so regions the player set
        // earlier come back automatically.
        _profile = ProfileStore.Match(_profiles.LoadAll(), selected.ProcessName, selected.Title)
            ?? GameProfile.CreateDefault(string.IsNullOrWhiteSpace(selected.ProcessName)
                ? selected.Title
                : selected.ProcessName);

        _profile.ProcessName ??= selected.ProcessName;

        // Exclusive fullscreen is the most common reason capture comes back
        // black, and it is not obvious to the player, so say it plainly.
        StatusText.Text = WindowFinder.IsBorderless(selected.Handle)
            ? $"เลือก: {selected.Title} ({selected.Width}x{selected.Height}) — borderless พร้อมจับภาพ"
            : $"เลือก: {selected.Title} — หน้าต่างนี้มีขอบ ถ้าเกมอยู่ในโหมด Exclusive Fullscreen จะจับภาพไม่ได้";
    }

    private void OnEditRegionClick(object sender, RoutedEventArgs e) => EditRegion();

    private void EditRegion()
    {
        var selected = SelectedWindow;
        if (selected is null)
        {
            return;
        }

        var editor = new RegionEditorWindow(selected.Handle) { Owner = this };

        if (editor.ShowDialog() != true || editor.Result is null)
        {
            return;
        }

        _profile.Regions = [editor.Result];
        _profiles.Save(_profile);

        StatusText.Text = $"บันทึกพื้นที่แปลของ {_profile.Name} แล้ว";

        // A moved region invalidates everything the pipeline has settled on, so
        // restart rather than translating against stale coordinates.
        if (_session?.IsRunning == true)
        {
            _ = RestartAsync();
        }
    }

    private void OnStartStopClick(object sender, RoutedEventArgs e) => _ = ToggleAsync();

    private async Task ToggleAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        StartButton.IsEnabled = false;

        try
        {
            if (_session?.IsRunning == true)
            {
                await _session.StopAsync();
                StartButton.Content = "เริ่มแปล";
                StatusText.Text = "หยุดแปลแล้ว";
                return;
            }

            var selected = SelectedWindow;
            if (selected is null)
            {
                return;
            }

            _session ??= CreateSession();
            await _session.StartAsync(selected, _profile);

            StartButton.Content = _session.IsRunning ? "หยุดแปล" : "เริ่มแปล";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to toggle the translation session.");
            StatusText.Text = $"เริ่มไม่สำเร็จ: {ex.Message}";
        }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = SelectedWindow is not null;
        }
    }

    private async Task RestartAsync()
    {
        if (_session is null || SelectedWindow is null)
        {
            return;
        }

        await _session.StopAsync();
        await _session.StartAsync(SelectedWindow, _profile);
    }

    private TranslationSession CreateSession()
    {
        var session = new TranslationSession(_settings.Translator, Dispatcher);
        session.Status += (_, message) => StatusText.Text = message;
        return session;
    }

    private void OnHotKey(object? sender, HotKeyAction action)
    {
        switch (action)
        {
            case HotKeyAction.ToggleTranslation:
                _ = ToggleAsync();
                break;

            case HotKeyAction.EditRegions:
                EditRegion();
                break;

            case HotKeyAction.ToggleOverlayVisible:
                _session?.ToggleOverlayVisible();
                break;

            case HotKeyAction.ToggleClickThrough:
                _clickThrough = !_clickThrough;
                _session?.SetClickThrough(_clickThrough);
                StatusText.Text = _clickThrough ? "overlay คลิกทะลุ" : "overlay รับคลิกแล้ว";
                break;

            case HotKeyAction.TranslateOnce:
                // Snip-once shares the region editor's selection flow; wiring it
                // to a one-shot pipeline pass is the next step.
                StatusText.Text = "โหมดแปลครั้งเดียวยังไม่พร้อมใช้งาน";
                break;
        }
    }

    private void UpdateMetrics()
    {
        var metrics = _session?.Metrics;

        if (metrics is null || metrics.FramesExamined == 0)
        {
            MetricsText.Text = string.Empty;
            return;
        }

        MetricsText.Text =
            $"OCR {metrics.AverageOcrMs:F0} ms · แปล {metrics.AverageTranslateMs:F0} ms · " +
            $"ข้ามเฟรม {metrics.SkipRatio:P0} · แปลไปแล้ว {metrics.TranslationsIssued} ประโยค";
    }

    private void OnSetupClick(object sender, RoutedEventArgs e)
    {
        // Reachable at any time, not only on first run: this is also how the
        // player switches model or moves the work between CPU and GPU.
        new SetupWindow(_settings) { Owner = this }.ShowDialog();

        StatusText.Text = TranslatorFactory.IsModelInstalled(_settings.Translator)
            ? "โมเดลพร้อมใช้งานแล้ว"
            : "ยังตั้งค่าโมเดลไม่ครบ — ยังแปลไม่ได้";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private async void OnClosed(object? sender, EventArgs e)
    {
        _metricsTimer.Stop();
        _hotKeys.Pressed -= OnHotKey;
        _hotKeys.Dispose();

        SettingsStore.Save(App.DataDirectory, _settings);

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }
}
