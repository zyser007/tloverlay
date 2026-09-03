using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TLOverlay.Core.Translation;

/// <summary>A hosted translator refused or could not answer the request.</summary>
public sealed class CloudTranslationException : Exception
{
    public CloudTranslationException(string message)
        : base(message)
    {
    }

    public CloudTranslationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Translates through Google Translate, for machines that cannot run a local
/// model at all.
///
/// Two endpoints behind one class, because they answer the same question with
/// different paperwork. With an API key it uses Cloud Translation v2, which is
/// documented, billed, and stable. Without one it uses the endpoint the Google
/// Translate web page itself calls, which needs no account and is what makes
/// this usable on a machine whose owner has neither a spare 2 GB of RAM nor a
/// credit card - but it is undocumented, rate-limited by address, and Google may
/// change or withdraw it without notice. The app says so where the choice is
/// made, rather than letting a player discover it mid-game.
/// </summary>
public sealed class GoogleTranslateTranslator : ITranslator
{
    private const string FreeEndpoint = "https://translate.googleapis.com/translate_a/single";
    private const string CloudEndpoint = "https://translation.googleapis.com/language/translate/v2";

    private readonly HttpClient _client;
    private readonly GlossaryService _glossary;
    private readonly string? _apiKey;
    private readonly bool _ownsClient;
    private bool _disposed;

    public GoogleTranslateTranslator(
        string? apiKey = null,
        GlossaryService? glossary = null,
        HttpClient? client = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _glossary = glossary ?? new GlossaryService();
        _ownsClient = client is null;
        _client = client ?? new HttpClient();

        if (_ownsClient)
        {
            _client.Timeout = TimeSpan.FromSeconds(20);
        }
    }

    /// <summary>
    /// Part of the cache key, and the two endpoints are different products with
    /// different output, so they must not share cached lines.
    /// </summary>
    public string Id => _apiKey is null ? "google:free" : "google:v2";

    public string SourceLanguage { get; init; } = "en";

    public string TargetLanguage { get; init; } = "th";

    /// <summary>
    /// Nothing to start up. Reachability is not probed here on purpose: a check
    /// that costs a request would double the cost of every session start, and
    /// the first real translation reports the same failure just as clearly.
    /// </summary>
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var protectedText = _glossary.Protect(text);

        string translated = _apiKey is null
            ? await TranslateFreeAsync(protectedText.Text, cancellationToken).ConfigureAwait(false)
            : await TranslateCloudAsync(protectedText.Text, cancellationToken).ConfigureAwait(false);

        return GlossaryService.Restore(translated.Trim(), protectedText);
    }

    private async Task<string> TranslateFreeAsync(string text, CancellationToken cancellationToken)
    {
        var url = new StringBuilder(FreeEndpoint)
            .Append("?client=gtx&dt=t")
            .Append("&sl=").Append(Uri.EscapeDataString(SourceLanguage))
            .Append("&tl=").Append(Uri.EscapeDataString(TargetLanguage))
            .Append("&q=").Append(Uri.EscapeDataString(text))
            .ToString();

        using HttpResponseMessage response = await _client
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, keyed: false);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseFreeResponse(body);
    }

    private async Task<string> TranslateCloudAsync(string text, CancellationToken cancellationToken)
    {
        var payload = new
        {
            q = text,
            source = SourceLanguage,
            target = TargetLanguage,
            format = "text",
        };

        string url = $"{CloudEndpoint}?key={Uri.EscapeDataString(_apiKey!)}";

        using HttpResponseMessage response = await _client
            .PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, keyed: true);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseCloudResponse(body);
    }

    /// <summary>
    /// Reads the free endpoint's answer: nested arrays, where the first element
    /// holds one entry per sentence and each entry's first element is the
    /// translated text. Long dialogue comes back split, so the pieces are joined
    /// rather than only the first one taken.
    /// </summary>
    internal static string ParseFreeResponse(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            JsonElement sentences = document.RootElement[0];

            if (sentences.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach (JsonElement sentence in sentences.EnumerateArray())
            {
                if (sentence.ValueKind == JsonValueKind.Array
                    && sentence.GetArrayLength() > 0
                    && sentence[0].ValueKind == JsonValueKind.String)
                {
                    builder.Append(sentence[0].GetString());
                }
            }

            return builder.ToString();
        }
        catch (JsonException ex)
        {
            // The unofficial endpoint answers with an HTML challenge page when it
            // decides an address has asked too often.
            throw new CloudTranslationException(
                "Google ตอบกลับมาในรูปแบบที่อ่านไม่ได้ — มักเกิดจากการเรียกถี่เกินไป ลองใหม่ภายหลัง หรือใส่ API key",
                ex);
        }
    }

    /// <summary>
    /// Reads Cloud Translation v2: data.translations[].translatedText, which is
    /// HTML-escaped even when format is "text" - an apostrophe comes back as
    /// &amp;#39; and would be shown that way over the game.
    /// </summary>
    internal static string ParseCloudResponse(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("translations", out JsonElement translations)
                || translations.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach (JsonElement translation in translations.EnumerateArray())
            {
                if (translation.TryGetProperty("translatedText", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    builder.Append(WebUtility.HtmlDecode(value.GetString()));
                }
            }

            return builder.ToString();
        }
        catch (JsonException ex)
        {
            throw new CloudTranslationException("Google ตอบกลับมาในรูปแบบที่อ่านไม่ได้", ex);
        }
    }

    private static CloudTranslationException Failure(HttpStatusCode status, bool keyed)
    {
        string detail = (int)status switch
        {
            400 or 403 when keyed => "API key ไม่ถูกต้อง หรือยังไม่ได้เปิดใช้ Cloud Translation API",
            429 => keyed
                ? "เกินโควตาของ API key แล้ว"
                : "ถูกจำกัดจำนวนคำขอชั่วคราว (บริการฟรีจำกัดตามหมายเลข IP) — พักสักครู่ หรือใส่ API key",
            >= 500 => "ฝั่ง Google ขัดข้องชั่วคราว",
            _ => string.Empty,
        };

        string code = ((int)status).ToString(CultureInfo.InvariantCulture);

        return new CloudTranslationException(
            $"แปลผ่าน Google ไม่สำเร็จ ({code}) {detail}".TrimEnd());
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
