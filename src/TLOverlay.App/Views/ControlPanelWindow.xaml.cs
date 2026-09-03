using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using TLOverlay.App.Interop;
using TLOverlay.App.Services;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Profiles;
using TLOverlay.Core.Setup;
using TLOverlay.Core.Translation;
using TLOverlay.Core.Update;

namespace TLOverlay.App.Views;

public partial class ControlPanelWindow : Window
{
    /// <summary>
    /// What each hotkey does, in the player's words. Paired with the binding list
    /// the keys are actually registered from, so the panel cannot advertise a key
    /// that was never bound.
    /// </summary>
    private static readonly Dictionary<HotKeyAction, string> ActionNames = new()
    {
        [HotKeyAction.ToggleTranslation] = "เปิด/ปิดการแปล",
        [HotKeyAction.EditRegions] = "เลือกพื้นที่การแปล",
        [HotKeyAction.ToggleTranslations] = "ซ่อน/แสดงข้อความแปล",
        [HotKeyAction.ToggleRegionOutlines] = "ซ่อน/แสดงพื้นที่การแปล",
        [HotKeyAction.ToggleClickThrough] = "สลับโหมดเมาส์",
        [HotKeyAction.TranslateOnce] = "แปลครั้งเดียว",
    };

    private readonly ProfileStore _profiles = new(AppPaths.ProfilesDirectory);
    private readonly AppSettings _settings = SettingsStore.Load(App.DataDirectory);
    private readonly GlobalHotKeyService _hotKeys = new();
    private readonly DispatcherTimer _metricsTimer;
    private readonly UpdateService _updates;

    private TranslationSession? _session;
    private GameProfile _profile = GameProfile.CreateDefault("Default");
    private IReadOnlyList<HotKeyBinding> _bindings = GlobalHotKeyService.Defaults;
    private UpdateManifest? _availableUpdate;
    private bool _busy;
    private bool _updating;

    public ControlPanelWindow()
    {
        InitializeComponent();

        WindowSizing.ClampToWorkArea(this);

        WindowList.ItemsSource = Windows;

        _hotKeys.Pressed += OnHotKey;
        _bindings = HotKeyProfile.Load(_settings);
        var failed = _hotKeys.Register(_bindings);

        BuildHotKeyGrid(failed);
        UpdateMouseModeHint();
        UpdateTranslateModeHint();

        _metricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _metricsTimer.Start();

        _updates = new UpdateService(_settings);

        Loaded += (_, _) =>
        {
            Refresh();

            // After the panel is up, never before it: a check that blocks the
            // first paint would make a slow connection look like a slow program.
            _ = CheckForUpdatesAsync(force: false);
        };
        Closed += OnClosed;

        if (failed.Count > 0)
        {
            StatusText.Text =
                "คีย์ลัดบางตัวถูกโปรแกรมอื่นใช้อยู่: " +
                string.Join(", ", failed.Select(static f => f.Gesture));
        }
    }

    public ObservableCollection<GameWindow> Windows { get; } = [];

    public GameWindow? SelectedWindow => WindowList.SelectedItem as GameWindow;

    private bool ClickThrough => ClickThroughOption.IsChecked == true;

    /// <summary>
    /// Prints one row per binding. Unavailable keys are struck through rather
    /// than hidden, so a key that silently did nothing is visibly accounted for.
    /// </summary>
    private void BuildHotKeyGrid(IReadOnlyList<HotKeyBinding> failed)
    {
        HotKeyGrid.Children.Clear();

        foreach (var binding in _bindings)
        {
            bool available = !failed.Contains(binding);

            var keycap = new Border
            {
                Style = (Style)FindResource("Keycap"),
                Child = new TextBlock
                {
                    Text = binding.Gesture,
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 11.5,
                    Foreground = (Brush)FindResource(available ? "PanelForeground" : "PanelMuted"),
                },
            };

            var label = new TextBlock
            {
                Text = ActionNames.TryGetValue(binding.Action, out string? name) ? name : binding.Action.ToString(),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(available ? "PanelForeground" : "PanelMuted"),
                TextDecorations = available ? null : TextDecorations.Strikethrough,
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 12, 6),
            };

            row.Children.Add(keycap);
            row.Children.Add(label);
            HotKeyGrid.Children.Add(row);
        }
    }

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

        // Load whichever profile matches this game, so the area and the panel
        // placement set earlier come back automatically.
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

        // Hand the editor the area already set, so a redraw is a deliberate
        // replacement rather than a surprise, and Escape keeps what was there.
        var editor = new RegionEditorWindow(selected.Handle, _profile.Region) { Owner = this };

        if (editor.ShowDialog() != true)
        {
            return;
        }

        _profile.SetRegion(editor.Result);
        _profiles.Save(_profile);

        StatusText.Text = editor.Result is null
            ? $"ล้างพื้นที่การแปลของ {_profile.Name} แล้ว"
            : $"บันทึกพื้นที่การแปลของ {_profile.Name} แล้ว";

        // A moved area invalidates everything the pipeline has settled on, so
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
                SetStartButtonState(running: false);
                UpdateTranslateModeHint();
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

            SetStartButtonState(_session.IsRunning);
            UpdateTranslateModeHint();
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

    private void SetStartButtonState(bool running)
    {
        StartButtonText.Text = running ? "หยุดแปล" : "เริ่มแปล";
        StartButtonIcon.Data = (Geometry)FindResource(running ? "IconStop" : "IconPlay");
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
        var session = new TranslationSession(_settings.Translator, _settings.InstallRoot, Dispatcher);
        session.Status += (_, message) => StatusText.Text = message;

        // The panel remembers where it was dragged to, per game.
        session.PanelPlacementChanged += (_, placement) =>
        {
            _profile.PanelBounds = placement;
            _profiles.Save(_profile);
        };

        session.SetTranslationsVisible(ShowTranslationsToggle.IsChecked == true);
        session.SetRegionVisible(ShowRegionToggle.IsChecked == true);
        session.SetClickThrough(ClickThrough);
        session.SetAutomaticTranslation(AutoTranslateToggle.IsChecked == true);

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

            case HotKeyAction.ToggleTranslations:
                ShowTranslationsToggle.IsChecked = ShowTranslationsToggle.IsChecked != true;
                break;

            case HotKeyAction.ToggleRegionOutlines:
                ShowRegionToggle.IsChecked = ShowRegionToggle.IsChecked != true;
                break;

            case HotKeyAction.ToggleClickThrough:
                // Flipping the radio raises Checked, which is the one place the
                // mode is actually applied.
                if (ClickThrough)
                {
                    InteractiveOption.IsChecked = true;
                }
                else
                {
                    ClickThroughOption.IsChecked = true;
                }

                break;

            case HotKeyAction.TranslateOnce:
                _ = TranslateOnceAsync();
                break;
        }
    }

    /// <summary>
    /// Both the buttons and the hotkeys route through here, so the controls on
    /// screen can never disagree with what the overlay is drawing.
    /// </summary>
    private void OnLayerToggled(object sender, RoutedEventArgs e)
    {
        _session?.SetTranslationsVisible(ShowTranslationsToggle.IsChecked == true);
        _session?.SetRegionVisible(ShowRegionToggle.IsChecked == true);
    }

    private void OnAutoTranslateChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the hint exists.
        if (TranslateModeHint is null)
        {
            return;
        }

        _session?.SetAutomaticTranslation(AutoTranslateToggle.IsChecked == true);
        UpdateTranslateModeHint();
    }

    private void UpdateTranslateModeHint()
    {
        bool automatic = AutoTranslateToggle.IsChecked == true;
        bool running = _session?.IsRunning == true;

        TranslateModeHint.Text = automatic
            ? "แปลอัตโนมัติ: แปลให้เองทุกครั้งที่ข้อความในพื้นที่เปลี่ยนและนิ่งแล้ว"
            : "แปลเอง: จะแปลเมื่อกด “แปลครั้งเดียว” หรือ Ctrl+Alt+S เท่านั้น — เซสชันยังทำงานอยู่ โมเดลจึงตอบทันที";

        // Only meaningful while a session is attached to a game.
        TranslateOnceButton.IsEnabled = running;
    }

    private async Task TranslateOnceAsync()
    {
        if (_session?.IsRunning != true)
        {
            StatusText.Text = "ยังไม่ได้เริ่มแปล — กด “เริ่มแปล” ก่อน";
            return;
        }

        try
        {
            await _session.TranslateOnceAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "On-demand translation failed.");
            StatusText.Text = $"แปลไม่สำเร็จ: {ex.Message}";
        }
    }

    private void OnTranslateOnceClick(object sender, RoutedEventArgs e) => _ = TranslateOnceAsync();

    private void OnMouseModeChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the hint exists.
        if (MouseModeHint is null)
        {
            return;
        }

        _session?.SetClickThrough(ClickThrough);
        UpdateMouseModeHint();
    }

    private void UpdateMouseModeHint()
    {
        MouseModeHint.Text = ClickThrough
            ? "คลิกทะลุ: เมาส์ทะลุไปที่เกมทั้งหมด — เล่นเกมได้ตามปกติ แต่ย้ายกรอบข้อความแปลไม่ได้"
            : "โต้ตอบได้: ลากกรอบข้อความแปลเพื่อย้าย และลากมุมขวาล่างเพื่อปรับขนาด — เกมจะไม่ได้รับคลิกที่ตกบนกรอบ";

        ResetPanelButton.IsEnabled = _profile.PanelBounds is not null;
    }

    private void OnResetPanelClick(object sender, RoutedEventArgs e)
    {
        _profile.PanelBounds = null;
        _profiles.Save(_profile);
        _session?.ResetPanelPlacement();

        StatusText.Text = "ย้ายกรอบข้อความแปลกลับตำแหน่งเริ่มต้นแล้ว";
        UpdateMouseModeHint();
    }

    private void UpdateMetrics()
    {
        // Memory is worth showing even before a session starts - it is what
        // tells a player on a small machine whether the model fits.
        string memory = $"แรม แอป {MemoryReadout.Format(MemoryReadout.AppBytes)}";

        long server = MemoryReadout.ModelServerBytes;
        if (server > 0)
        {
            memory += $" · โมเดล {MemoryReadout.Format(server)}";
        }

        var metrics = _session?.Metrics;

        if (metrics is null || metrics.FramesExamined == 0)
        {
            MetricsText.Text = memory;
            return;
        }

        MetricsText.Text =
            $"OCR {metrics.AverageOcrMs:F0} ms · แปล {metrics.AverageTranslateMs:F0} ms · " +
            $"ข้ามเฟรม {metrics.SkipRatio:P0} · แปลไปแล้ว {metrics.TranslationsIssued} ประโยค" +
            Environment.NewLine +
            $"{memory} · ดึงภาพทุก {metrics.PollIntervalMilliseconds} ms";
    }

    private void OnSetupClick(object sender, RoutedEventArgs e)
    {
        // Reachable at any time, not only on first run: this is also how the
        // player switches model or moves the work between CPU and GPU.
        new SetupWindow(_settings) { Owner = this }.ShowDialog();

        // Naming the engine is the point: a cloud engine sends the game's text
        // off this machine, and that should be visible without opening a dialog.
        string engine = _settings.Translator.Backend switch
        {
            TranslationBackend.Google => "Google แปลภาษา",
            TranslationBackend.OpenAi => $"OpenAI · {_settings.Translator.OpenAiModel}",
            _ => "โมเดลในเครื่อง",
        };

        StatusText.Text = TranslatorFactory.IsReadyToTranslate(_settings.Translator, _settings.InstallRoot)
            ? $"พร้อมแปลแล้ว — {engine}"
            : $"ยังตั้งค่าไม่ครบ ({engine}) — ยังแปลไม่ได้";
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings, _hotKeys, _updates) { Owner = this };

        // A manual check in there still belongs on the banner out here.
        window.UpdateFound += (_, manifest) =>
        {
            _availableUpdate = manifest;
            ShowUpdateBanner(manifest);
        };

        window.ShowDialog();

        if (window.HotKeysChanged)
        {
            _bindings = HotKeyProfile.Load(_settings);
            BuildHotKeyGrid(_hotKeys.Register(_bindings));
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Fills the update card. Built in code so the policy list and the running
    /// version come from one place rather than being restated in XAML.
    /// </summary>
    /// <summary>
    /// Looks for a new version. <paramref name="force"/> is the player pressing
    /// the button, which reports "you are up to date" rather than saying nothing.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool force)
    {
        if (_updating)
        {
            return;
        }

        try
        {
            UpdateManifest? found = await _updates.CheckAsync(force);

            if (found is null)
            {
                return;
            }

            _availableUpdate = found;
            ShowUpdateBanner(found);

            if (_settings.Updates == UpdatePolicy.Automatic && UpdateService.CanSelfUpdate)
            {
                await InstallUpdateAsync(found);
            }
        }
        catch (Exception ex) when (ex is UpdateCheckException or HttpRequestException or TaskCanceledException)
        {
            Log.Warning(ex, "Update check failed.");

            // Only ever said out loud when the player asked: a background check
            // that cannot reach GitHub is not their problem to hear about. The
            // settings window reports its own manual checks.
            if (force)
            {
                StatusText.Text = $"ตรวจสอบเวอร์ชันใหม่ไม่สำเร็จ: {ex.Message}";
            }
        }
    }

    private void ShowUpdateBanner(UpdateManifest manifest)
    {
        UpdateTitle.Text = $"มีเวอร์ชันใหม่ {manifest.Version}";

        UpdateDetail.Text = UpdateService.CanSelfUpdate
            ? $"ดาวน์โหลด {manifest.MegabytesApproximately:F0} MB แล้วเปิดโปรแกรมใหม่ให้อัตโนมัติ · เวอร์ชันปัจจุบัน {App.Version}"
            : $"โฟลเดอร์นี้เขียนไฟล์ไม่ได้ จึงต้องดาวน์โหลดมาแทนที่เอง · เวอร์ชันปัจจุบัน {App.Version}";

        UpdateInstallButton.IsEnabled = UpdateService.CanSelfUpdate;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateNotesClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                _availableUpdate.ReleasePage.ToString())
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the release page.");
            StatusText.Text = $"เปิดหน้า release ไม่ได้: {_availableUpdate.ReleasePage}";
        }
    }

    private void OnUpdateSkipClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
        {
            _updates.Skip(_availableUpdate);
        }

        _availableUpdate = null;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnUpdateInstallClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
        {
            _ = InstallUpdateAsync(_availableUpdate);
        }
    }

    /// <summary>
    /// Installs an update and restarts into it.
    ///
    /// Stopping a running session first is not tidiness: the overlay owns a
    /// capture session and a model server held in a job object, and replacing the
    /// executable underneath all that is how a player ends up with an orphaned
    /// llama-server holding two gigabytes.
    /// </summary>
    private async Task InstallUpdateAsync(UpdateManifest manifest)
    {
        if (_updating)
        {
            return;
        }

        if (_session?.IsRunning == true)
        {
            MessageBoxResult answer = MessageBox.Show(
                $"ต้องหยุดการแปลก่อนอัพเดทเป็น {manifest.Version}\n\nหยุดแล้วอัพเดทเลยไหม?",
                "TLOverlay",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            await ToggleAsync();
        }

        _updating = true;
        UpdateActions.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;

        var progress = new Progress<DownloadProgress>(report =>
        {
            UpdateProgress.IsIndeterminate = report.Fraction is null;
            UpdateProgress.Value = report.Fraction ?? 0;
            UpdateDetail.Text =
                $"กำลังดาวน์โหลด {report.BytesCompleted / 1024d / 1024d:F0} / {manifest.MegabytesApproximately:F0} MB";
        });

        try
        {
            UpdateTitle.Text = $"กำลังอัพเดทเป็น {manifest.Version}";

            if (await _updates.InstallAndRestartAsync(manifest, progress))
            {
                // The new build is already starting; this one steps out of its way.
                Application.Current.Shutdown();
                return;
            }

            UpdateTitle.Text = "อัพเดทไม่สำเร็จ";
            UpdateDetail.Text =
                "ไฟล์ที่ดาวน์โหลดมาเปิดไม่ผ่านการทดสอบ จึงไม่ได้ติดตั้งทับ — เวอร์ชันเดิมยังใช้งานได้ตามปกติ";
        }
        catch (Exception ex) when (ex is UpdateInstallException or ModelDownloadException or HttpRequestException or IOException)
        {
            Log.Error(ex, "Update to {Version} failed.", manifest.Version);
            UpdateTitle.Text = "อัพเดทไม่สำเร็จ";
            UpdateDetail.Text = ex.Message;
        }
        finally
        {
            _updating = false;
            UpdateActions.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

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
