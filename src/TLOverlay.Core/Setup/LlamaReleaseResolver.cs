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
    // Deliberately the list, not /releases/latest. GitHub defines "latest" as the
    // newest release that is not a prerelease, and llama.cpp publishes its actual
    // Windows builds as prereleases while keeping a marker release whose only
    // asset is nightly-tag.txt. Asking for "latest" reliably returned that marker
    // and no binaries at all.
    private const string ReleaseListUrl =
        "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30";

    private readonly HttpClient _client;

    public LlamaReleaseResolver(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<LlamaAsset> ResolveLatestAsync(
        LlamaBackend backend,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseListUrl);

        // GitHub rejects API requests with no user agent.
        request.Headers.UserAgent.ParseAdd("TLOverlay-setup");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // GitHub allows 60 unauthenticated API calls an hour per address, and
            // a shared or carrier-grade NAT address can exhaust that without the
            // user doing anything unusual. Worth naming, because the fix is to
            // wait rather than to retry immediately.
            string detail = (int)response.StatusCode is 403 or 429
                ? "ถูกจำกัดจำนวนคำขอชั่วคราว ลองใหม่ในอีกสักครู่ หรือดาวน์โหลดเองจากลิงก์ด้านล่าง"
                : response.ReasonPhrase ?? string.Empty;

            throw new ModelDownloadException(
                $"ขอข้อมูล release ของ llama.cpp ไม่สำเร็จ ({(int)response.StatusCode}) {detail}".TrimEnd());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return SelectFromReleases(document.RootElement, backend);
    }

    /// <summary>
    /// Walks releases newest-first and returns the first Windows x64 archive it
    /// finds.
    ///
    /// Picking by "has the file I need" rather than by position is what makes
    /// this survive upstream's habits: a marker release carrying only
    /// nightly-tag.txt, a release whose assets are still uploading, or a change
    /// in which builds get flagged as prereleases.
    /// </summary>
    internal static LlamaAsset SelectFromReleases(JsonElement releases, LlamaBackend backend)
    {
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
        {
            throw new ModelDownloadException("ไม่พบ release ของ llama.cpp เลย");
        }

        // Kept for the error message: the newest release that actually shipped
        // files is the useful thing to show when nothing matches.
        string? reportedTag = null;
        string? reportedAssets = null;
        int scanned = 0;

        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out JsonElement draft)
                && draft.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            scanned++;

            var candidates = ReadAssets(release);
            if (candidates.Count == 0)
            {
                continue;
            }

            LlamaAsset? match = candidates.FirstOrDefault(asset => Matches(asset.Name, backend));
            if (match is not null)
            {
                return match;
            }

            reportedTag ??= release.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() : null;
            reportedAssets ??= string.Join(", ", candidates.Select(static a => a.Name));
        }

        throw new ModelDownloadException(
            $"ไม่พบไฟล์ llama.cpp สำหรับ Windows x64 ({backend}) ใน {scanned} release ล่าสุด — " +
            $"release {reportedTag ?? "(ไม่ทราบ)"} มีไฟล์: {reportedAssets ?? "(ไม่มีเลย)"}");
    }

    /// <summary>
    /// Chooses the Windows x64 archive from a single release.
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

        var candidates = ReadAssets(release);

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

    private static List<LlamaAsset> ReadAssets(JsonElement release)
    {
        var candidates = new List<LlamaAsset>();

        if (!release.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

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

        return candidates;
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
