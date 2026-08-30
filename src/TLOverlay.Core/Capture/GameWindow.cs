namespace TLOverlay.Core.Capture;

/// <summary>A top-level window the user could target for translation.</summary>
public sealed record GameWindow(
    IntPtr Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    int Width,
    int Height)
{
    public override string ToString() => $"{Title} ({ProcessName}) {Width}x{Height}";
}
