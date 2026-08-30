namespace TLOverlay.Core.Ocr;

/// <summary>
/// A rectangle in the coordinate space of the image that was recognised.
/// Kept separate from System.Drawing / WinRT rect types so the pipeline and its
/// tests never need a graphics stack.
/// </summary>
public readonly record struct TextRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterY => Y + (Height / 2);

    public static TextRect Union(TextRect a, TextRect b)
    {
        double left = Math.Min(a.X, b.X);
        double top = Math.Min(a.Y, b.Y);
        double right = Math.Max(a.Right, b.Right);
        double bottom = Math.Max(a.Bottom, b.Bottom);
        return new TextRect(left, top, right - left, bottom - top);
    }
}

public sealed record OcrWord(string Text, TextRect Bounds);

public sealed record OcrLine(string Text, TextRect Bounds, IReadOnlyList<OcrWord> Words)
{
    public static OcrLine FromText(string text, TextRect bounds) =>
        new(text, bounds, Array.Empty<OcrWord>());
}

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    public static readonly OcrResult Empty = new(Array.Empty<OcrLine>());

    public bool IsEmpty => Lines.Count == 0;
}
