using System.Net.Http;
using System.Text.Json;

namespace TLOverlay.Core.Setup;

public sealed record LlamaAsset(string Name, Uri DownloadUrl, long SizeBytes);

/// <summary>
/// Picks the right llama.cpp release archive for this machine.
///
/// Split out from the downloader so it can be tested against a captured release
/// payload: llama.cpp has renamed its Windows assets more than once, and a
/// resolver that breaks silently would be very hard to diagnose from a user's
/// bug report.
/// </summary>
public sealed class LlamaReleaseResolver
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";

    private readonly HttpClient _client;

    public LlamaReleaseResolver(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<LlamaAsset> ResolveLatestAsync(
        LlamaBackend backend,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);

        // GitHub rejects API requests with no user agent.
        request.Headers.UserAgent.ParseAdd("TLOverlay-setup");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException(
                $"ขอข้อมูล release ของ llama.cpp ไม่สำเร็จ ({(int)response.StatusCode})");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Select(document.RootElement, backend);
    }

    /// <summary>
    /// Chooses the Windows x64 archive for the requested backend.
    /// </summary>
    /// <exception cref="ModelDownloadException">
    /// Nothing matched. The message lists what the release actually contains,
    /// because that is the only way to tell a renamed asset from a missing one.
    /// </exception>
    internal static LlamaAsset Select(JsonElement release, LlamaBackend backend)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new ModelDownloadException("ข้อมูล release ของ llama.cpp ไม่มีรายการไฟล์");
        }

        var candidates = new List<LlamaAsset>();

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString()
                : null;

            string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                ? urlElement.GetString()
                : null;

            if (name is null || url is null)
            {
                continue;
            }

            long size = asset.TryGetProperty("size", out JsonElement sizeElement)
                && sizeElement.TryGetInt64(out long parsed)
                ? parsed
                : 0;

            candidates.Add(new LlamaAsset(name, new Uri(url), size));
        }

        LlamaAsset? match = candidates.FirstOrDefault(asset => Matches(asset.Name, backend));

        if (match is not null)
        {
            return match;
        }

        string available = candidates.Count == 0
            ? "(ไม่มีเลย)"
            : string.Join(", ", candidates.Select(static a => a.Name));

        throw new ModelDownloadException(
            $"ไม่พบไฟล์ llama.cpp สำหรับ Windows x64 ({backend}) — ไฟล์ที่มีใน release นี้: {available}");
    }

    private static bool Matches(string name, LlamaBackend backend)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Contains(name, "bin-win") || !Contains(name, "x64"))
        {
            return false;
        }

        return backend switch
        {
            LlamaBackend.Cuda => Contains(name, "cuda"),

            // The CPU archive must not be a GPU one: "cpu" is not always in the
            // name, but vulkan/hip/sycl builds all carry their backend.
            LlamaBackend.Cpu => Contains(name, "cpu")
                || !(Contains(name, "cuda") || Contains(name, "vulkan")
                    || Contains(name, "hip") || Contains(name, "sycl")
                    || Contains(name, "arm64")),

            _ => false,
        };
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
