using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TLOverlay.Core.Capture;
using TLOverlay.Core.Diagnostics;
using TLOverlay.Core.Ocr;
using TLOverlay.Core.Profiles;
using TLOverlay.Core.Translation;

namespace TLOverlay.Core.Pipeline;

/// <summary>One region's translated text, ready to draw.</summary>
public sealed record RegionTranslation(
    string RegionName,
    string SourceText,
    string TranslatedText,
    TextRect RegionBoundsInWindow,
    TextRect TextBoundsInWindow);

/// <summary>Text disappeared from a region, so whatever is drawn there should go.</summary>
public sealed record RegionCleared(string RegionName);

/// <summary>
/// Drives capture -> change detection -> OCR -> translation for every region of
/// the active profile.
///
/// The ordering here is the whole performance story. Capture is cheap, OCR is
/// moderate, translation is expensive, so each stage only runs when the one
/// before it says something actually changed.
/// </summary>
public sealed class TranslationPipeline : IAsyncDisposable
{
    /// <summary>
    /// Above this similarity, newly recognised text is treated as the same line
    /// read slightly differently rather than as new dialogue.
    /// </summary>
    private const double SameTextThreshold = 0.93;

    private readonly ICaptureSource _capture;
    private readonly IOcrEngine _ocr;
    private readonly ITranslator _translator;
    private readonly ILogger _logger;
    private readonly Dictionary<string, RegionState> _regions = new(StringComparer.Ordinal);

    /// <summary>
    /// One grab at a time. The capture source keeps a single pending request and
    /// completes an older one with null when a newer arrives, so an on-demand
    /// translation firing between the loop's poll and its frame would hand the
    /// loop a null - which it reads as "the game exited".
    /// </summary>
    private readonly SemaphoreSlim _grabGate = new(1, 1);

    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    private GameProfile _profile = GameProfile.CreateDefault("Default");
    private bool _disposed;

    public TranslationPipeline(
        ICaptureSource capture,
        IOcrEngine ocr,
        ITranslator translator,
        ILogger<TranslationPipeline>? logger = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _logger = logger ?? NullLogger<TranslationPipeline>.Instance;
    }

    public event EventHandler<RegionTranslation>? TranslationReady;

    public event EventHandler<RegionCleared>? TextCleared;

    public event EventHandler<string>? Failed;

    public PipelineMetrics Metrics { get; } = new();

    /// <summary>
    /// Whether the pipeline translates on its own as the screen changes.
    ///
    /// Turning it off leaves the session running - capture stays attached and the
    /// model stays loaded - so a single on-demand translation is instant rather
    /// than paying for a cold start.
    /// </summary>
    public bool AutomaticTranslation { get; set; } = true;

    public bool IsRunning => _loop is { IsCompleted: false };

    public async Task StartAsync(IntPtr windowHandle, GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await StopAsync().ConfigureAwait(false);

        _profile = profile;
        _regions.Clear();

        foreach (var region in profile.Regions.Where(static r => r.IsValid))
        {
            _regions[region.Name] = new RegionState(
                region,
                new ChangeDetector(settleTime: TimeSpan.FromMilliseconds(profile.SettleMilliseconds)));
        }

        Metrics.Reset();
        _capture.Start(windowHandle);

        var cancellation = new CancellationTokenSource();
        _loopCancellation = cancellation;

        // The token is read here rather than inside the lambda: the lambda runs
        // on a threadpool thread some time later, and a stop that arrives first
        // has already set the field back to null.
        CancellationToken token = cancellation.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    public async Task StopAsync()
    {
        var cancellation = _loopCancellation;
        var loop = _loop;

        _loopCancellation = null;
        _loop = null;

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        cancellation?.Dispose();

        foreach (var state in _regions.Values)
        {
            state.CancelInFlight();
        }

        _capture.Stop();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = new Stopwatch();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                stopwatch.Restart();
                CapturedFrame? frame = await GrabAsync(cancellationToken).ConfigureAwait(false);
                double captureMs = stopwatch.Elapsed.TotalMilliseconds;

                if (frame is null)
                {
                    if (!_capture.IsRunning)
                    {
                        // Capture stopped - usually the game exited.
                        break;
                    }

                    // A frame that never arrived. Poll again rather than treating
                    // one empty grab as the end of the session.
                    await Task.Delay(_profile.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool anyWork = false;

                if (AutomaticTranslation)
                {
                    foreach (var state in _regions.Values)
                    {
                        anyWork |= await ProcessRegionAsync(state, frame, force: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                Metrics.RecordFrame(captureMs, skipped: !anyWork);

                await Task.Delay(_profile.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad frame must not end the session; log, report and keep going.
                _logger.LogError(ex, "Pipeline iteration failed.");
                Failed?.Invoke(this, ex.Message);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Translates the region right now, whether or not anything changed and
    /// whether or not automatic translation is on.
    ///
    /// Deliberately bypasses both gates. Someone who asks for a translation has
    /// already decided the text is worth reading again; answering "nothing
    /// changed" would be technically true and useless.
    /// </summary>
    public async Task TranslateOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CapturedFrame? frame = await GrabAsync(cancellationToken).ConfigureAwait(false);

        if (frame is null)
        {
            return;
        }

        foreach (var state in _regions.Values)
        {
            await ProcessRegionAsync(state, frame, force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Serialises grabs, so the loop and an on-demand request cannot cancel each other.</summary>
    private async Task<CapturedFrame?> GrabAsync(CancellationToken cancellationToken)
    {
        await _grabGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await _capture.GrabAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _grabGate.Release();
        }
    }

    /// <summary>Returns true if this region did work beyond change detection.</summary>
    private async Task<bool> ProcessRegionAsync(
        RegionState state,
        CapturedFrame frame,
        bool force,
        CancellationToken cancellationToken)
    {
        var (x, y, width, height) = state.Region.ToPixels(frame.Width, frame.Height);
        CapturedFrame crop = frame.Crop(x, y, width, height);

        ChangeState change = state.Detector.Observe(crop.Signature(), DateTimeOffset.UtcNow);

        if (!force && change != ChangeState.Settled)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        OcrResult recognized = await _ocr.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
        Metrics.RecordOcr(stopwatch.Elapsed.TotalMilliseconds);

        string sourceText = TextAssembler.Assemble(recognized);

        if (sourceText.Length == 0)
        {
            if (state.LastSourceText.Length > 0)
            {
                state.LastSourceText = string.Empty;
                state.CancelInFlight();
                TextCleared?.Invoke(this, new RegionCleared(state.Region.Name));
            }

            return true;
        }

        // Second line of defence against mid-reveal text: if the new reading is
        // the old one plus more characters and still has no sentence ending, the
        // game is typing. Re-arm and wait rather than translating a fragment.
        if (!force
            && state.LastSourceText.Length > 0
            && sourceText.StartsWith(state.LastSourceText, StringComparison.Ordinal)
            && !TextAssembler.LooksComplete(sourceText))
        {
            state.Detector.Reset();
            return true;
        }

        if (!force && TextSimilarity.Ratio(sourceText, state.LastSourceText) >= SameTextThreshold)
        {
            // Same line, read slightly differently. Not worth a translation.
            return true;
        }

        state.LastSourceText = sourceText;

        var token = state.BeginTranslation(cancellationToken);

        try
        {
            stopwatch.Restart();
            string translated = await _translator.TranslateAsync(sourceText, token).ConfigureAwait(false);
            Metrics.RecordTranslation(stopwatch.Elapsed.TotalMilliseconds);

            if (translated.Length == 0)
            {
                return true;
            }

            TextRect textBounds = MeasureText(recognized, x, y);

            TranslationReady?.Invoke(this, new RegionTranslation(
                state.Region.Name,
                sourceText,
                translated,
                new TextRect(x, y, width, height),
                textBounds));
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer on-screen text. Dropping this one is correct.
        }

        return true;
    }

    /// <summary>
    /// Union of the recognised lines, shifted into game-window coordinates, so
    /// inline mode can cover exactly the original text rather than the whole
    /// region.
    /// </summary>
    private static TextRect MeasureText(OcrResult recognized, int regionX, int regionY)
    {
        TextRect? bounds = null;

        foreach (var line in recognized.Lines)
        {
            if (line.Bounds.Width <= 0 || line.Bounds.Height <= 0)
            {
                continue;
            }

            bounds = bounds is null ? line.Bounds : TextRect.Union(bounds.Value, line.Bounds);
        }

        if (bounds is null)
        {
            return default;
        }

        return new TextRect(
            bounds.Value.X + regionX,
            bounds.Value.Y + regionY,
            bounds.Value.Width,
            bounds.Value.Height);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        _capture.Dispose();
        _ocr.Dispose();
        _grabGate.Dispose();
        await _translator.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class RegionState
    {
        private CancellationTokenSource? _translation;

        public RegionState(CaptureRegion region, ChangeDetector detector)
        {
            Region = region;
            Detector = detector;
        }

        public CaptureRegion Region { get; }

        public ChangeDetector Detector { get; }

        public string LastSourceText { get; set; } = string.Empty;

        /// <summary>
        /// Starts a translation, abandoning any earlier one for this region. The
        /// player should never see a translation of text that has already left
        /// the screen.
        /// </summary>
        public CancellationToken BeginTranslation(CancellationToken linked)
        {
            CancelInFlight();
            _translation = CancellationTokenSource.CreateLinkedTokenSource(linked);
            return _translation.Token;
        }

        public void CancelInFlight()
        {
            var previous = _translation;
            _translation = null;

            if (previous is not null)
            {
                previous.Cancel();
                previous.Dispose();
            }
        }
    }
}
