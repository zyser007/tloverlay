namespace TLOverlay.Core.Translation;

/// <summary>
/// How to launch and talk to the bundled llama.cpp server.
/// </summary>
public sealed class LlamaServerOptions
{
    /// <summary>Full path to llama-server.exe.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Full path to the GGUF model file.</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Loopback port. Nothing here ever leaves the machine - the server is bound
    /// to 127.0.0.1 and the app stays usable with no network at all.
    /// </summary>
    public int Port { get; set; } = 8787;

    /// <summary>
    /// Layers to offload to the GPU. 0 keeps the model entirely on CPU, which is
    /// the right default while a game owns the GPU; raise it when there is spare
    /// VRAM and you want sub-second translations.
    /// </summary>
    public int GpuLayers { get; set; }

    /// <summary>Context size. Game lines are short; a small window loads faster.</summary>
    public int ContextSize { get; set; } = 2048;

    /// <summary>How long to wait for the model to finish loading.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Per-request ceiling. Beyond this we drop the line rather than stall the overlay.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Short label for the model, used in the cache key.</summary>
    public string ModelId { get; set; } = "local-gguf";

    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
}
