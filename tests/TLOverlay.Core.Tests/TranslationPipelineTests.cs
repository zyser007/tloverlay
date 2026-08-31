using TLOverlay.Core.Capture;
using TLOverlay.Core.Ocr;
using TLOverlay.Core.Pipeline;
using TLOverlay.Core.Profiles;

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

        // Several poll intervals worth of frames, none of which may be read.
        await capture.ServeAsync(5);

        Assert.Equal(0, translator.CallCount);
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

    /// <summary>Hands out identical frames, and counts how many were asked for.</summary>
    private sealed class FakeCaptureSource : ICaptureSource
    {
        private int _grabs;

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

            return Task.FromResult<CapturedFrame?>(
                new CapturedFrame(new byte[Width * Height * CapturedFrame.BytesPerPixel], Width, Height, Width * CapturedFrame.BytesPerPixel));
        }

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
        private readonly string _text;

        public FakeOcrEngine(string text) => _text = text;

        public bool IsAvailable => true;

        public string LanguageTag => "en-US";

        public Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult([OcrLine.FromText(_text, new TextRect(0, 0, 100, 20))]));

        public void Dispose()
        {
        }
    }
}
