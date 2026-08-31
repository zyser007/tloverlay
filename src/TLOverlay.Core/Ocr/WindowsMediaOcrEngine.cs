using Windows.Globalization;
using TLOverlay.Core.Capture;
using WinOcr = Windows.Media.Ocr;

namespace TLOverlay.Core.Ocr;

/// <summary>
/// Offline OCR through Windows.Media.Ocr.
///
/// Chosen over Tesseract or a bundled neural recogniser because it ships with
/// Windows, needs no model download, and recognises a dialogue-box-sized region
/// in tens of milliseconds - which is what makes per-frame polling affordable at
/// all. Bounding boxes come back in the coordinate space of the frame that was
/// passed in, not of the upscaled image OCR actually saw.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    private readonly Lazy<WinOcr.OcrEngine?> _engine;
    private readonly PreprocessOptions _preprocess;

    public WindowsMediaOcrEngine(string languageTag = "en-US", PreprocessOptions? preprocess = null)
    {
        LanguageTag = languageTag;

        // Deliberately lazy. The engine is built on first use, which happens on
        // the pipeline's threadpool thread - the same thread that will call it.
        // Constructing it here would build it on WPF's STA UI thread, and using
        // that instance from the pipeline is the "marshalled for a different
        // thread" failure.
        _engine = new Lazy<WinOcr.OcrEngine?>(
            () => TryCreateEngine(languageTag),
            LazyThreadSafetyMode.ExecutionAndPublication);

        _preprocess = preprocess ?? new PreprocessOptions
        {
            // The recogniser refuses anything larger, so make its own limit the
            // ceiling rather than guessing one.
            MaxDimension = (int)WinOcr.OcrEngine.MaxImageDimension,
        };
    }

    /// <summary>
    /// Probes with a throwaway engine rather than the one recognition will use,
    /// so asking the question from the UI thread cannot pin an engine to it.
    /// </summary>
    public bool IsAvailable => TryCreateEngine(LanguageTag) is not null;

    public string LanguageTag { get; }

    /// <summary>
    /// Language tags this machine can actually recognise. Shown in the UI when
    /// the requested language is missing, so the player knows what to install.
    /// </summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        try
        {
            return WinOcr.OcrEngine.AvailableRecognizerLanguages
                .Select(static language => language.LanguageTag)
                .ToList();
        }
        catch (Exception ex) when (ex is TypeLoadException or NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    public async Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        WinOcr.OcrEngine? engine = _engine.Value;

        if (engine is null)
        {
            return OcrResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Prepare always builds a new frame, so this owns it - and its buffer is
        // pooled, so giving it back promptly is the point.
        using CapturedFrame prepared = ImagePreprocessor.Prepare(frame, _preprocess);

        // Everything downstream wants coordinates in the caller's frame, so undo
        // whatever scaling preprocessing applied.
        double inverseScale = prepared.Width == 0 ? 1.0 : (double)frame.Width / prepared.Width;

        using var bitmap = SoftwareBitmapInterop.ToSoftwareBitmap(prepared);

        cancellationToken.ThrowIfCancellationRequested();

        WinOcr.OcrResult recognized = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);

        return Convert(recognized, inverseScale);
    }

    private static OcrResult Convert(WinOcr.OcrResult recognized, double inverseScale)
    {
        var lines = new List<OcrLine>(recognized.Lines.Count);

        foreach (WinOcr.OcrLine line in recognized.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            TextRect? bounds = null;

            foreach (WinOcr.OcrWord word in line.Words)
            {
                var rect = new TextRect(
                    word.BoundingRect.X * inverseScale,
                    word.BoundingRect.Y * inverseScale,
                    word.BoundingRect.Width * inverseScale,
                    word.BoundingRect.Height * inverseScale);

                words.Add(new OcrWord(word.Text, rect));
                bounds = bounds is null ? rect : TextRect.Union(bounds.Value, rect);
            }

            // A Windows OCR line carries no rectangle of its own; the union of its
            // words is what the overlay positions against.
            lines.Add(new OcrLine(line.Text, bounds ?? default, words));
        }

        return new OcrResult(lines);
    }

    private static WinOcr.OcrEngine? TryCreateEngine(string languageTag)
    {
        try
        {
            var engine = WinOcr.OcrEngine.TryCreateFromLanguage(new Language(languageTag));

            // Falling back to the user's profile languages beats failing outright:
            // an en-GB install has no en-US recogniser but reads English fine.
            return engine ?? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        // The WinRT engine holds no disposable resources; the method exists so
        // callers can treat every engine implementation the same way.
    }
}
