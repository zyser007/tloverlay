namespace TLOverlay.Core.Diagnostics;

/// <summary>
/// Rolling timings for the control panel.
///
/// Worth surfacing rather than hiding in a log: when translations feel slow the
/// player needs to know whether it is capture, OCR or the model, because the fix
/// is different in each case (region too large, text too small, offload layers
/// to the GPU).
/// </summary>
public sealed class PipelineMetrics
{
    private readonly object _gate = new();

    public double LastCaptureMs { get; private set; }

    public double LastOcrMs { get; private set; }

    public double LastTranslateMs { get; private set; }

    public double AverageOcrMs { get; private set; }

    public double AverageTranslateMs { get; private set; }

    public long FramesExamined { get; private set; }

    public long FramesSkipped { get; private set; }

    public long TranslationsIssued { get; private set; }

    /// <summary>
    /// How often frames are being pulled right now. It moves on its own - the
    /// pipeline slows down while the screen is quiet - so seeing it makes the
    /// difference between "idling" and "stuck" obvious.
    /// </summary>
    public int PollIntervalMilliseconds { get; set; }

    /// <summary>
    /// The number that says whether change detection is doing its job. Anything
    /// below roughly 0.8 during normal play means regions are picking up animated
    /// scenery and OCR is running far more often than it needs to.
    /// </summary>
    public double SkipRatio => FramesExamined == 0 ? 0 : (double)FramesSkipped / FramesExamined;

    public void RecordFrame(double captureMs, bool skipped)
    {
        lock (_gate)
        {
            LastCaptureMs = captureMs;
            FramesExamined++;

            if (skipped)
            {
                FramesSkipped++;
            }
        }
    }

    public void RecordOcr(double milliseconds)
    {
        lock (_gate)
        {
            LastOcrMs = milliseconds;
            AverageOcrMs = Blend(AverageOcrMs, milliseconds);
        }
    }

    public void RecordTranslation(double milliseconds)
    {
        lock (_gate)
        {
            LastTranslateMs = milliseconds;
            AverageTranslateMs = Blend(AverageTranslateMs, milliseconds);
            TranslationsIssued++;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            LastCaptureMs = LastOcrMs = LastTranslateMs = 0;
            AverageOcrMs = AverageTranslateMs = 0;
            FramesExamined = FramesSkipped = TranslationsIssued = 0;
            PollIntervalMilliseconds = 0;
        }
    }

    // Exponential moving average: no history to keep, and recent samples are
    // what the player is actually experiencing.
    private static double Blend(double average, double sample) =>
        average == 0 ? sample : (average * 0.8) + (sample * 0.2);
}
