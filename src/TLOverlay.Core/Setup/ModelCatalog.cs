namespace TLOverlay.Core.Setup;

/// <summary>Which llama.cpp build to fetch.</summary>
public enum LlamaBackend
{
    /// <summary>Runs on the CPU. Slower per line, but never competes with the game for VRAM.</summary>
    Cpu,

    /// <summary>CUDA build for NVIDIA cards.</summary>
    Cuda,
}

/// <summary>One downloadable translation model.</summary>
public sealed record ModelEntry(
    string Id,
    string DisplayName,
    Uri Url,
    long ApproximateBytes,
    string License,
    bool CommercialUseAllowed,
    string Notes)
{
    public double ApproximateGigabytes => ApproximateBytes / 1024d / 1024d / 1024d;

    /// <summary>
    /// One line fit for the model dropdown. Licence is on it deliberately: the
    /// player should see that a model is non-commercial while choosing it, not
    /// after two gigabytes have finished downloading.
    /// </summary>
    public string Summary => $"{DisplayName} · {ApproximateGigabytes:F1} GB · {Notes}";
}

/// <summary>The models offered in Setup.</summary>
public static class ModelCatalog
{
    public const long Gigabyte = 1024L * 1024 * 1024;

    public static IReadOnlyList<ModelEntry> Entries { get; } =
    [
        new ModelEntry(
            "typhoon2-3b-q4",
            "Typhoon 2 3B (q4_k_m)",
            new Uri("https://huggingface.co/scb10x/llama3.2-typhoon2-3b-instruct-gguf/resolve/main/llama3.2-typhoon2-3b-instruct-q4_k_m.gguf"),
            (long)(2.02 * Gigabyte),
            "See the model card",
            CommercialUseAllowed: true,
            "จูนภาษาไทยโดยเฉพาะ"),

        new ModelEntry(
            "gemma3-4b-q4",
            "Gemma 3 4B (q4_k_m)",
            new Uri("https://huggingface.co/ggml-org/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf"),
            (long)(2.49 * Gigabyte),
            "Gemma Terms of Use",
            CommercialUseAllowed: true,
            "ทั่วไป คุณภาพดี"),
    ];

    public static ModelEntry Default => Entries[0];

    public static ModelEntry? FindById(string? id) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
}
