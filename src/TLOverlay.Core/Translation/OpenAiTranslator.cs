using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TLOverlay.Core.Translation;

/// <summary>Where to send chat-completion requests, and as whom.</summary>
public sealed class OpenAiOptions
{
    /// <summary>
    /// Base address of an OpenAI-compatible API. Configurable because the wire
    /// format is not OpenAI's alone - the same settings point at OpenRouter,
    /// Groq, Azure-hosted deployments or a model running on another machine on
    /// the LAN, which is a real answer for a household with one capable PC.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("https://api.openai.com/v1/");

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Left as free text rather than a fixed list. Model names change faster
    /// than this app ships, and a dropdown that has gone stale is worse than a
    /// field: it stops the player using a model that exists.
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Translates through a hosted OpenAI-compatible chat model.
///
/// Same prompt and same output cleanup as the local model, so switching between
/// them changes the speed and the bill, not the voice.
/// </summary>
public sealed class OpenAiTranslator : ITranslator
{
    private readonly OpenAiOptions _options;
    private readonly GlossaryService _glossary;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    public OpenAiTranslator(
        OpenAiOptions options,
        GlossaryService? glossary = null,
        HttpClient? client = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _glossary = glossary ?? new GlossaryService();
        _ownsClient = client is null;
        _client = client ?? new HttpClient();

        if (_ownsClient)
        {
            _client.Timeout = _options.RequestTimeout;
        }
    }

    public string Id => $"openai:{_options.Model}";

    /// <summary>
    /// True when there is a key to send. Nothing is called to find out: a probe
    /// request would be a billable token spend on every session start, and a bad
    /// key produces a perfectly clear 401 on the first real line.
    /// </summary>
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(_options.ApiKey));

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new CloudTranslationException("ยังไม่ได้ใส่ API key");
        }

        var protectedText = _glossary.Protect(text);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_options.BaseAddress, "chat/completions"))
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                messages = ChatTranslationPrompt.BuildMessages(protectedText.Text),

                // Near-greedy, as with the local model: the same line should
                // translate the same way every time it appears, or the cache and
                // the player both suffer.
                temperature = 0.1,
                max_tokens = 512,
                stream = false,
            }),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw Failure(response.StatusCode, body);
        }

        string raw = ExtractContent(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return GlossaryService.Restore(ChatTranslationPrompt.CleanModelOutput(raw), protectedText);
    }

    internal static string ExtractContent(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (JsonException ex)
        {
            throw new CloudTranslationException("เซิร์ฟเวอร์ตอบกลับมาในรูปแบบที่อ่านไม่ได้", ex);
        }
    }

    /// <summary>
    /// Turns a status code into something a player can act on. The provider's own
    /// message is appended when there is one, because for a wrong model name or a
    /// disabled account it is the only text that says which.
    /// </summary>
    internal static CloudTranslationException Failure(HttpStatusCode status, string body)
    {
        string detail = (int)status switch
        {
            401 => "API key ไม่ถูกต้อง หรือถูกยกเลิกแล้ว",
            403 => "คีย์นี้ไม่มีสิทธิ์ใช้โมเดลที่เลือก",
            404 => "ไม่พบโมเดลที่ระบุ — ตรวจสอบชื่อโมเดลอีกครั้ง",
            429 => "เรียกถี่เกินไป หรือเครดิตหมด",
            >= 500 => "ฝั่งผู้ให้บริการขัดข้องชั่วคราว",
            _ => string.Empty,
        };

        string? provider = ReadErrorMessage(body);
        string code = ((int)status).ToString(CultureInfo.InvariantCulture);

        return new CloudTranslationException(
            $"แปลไม่สำเร็จ ({code}) {detail}{(provider is null ? string.Empty : $" — {provider}")}".TrimEnd());
    }

    private static string? ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy in front of the API. The status
            // code already said what matters.
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_ownsClient)
            {
                _client.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}
