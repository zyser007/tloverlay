using TLOverlay.Core.Capture;
using TLOverlay.Core.Ocr;
using TLOverlay.Core.Pipeline;
using TLOverlay.Core.Profiles;
using Xunit;

namespace TLOverlay.Core.Tests;

/// <summary>
/// Covers the two translating modes. The pipeline is normally exercised against
/// a live game, which is exactly why the on-demand path needs tests: nothing on
/// this machine can press the button.
/// </summary>
public class TranslationPipelineTests
{
    private static GameProfile Profile() => new()
    {
        Regions = [CaptureRegion.BottomDialogue],
        SettleMilliseconds = 0,
        PollIntervalMilliseconds = 5,
    };

    [Fact]
    public async Task NothingIsTranslatedWhileAutomaticTranslationIsOff()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeTranslator();

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), translator)
        {
            AutomaticTranslation = false,
        };

        await pipeline.StartAsync(new IntPtr(1), Profile());

        // Several poll intervals. On demand the loop does not even grab, so this
        // waits on the clock rather than on frames that will never be asked for.
        await Task.Delay(120);

        Assert.Equal(0, translator.CallCount);
        Assert.Equal(0, capture.Issued);
    }

    [Fact]
    public async Task TranslateOnceReadsTheScreenEvenWithAutomaticTranslationOff()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeTranslator();
        var received = new List<RegionTranslation>();

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), translator)
        {
            AutomaticTranslation = false,
        };

        pipeline.TranslationReady += (_, translation) => received.Add(translation);

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await pipeline.TranslateOnceAsync();

        Assert.Equal(1, translator.CallCount);
        Assert.Equal("Hello there.", Assert.Single(received).SourceText);
    }

    [Fact]
    public async Task AskingTwiceForTheSameLineTranslatesItTwice()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeTranslator();

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), translator)
        {
            AutomaticTranslation = false,
        };

        await pipeline.StartAsync(new IntPtr(1), Profile());

        await pipeline.TranslateOnceAsync();
        await pipeline.TranslateOnceAsync();

        // The same-text guard is what makes automatic mode cheap, and what would
        // make pressing the button a second time appear to do nothing.
        Assert.Equal(2, translator.CallCount);
    }

    [Fact]
    public async Task AGrabThatReturnsNoFrameDoesNotEndTheSession()
    {
        var capture = new FakeCaptureSource { NextGrabIsEmpty = true };

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), new FakeTranslator());

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await capture.ServeAsync(3);

        // An empty grab means "no frame arrived in time", which happens whenever
        // an on-demand request supersedes the loop's own. Only capture stopping
        // ends the loop.
        Assert.True(pipeline.IsRunning);
    }

    [Fact]
    public async Task TheLoopEndsWhenCaptureStops()
    {
        var capture = new FakeCaptureSource();

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), new FakeTranslator());

        await pipeline.StartAsync(new IntPtr(1), Profile());
        capture.Stop();

        for (int attempt = 0; attempt < 200 && pipeline.IsRunning; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.False(pipeline.IsRunning);
    }

    [Fact]
    public async Task EveryFrameThePipelineTakesIsGivenBack()
    {
        var capture = new FakeCaptureSource();

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), new FakeTranslator());

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await capture.ServeAsync(6);
        await pipeline.StopAsync();

        // The leak that took the app past ten gigabytes was exactly this: frames
        // handed out and never released. One or two may still be in flight when
        // the loop stops, so this asserts the loop is not accumulating them.
        Assert.True(
            capture.Outstanding <= 1,
            $"{capture.Outstanding} of {capture.Issued} frames were never disposed.");
    }

    [Fact]
    public async Task AnOnDemandTranslationReleasesItsFrameToo()
    {
        var capture = new FakeCaptureSource();

        await using var pipeline = new TranslationPipeline(capture, new FakeOcrEngine("Hello there."), new FakeTranslator())
        {
            AutomaticTranslation = false,
        };

        await pipeline.StartAsync(new IntPtr(1), Profile());

        for (int press = 0; press < 5; press++)
        {
            await pipeline.TranslateOnceAsync();
        }

        await pipeline.StopAsync();

        Assert.True(
            capture.Outstanding <= 1,
            $"{capture.Outstanding} of {capture.Issued} frames were never disposed.");
    }


    [Fact]
    public async Task AFullScreenPassTranslatesEveryLineItFinds()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeBatchTranslator();
        ScreenTranslation? received = null;

        await using var pipeline = new TranslationPipeline(
            capture,
            new FakeOcrEngine(
            [
                OcrLine.FromText("Start Game", new TextRect(0, 0, 160, 30)),
                OcrLine.FromText("Options", new TextRect(0, 40, 160, 30)),
            ]),
            translator)
        {
            Mode = TranslationMode.OnDemandFullScreen,
        };

        pipeline.ScreenTranslationReady += (_, screen) => received = screen;

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await pipeline.TranslateScreenAsync();

        Assert.NotNull(received);
        Assert.Equal(2, received.Lines.Count);
        Assert.Equal("TH:Start Game", received.Lines[0].TranslatedText);

        // One request for the screen, not one per line - which on a metered
        // engine is the whole point.
        Assert.Equal(1, translator.BatchCallCount);
        Assert.Equal(0, translator.CallCount);
    }

    [Fact]
    public async Task ScreenLineBoundsAreFractionsOfTheFrame()
    {
        var capture = new FakeCaptureSource();
        ScreenTranslation? received = null;

        // The fake frame is 640x360, so a box at (320,180) sized 64x36 is dead
        // centre and a tenth of the frame.
        await using var pipeline = new TranslationPipeline(
            capture,
            new FakeOcrEngine([OcrLine.FromText("Continue", new TextRect(320, 180, 64, 36))]),
            new FakeBatchTranslator())
        {
            Mode = TranslationMode.OnDemandFullScreen,
        };

        pipeline.ScreenTranslationReady += (_, screen) => received = screen;

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await pipeline.TranslateScreenAsync();

        RelativeRect bounds = Assert.Single(received!.Lines).Bounds;

        Assert.Equal(0.5, bounds.X, 3);
        Assert.Equal(0.5, bounds.Y, 3);
        Assert.Equal(0.1, bounds.Width, 3);
        Assert.Equal(0.1, bounds.Height, 3);
    }

    [Fact]
    public async Task AFullScreenPassReleasesItsFrameBeforeTranslating()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeBatchTranslator();

        // The frame is eight megabytes at 1080p and translation can take tens of
        // seconds; holding it for all of that is exactly the leak this app had.
        // Deterministic because the loop does not grab in this mode: the only
        // frame in play is the sweep's own.
        int outstandingWhileTranslating = -1;
        translator.WhileTranslating = () => outstandingWhileTranslating = capture.Outstanding;

        await using var pipeline = new TranslationPipeline(
            capture,
            new FakeOcrEngine([OcrLine.FromText("Start Game", new TextRect(0, 0, 160, 30))]),
            translator)
        {
            Mode = TranslationMode.OnDemandFullScreen,
        };

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await pipeline.TranslateScreenAsync();
        await pipeline.StopAsync();

        Assert.Equal(0, outstandingWhileTranslating);
        Assert.True(capture.Outstanding <= 1, $"{capture.Outstanding} frames were never disposed.");
    }

    [Fact]
    public async Task FullScreenModeNeverTranslatesFromThePollLoop()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeBatchTranslator();

        await using var pipeline = new TranslationPipeline(
            capture,
            new FakeOcrEngine([OcrLine.FromText("Start Game", new TextRect(0, 0, 160, 30))]),
            translator)
        {
            Mode = TranslationMode.OnDemandFullScreen,
        };

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await Task.Delay(120);

        Assert.Equal(0, translator.CallCount);
        Assert.Equal(0, translator.BatchCallCount);

        // And it does not pull frames it has no intention of reading.
        Assert.Equal(0, capture.Issued);
    }

    [Fact]
    public async Task TheBusyFlagBracketsAFullScreenPass()
    {
        var capture = new FakeCaptureSource();
        var states = new List<bool>();

        await using var pipeline = new TranslationPipeline(
            capture,
            new FakeOcrEngine([OcrLine.FromText("Start Game", new TextRect(0, 0, 160, 30))]),
            new FakeBatchTranslator())
        {
            Mode = TranslationMode.OnDemandFullScreen,
        };

        pipeline.ScreenPassBusy += (_, busy) => states.Add(busy);

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await pipeline.TranslateScreenAsync();

        Assert.Equal([true, false], states);
    }

    [Fact]
    public async Task AScreenWithNothingReadableOnItReportsAnEmptyResult()
    {
        var capture = new FakeCaptureSource();
        var translator = new FakeBatchTranslator();
        ScreenTranslation? received = null;

        await using var pipeline = new TranslationPipeline(
            capture,
            new FakeOcrEngine([OcrLine.FromText("1,240", new TextRect(0, 0, 60, 20))]),
            translator)
        {
            Mode = TranslationMode.OnDemandFullScreen,
        };

        pipeline.ScreenTranslationReady += (_, screen) => received = screen;

        await pipeline.StartAsync(new IntPtr(1), Profile());
        await pipeline.TranslateScreenAsync();

        Assert.NotNull(received);
        Assert.Empty(received.Lines);
        Assert.Equal(0, translator.BatchCallCount);
    }

    [Fact]
    public void TheOldAutomaticFlagStillMapsOntoTheMode()
    {
        var pipeline = new TranslationPipeline(
            new FakeCaptureSource(),
            new FakeOcrEngine("Hello there."),
            new FakeTranslator());

        Assert.True(pipeline.AutomaticTranslation);

        pipeline.AutomaticTranslation = false;
        Assert.Equal(TranslationMode.OnDemandRegion, pipeline.Mode);

        pipeline.Mode = TranslationMode.OnDemandFullScreen;
        Assert.False(pipeline.AutomaticTranslation);
    }

    /// <summary>Hands out identical frames, and counts how many were asked for.</summary>
    private sealed class FakeCaptureSource : ICaptureSource
    {
        private int _grabs;
        private int _issued;
        private int _disposed;

        public bool IsRunning { get; private set; }

        /// <summary>Makes the next grab complete with no frame, as a superseded one does.</summary>
        public bool NextGrabIsEmpty { get; set; }

        public void Start(IntPtr windowHandle) => IsRunning = true;

        public void Stop() => IsRunning = false;

        public Task<CapturedFrame?> GrabAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _grabs);

            if (!IsRunning)
            {
                return Task.FromResult<CapturedFrame?>(null);
            }

            if (NextGrabIsEmpty)
            {
                return Task.FromResult<CapturedFrame?>(null);
            }

            const int Width = 640;
            const int Height = 360;

            Interlocked.Increment(ref _issued);

            return Task.FromResult<CapturedFrame?>(CapturedFrame.Adopt(
                new byte[Width * Height * CapturedFrame.BytesPerPixel],
                Width,
                Height,
                Width * CapturedFrame.BytesPerPixel,
                _ => Interlocked.Increment(ref _disposed)));
        }

        public int Issued => Volatile.Read(ref _issued);

        public int Outstanding => Volatile.Read(ref _issued) - Volatile.Read(ref _disposed);

        /// <summary>Waits until the loop has asked for at least this many frames.</summary>
        public async Task ServeAsync(int frames)
        {
            for (int attempt = 0; attempt < 400 && Volatile.Read(ref _grabs) < frames; attempt++)
            {
                await Task.Delay(10);
            }
        }

        public void Dispose() => Stop();
    }

    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly IReadOnlyList<OcrLine> _lines;

        public FakeOcrEngine(string text)
            : this([OcrLine.FromText(text, new TextRect(0, 0, 100, 20))])
        {
        }

        public FakeOcrEngine(IReadOnlyList<OcrLine> lines) => _lines = lines;

        public bool IsAvailable => true;

        public string LanguageTag => "en-US";

        public Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult(_lines));

        public void Dispose()
        {
        }
    }
}
