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
    public string Summary => ApproximateBytes > 0
        ? $"{DisplayName} · {ApproximateGigabytes:F1} GB · {Notes}"
        : $"{DisplayName} · {Notes}";
}

/// <summary>The models offered in Setup.</summary>
public static class ModelCatalog
{
    public const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>
    /// Every URL here has been checked to return 200 with the size given.
    /// tools/check-model-urls.ps1 re-checks them in CI, because an entry that
    /// has quietly moved upstream fails on the user's machine after they press
    /// download, which is the worst place to discover it.
    /// </summary>
    public static IReadOnlyList<ModelEntry> Entries { get; } =
    [
        new ModelEntry(
            "gemma3-4b-q4km",
            "Gemma 3 4B Instruct (Q4_K_M)",
            new Uri("https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf"),
            (long)(2.32 * Gigabyte),
            "Gemma Terms of Use",
            CommercialUseAllowed: true,
            "เล็กและเร็ว เหมาะกับรันบน CPU"),

        new ModelEntry(
            "gemma3-4b-qat-q4",
            "Gemma 3 4B Instruct QAT (Q4_0)",
            new Uri("https://huggingface.co/ggml-org/gemma-3-4b-it-qat-GGUF/resolve/main/gemma-3-4b-it-qat-Q4_0.gguf"),
            (long)(2.35 * Gigabyte),
            "Gemma Terms of Use",
            CommercialUseAllowed: true,
            "ฝึกมาเพื่อ quantise โดยเฉพาะ คุณภาพดีกว่าที่ขนาดเท่ากัน"),

        new ModelEntry(
            "typhoon-v15-8b-q4km",
            "Typhoon v1.5 8B Instruct (Q4_K_M)",
            new Uri("https://huggingface.co/typhoon-ai/llama-3-typhoon-v1.5-8b-instruct-gguf/resolve/main/llama-3-typhoon-v1.5-8b-instruct.Q4_K_M.gguf"),
            (long)(4.58 * Gigabyte),
            "Llama 3 Community License",
            CommercialUseAllowed: true,
            "จูนภาษาไทยโดยเฉพาะ ไทยดีที่สุด แต่ใหญ่และช้าบน CPU — ควรใช้กับ GPU"),
    ];

    /// <summary>
    /// Marks the "type your own URL" choice. A catalog entry that has moved or
    /// been withdrawn upstream must never be a dead end.
    /// </summary>
    public const string CustomId = "custom";

    /// <summary>Builds an entry from a URL the user typed.</summary>
    public static ModelEntry? TryCreateCustom(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        string fileName = Path.GetFileName(uri.LocalPath);

        return new ModelEntry(
            CustomId,
            string.IsNullOrWhiteSpace(fileName) ? "โมเดลที่ระบุเอง" : fileName,
            uri,
            ApproximateBytes: 0,
            "ตรวจสอบเงื่อนไขที่ต้นทางเอง",
            CommercialUseAllowed: true,
            "URL ที่ระบุเอง");
    }

    public static ModelEntry Default => Entries[0];

    public static ModelEntry? FindById(string? id) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
}
