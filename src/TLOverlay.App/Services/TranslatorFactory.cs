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
    /// <summary>
    /// Which engine translates. Local by default: it is the only one that costs
    /// nothing, needs no account and sends nothing anywhere.
    /// </summary>
    public TranslationBackend Backend { get; set; } = TranslationBackend.Local;

    public string? ExecutablePath { get; set; }

    public string? ModelPath { get; set; }

    public string ModelId { get; set; } = "gemma3-4b-q4km";

    /// <summary>
    /// Zero keeps the model on the CPU. That is the right default while a game
    /// owns the GPU: slower per line, but it cannot cost the player frames.
    /// </summary>
    public int GpuLayers { get; set; }

    public int Port { get; set; } = 8787;

    /// <summary>
    /// Cloud Translation API key, DPAPI-encrypted. Optional: without one, the
    /// Google backend uses the free endpoint instead.
    /// </summary>
    public string? GoogleApiKeyProtected { get; set; }

    /// <summary>OpenAI-compatible API key, DPAPI-encrypted.</summary>
    public string? OpenAiApiKeyProtected { get; set; }

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Any OpenAI-compatible endpoint. The wire format is not OpenAI's alone, and
    /// pointing this at OpenRouter, Groq, or a model on another PC in the house
    /// costs nothing to allow.
    /// </summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1/";
}

/// <summary>Builds the translator stack: the chosen engine, wrapped in caches.</summary>
public static class TranslatorFactory
{
    public const string DefaultExecutableRelativePath = @"runtime\llama-server.exe";
    public const string DefaultModelRelativePath = @"models\translator.gguf";

    public static ITranslator Create(
        TranslatorSettings settings,
        GameProfile profile,
        string cacheDirectory,
        string? installRoot,
        out SqliteTranslationCache persistentCache)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);

        var glossary = new GlossaryService(profile.Glossary.Select(static term =>
            new GlossaryEntry(term.Source, string.IsNullOrWhiteSpace(term.Target) ? null : term.Target)));

        persistentCache = new SqliteTranslationCache(Path.Combine(cacheDirectory, "translations.db"));

        var cache = new LayeredTranslationCache(new MemoryTranslationCache(), persistentCache);

        // The cache is keyed partly on the translator's Id, so switching engines
        // does not serve one engine's lines from another's.
        return new CachingTranslator(CreateBackend(settings, glossary, installRoot), cache);
    }

    private static ITranslator CreateBackend(
        TranslatorSettings settings,
        GlossaryService glossary,
        string? installRoot) =>
        settings.Backend switch
        {
            TranslationBackend.Google => new GoogleTranslateTranslator(
                SecretStore.Unprotect(settings.GoogleApiKeyProtected),
                glossary),

            TranslationBackend.OpenAi => new OpenAiTranslator(
                new OpenAiOptions
                {
                    ApiKey = SecretStore.Unprotect(settings.OpenAiApiKeyProtected) ?? string.Empty,
                    Model = settings.OpenAiModel,
                    BaseAddress = ParseBaseAddress(settings.OpenAiBaseUrl),
                },
                glossary),

            _ => new LlamaSidecarTranslator(
                new LlamaServerOptions
                {
                    ExecutablePath = settings.ExecutablePath ?? ResolveDefault(DefaultExecutableRelativePath, installRoot),
                    ModelPath = settings.ModelPath ?? ResolveDefault(DefaultModelRelativePath, installRoot),
                    ModelId = settings.ModelId,
                    GpuLayers = settings.GpuLayers,
                    Port = settings.Port,
                },
                glossary),
        };

    /// <summary>
    /// A base address that a relative "chat/completions" can be appended to. The
    /// trailing slash is load-bearing: without it, Uri drops the last path
    /// segment and a request meant for /v1/chat/completions goes to
    /// /chat/completions.
    /// </summary>
    internal static Uri ParseBaseAddress(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value)
            ? "https://api.openai.com/v1/"
            : value.Trim();

        if (!text.EndsWith('/'))
        {
            text += "/";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)
            ? parsed
            : new Uri("https://api.openai.com/v1/");
    }

    /// <summary>
    /// Where a given piece of the runtime lives.
    ///
    /// An existing file beside the executable wins, so a developer running from
    /// bin/Debug and a portable install both keep working. When nothing is found,
    /// the answer is the per-user data directory - never beside the executable,
    /// because that is where downloads would be lost (launched from an archive)
    /// or refused (installed under Program Files).
    /// </summary>
    public static string ResolveDefault(string relativePath, string? installRoot = null)
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

        return Path.Combine(installRoot ?? AppPaths.DataDirectory, relativePath);
    }

    public static bool IsModelInstalled(TranslatorSettings settings, string? installRoot = null) =>
        File.Exists(settings.ExecutablePath ?? ResolveDefault(DefaultExecutableRelativePath, installRoot))
        && File.Exists(settings.ModelPath ?? ResolveDefault(DefaultModelRelativePath, installRoot));

    /// <summary>
    /// Whether the chosen engine has everything it needs to translate.
    ///
    /// Not the same question as "is the model installed" any more: a player on a
    /// cloud backend has no model and does not need one, and must not be sent
    /// back to the setup screen on every launch for a file they deliberately did
    /// not download.
    /// </summary>
    public static bool IsReadyToTranslate(TranslatorSettings settings, string? installRoot = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Backend switch
        {
            // The free endpoint needs no key, so this backend is always ready.
            TranslationBackend.Google => true,
            TranslationBackend.OpenAi => !string.IsNullOrWhiteSpace(settings.OpenAiApiKeyProtected),
            _ => IsModelInstalled(settings, installRoot),
        };
    }
}
