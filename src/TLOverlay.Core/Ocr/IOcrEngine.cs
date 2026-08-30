using TLOverlay.Core.Capture;

namespace TLOverlay.Core.Ocr;

public interface IOcrEngine : IDisposable
{
    /// <summary>
    /// False when the recogniser could not be created - almost always a missing
    /// English OCR language pack, which the UI should say out loud rather than
    /// silently producing no text.
    /// </summary>
    bool IsAvailable { get; }

    string LanguageTag { get; }

    Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken = default);
}
