using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TLOverlay.Core.Translation;

/// <summary>
/// Translates through a llama.cpp server running on loopback.
///
/// Chosen over an in-process seq2seq model for the first cut because it is a
/// few hundred lines less code and a Thai-tuned instruct model produces markedly
/// more natural game dialogue than a general MT model of comparable size. It is
/// still fully offline: the server is a bundled binary bound to 127.0.0.1.
/// </summary>
public sealed class LlamaSidecarTranslator : ITranslator, IBatchTranslator
{
    private readonly LlamaServerOptions _options;
    private readonly LlamaServerProcess _server;
    private readonly HttpClient _client;
    private readonly GlossaryService _glossary;
    private readonly ILogger _logger;
    private readonly bool _ownsClient;
    private bool _disposed;

    public LlamaSidecarTranslator(
        LlamaServerOptions options,
        GlossaryService? glossary = null,
        HttpClient? client = null,
        ILogger<LlamaSidecarTranslator>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _glossary = glossary ?? new GlossaryService();
        _logger = logger ?? NullLogger<LlamaSidecarTranslator>.Instance;
        _ownsClient = client is null;
        _client = client ?? new HttpClient();
        _client.Timeout = _options.RequestTimeout;
        _server = new LlamaServerProcess(_options);
    }

    public string Id => $"llama:{_options.ModelId}";

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
        _server.EnsureStartedAsync(_client, cancellationToken);

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Local translation server is not available.");
        }

        var protectedText = _glossary.Protect(text);

        string raw = await AskAsync(
            ChatTranslationPrompt.BuildMessages(protectedText.Text),
            maxTokens: 512,
            cancellationToken).ConfigureAwait(false);

        return GlossaryService.Restore(CleanModelOutput(raw), protectedText);
    }

    /// <summary>
    /// Translates a screen's worth of lines in one request.
    ///
    /// Worth more here than with a hosted model, and for a different reason:
    /// nothing is billed, but every request re-processes the prompt, and on a CPU
    /// that is most of the wait. One request for forty lines is one prompt.
    /// </summary>
    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lines);

        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Local translation server is not available.");
        }

        return await ChatBatch.RunAsync(
            lines,
            _glossary,
            (chunk, token) => AskAsync(
                ChatTranslationPrompt.BuildBatchMessages(chunk),
                ChatTranslationPrompt.MaxTokensFor(chunk.Count),
                token),
            (line, token) => TranslateAsync(line, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One request, one raw answer. Shared by the single and batch paths.</summary>
    private async Task<string> AskAsync(
        List<object> messages,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _options.ModelId,
            messages,

            // Near-greedy: we want the same line to translate the same way every
            // time it appears, otherwise the cache and the player both suffer.
            temperature = 0.1,
            top_p = 0.9,
            max_tokens = maxTokens,
            stream = false,
        };

        using var response = await _client
            .PostAsJsonAsync(new Uri(_options.BaseAddress, "v1/chat/completions"), payload, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return ExtractContent(document);
    }

    private static string ExtractContent(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    internal static string CleanModelOutput(string raw) => ChatTranslationPrompt.CleanModelOutput(raw);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _server.DisposeAsync().ConfigureAwait(false);

        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
