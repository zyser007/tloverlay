using System.IO;
using TLOverlay.Core.Profiles;
using TLOverlay.Core.Translation;

namespace TLOverlay.App.Services;

/// <summary>
/// Where the offline model and server live, and how many layers to put on the
/// GPU. Persisted next to the profiles so the player sets it once.
/// </summary>
public sealed class TranslatorSettings
{
    public string? ExecutablePath { get; set; }

    public string? ModelPath { get; set; }

    public string ModelId { get; set; } = "typhoon2-4b-q4";

    /// <summary>
    /// Zero keeps the model on the CPU. That is the right default while a game
    /// owns the GPU: slower per line, but it cannot cost the player frames.
    /// </summary>
    public int GpuLayers { get; set; }

    public int Port { get; set; } = 8787;
}

/// <summary>Builds the translator stack: local model, wrapped in caches.</summary>
public static class TranslatorFactory
{
    public const string DefaultExecutableRelativePath = @"runtime\llama-server.exe";
    public const string DefaultModelRelativePath = @"models\translator.gguf";

    public static ITranslator Create(
        TranslatorSettings settings,
        GameProfile profile,
        string cacheDirectory,
        out SqliteTranslationCache persistentCache)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);

        var options = new LlamaServerOptions
        {
            ExecutablePath = settings.ExecutablePath ?? ResolveDefault(DefaultExecutableRelativePath),
            ModelPath = settings.ModelPath ?? ResolveDefault(DefaultModelRelativePath),
            ModelId = settings.ModelId,
            GpuLayers = settings.GpuLayers,
            Port = settings.Port,
        };

        var glossary = new GlossaryService(profile.Glossary.Select(static term =>
            new GlossaryEntry(term.Source, string.IsNullOrWhiteSpace(term.Target) ? null : term.Target)));

        persistentCache = new SqliteTranslationCache(Path.Combine(cacheDirectory, "translations.db"));

        var cache = new LayeredTranslationCache(new MemoryTranslationCache(), persistentCache);

        return new CachingTranslator(new LlamaSidecarTranslator(options, glossary), cache);
    }

    /// <summary>
    /// Looks beside the executable first, then walks up to the repository root so
    /// a developer running from bin/Debug finds the same runtime/ and models/
    /// directories that fetch-models.ps1 populates.
    /// </summary>
    public static string ResolveDefault(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, relativePath);
    }

    public static bool IsModelInstalled(TranslatorSettings settings) =>
        File.Exists(settings.ExecutablePath ?? ResolveDefault(DefaultExecutableRelativePath))
        && File.Exists(settings.ModelPath ?? ResolveDefault(DefaultModelRelativePath));
}
