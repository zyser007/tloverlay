using System.Windows;
using System.Windows.Threading;
using Serilog;
using TLOverlay.App.Interop;
using TLOverlay.App.Views;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Diagnostics;
using TLOverlay.Core.Ocr;
using TLOverlay.Core.Pipeline;
using TLOverlay.Core.Profiles;
using TLOverlay.Core.Translation;

namespace TLOverlay.App.Services;

/// <summary>
/// Owns one live translation session: the overlay window, the pipeline, and the
/// hooks that keep the two aligned with the game.
///
/// Exists so the control panel stays a view. Everything here is about lifetime -
/// what to create, in what order, and what to tear down when the game closes.
/// </summary>
public sealed class TranslationSession : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TranslatorSettings _settings;

    private OverlayWindow? _overlay;
    private TranslationPipeline? _pipeline;
    private ForegroundWatcher? _watcher;
    private SqliteTranslationCache? _cache;
    private WindowsMediaOcrEngine? _ocr;
    private GameWindow? _target;
    private bool _translationsVisible = true;
    private bool _clickThrough = true;
    private bool _regionVisible;
    private bool _disposed;

    public TranslationSession(TranslatorSettings settings, Dispatcher? dispatcher = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dispatcher = dispatcher ?? Application.Current.Dispatcher;
    }

    public event EventHandler<string>? Status;

    public bool IsRunning => _pipeline?.IsRunning == true;

    public PipelineMetrics? Metrics => _pipeline?.Metrics;

    public GameWindow? Target => _target;

    public async Task StartAsync(GameWindow target, GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await StopAsync().ConfigureAwait(true);

        if (!WgcCaptureSource.IsSupported)
        {
            Report("Windows Graphics Capture ไม่รองรับบนเครื่องนี้ (ต้อง Windows 10 2004 ขึ้นไป)");
            return;
        }

        _ocr = new WindowsMediaOcrEngine();
        if (!_ocr.IsAvailable)
        {
            string available = string.Join(", ", WindowsMediaOcrEngine.AvailableLanguages());
            Report($"ไม่พบชุด OCR ภาษาอังกฤษ — ภาษาที่ติดตั้งอยู่: {available}");
            return;
        }

        if (!TranslatorFactory.IsModelInstalled(_settings))
        {
            Report("ยังไม่มีโมเดลแปลภาษา — กดปุ่ม “ตั้งค่าโมเดล” เพื่อดาวน์โหลด");
            return;
        }

        _target = target;

        var translator = TranslatorFactory.Create(_settings, profile, App.DataDirectory, out _cache);

        _pipeline = new TranslationPipeline(new WgcCaptureSource(), _ocr, translator);
        _pipeline.TranslationReady += OnTranslationReady;
        _pipeline.TextCleared += OnTextCleared;
        _pipeline.Failed += (_, message) => Report(message);

        _overlay = new OverlayWindow();
        _overlay.Show();
        _overlay.Attach(target.Handle, profile);
        _overlay.SetTranslationsVisible(_translationsVisible);
        _overlay.SetRegionVisible(_regionVisible);
        _overlay.SetClickThrough(_clickThrough);
        _overlay.PanelPlacementChanged += OnPanelPlacementChanged;

        if (!_overlay.IsHiddenFromCapture)
        {
            // Capture is scoped to the game window, so this is cosmetic rather
            // than a correctness problem - but the player should know why the
            // overlay turns up in their screenshots.
            Report("ซ่อน overlay จากการจับภาพไม่สำเร็จ — overlay จะติดไปในสกรีนช็อต");
        }

        _watcher = new ForegroundWatcher();
        _watcher.ForegroundChanged += OnForegroundChanged;
        _watcher.TargetMoved += OnTargetMoved;
        _watcher.Watch(target.Handle);

        Report("กำลังโหลดโมเดล...");

        if (!await translator.IsReadyAsync().ConfigureAwait(true))
        {
            Report("เริ่มเซิร์ฟเวอร์แปลภาษาไม่สำเร็จ — ดู log ที่ %AppData%\\TLOverlay\\logs");
            await StopAsync().ConfigureAwait(true);
            return;
        }

        await _pipeline.StartAsync(target.Handle, profile).ConfigureAwait(true);

        Report($"กำลังแปล: {target.Title}");
        Log.Information("Session started for {Title} ({Process}).", target.Title, target.ProcessName);
    }

    public bool TranslationsVisible => _translationsVisible;

    public bool RegionVisible => _regionVisible;

    /// <summary>
    /// The two layers toggle independently. Held on the session rather than only
    /// on the window so the choice survives a restart, which happens whenever a
    /// region is edited.
    /// </summary>
    public void SetTranslationsVisible(bool visible)
    {
        _translationsVisible = visible;
        _overlay?.SetTranslationsVisible(visible);
    }

    public void SetRegionVisible(bool visible)
    {
        _regionVisible = visible;
        _overlay?.SetRegionVisible(visible);
    }

    /// <summary>Puts the translation panel back where the display mode says.</summary>
    public void ResetPanelPlacement() => _overlay?.ResetPanelPlacement();

    public bool ClickThrough => _clickThrough;

    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        _overlay?.SetClickThrough(clickThrough);
    }

    public async Task StopAsync()
    {
        if (_watcher is not null)
        {
            _watcher.ForegroundChanged -= OnForegroundChanged;
            _watcher.TargetMoved -= OnTargetMoved;
            _watcher.Dispose();
            _watcher = null;
        }

        if (_pipeline is not null)
        {
            _pipeline.TranslationReady -= OnTranslationReady;
            _pipeline.TextCleared -= OnTextCleared;

            // Disposing the pipeline also disposes capture, OCR and the
            // translator - which is what shuts the model server down.
            await _pipeline.DisposeAsync().ConfigureAwait(true);
            _pipeline = null;
        }

        _ocr = null;

        _cache?.Dispose();
        _cache = null;

        if (_overlay is not null)
        {
            _overlay.PanelPlacementChanged -= OnPanelPlacementChanged;
            _overlay.Close();
            _overlay = null;
        }

        _target = null;
    }

    private void OnTranslationReady(object? sender, RegionTranslation translation) =>
        _ = _dispatcher.InvokeAsync(() => _overlay?.ShowTranslation(translation));

    private void OnTextCleared(object? sender, RegionCleared cleared) =>
        _ = _dispatcher.InvokeAsync(() => _overlay?.ClearText());

    /// <summary>Raised after a drag so the caller can write it into the profile.</summary>
    public event EventHandler<RelativeRect>? PanelPlacementChanged;

    private void OnPanelPlacementChanged(object? sender, RelativeRect placement) =>
        PanelPlacementChanged?.Invoke(this, placement);

    private void OnForegroundChanged(object? sender, bool isGameForeground) =>
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (_overlay is null)
            {
                return;
            }

            // Alt-tabbing away must hide the overlay immediately, or it sits on
            // top of whatever the player switched to.
            _overlay.Visibility = isGameForeground ? Visibility.Visible : Visibility.Hidden;

            if (isGameForeground)
            {
                _overlay.AlignToGame();
            }
        });

    private void OnTargetMoved(object? sender, EventArgs e) =>
        _ = _dispatcher.InvokeAsync(() => _overlay?.AlignToGame());

    private void Report(string message)
    {
        Log.Information("Session: {Message}", message);
        _ = _dispatcher.InvokeAsync(() => Status?.Invoke(this, message));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(true);
    }
}
