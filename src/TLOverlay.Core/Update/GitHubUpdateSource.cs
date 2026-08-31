using System.Net.Http;
using System.Text.Json;

namespace TLOverlay.Core.Update;

/// <summary>Something went wrong asking GitHub what the newest release is.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message)
        : base(message)
    {
    }

    public UpdateCheckException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Asks GitHub whether a newer build exists.
///
/// Deliberately the release list rather than /releases/latest, for the reason
/// the model downloader learned the hard way: "latest" means the newest release
/// that is not a prerelease, which is a different question from "the newest
/// release that actually carries the file I need".
/// </summary>
public sealed class GitHubUpdateSource
{
    /// <summary>
    /// The asset the updater replaces the running program with. Releases also
    /// carry a zip for people downloading by hand; this is the bare executable,
    /// so an update is one file and no unpacking.
    /// </summary>
    public const string ExecutableAssetName = "TLOverlay.exe";

    /// <summary>
    /// Checksums for the release's assets, one "&lt;hash&gt; *&lt;name&gt;" line each.
    ///
    /// Required, not optional. The executable is unsigned, so this file is the
    /// only thing standing between the updater and running whatever arrived over
    /// the wire - a release without it is treated as not updatable rather than
    /// installed unverified.
    /// </summary>
    public const string ChecksumAssetName = "SHA256SUMS.txt";

    private readonly HttpClient _client;
    private readonly Uri _releaseListUrl;

    public GitHubUpdateSource(HttpClient client, string owner = "zyser007", string repository = "tloverlay")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        _releaseListUrl = new Uri(
            $"https://api.github.com/repos/{owner}/{repository}/releases?per_page=20");
    }

    /// <summary>
    /// Returns the newest release above <paramref name="current"/>, or null when
    /// there is nothing newer to install.
    /// </summary>
    public async Task<UpdateManifest?> CheckAsync(
        Version current,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        using var request = new HttpRequestMessage(HttpMethod.Get, _releaseListUrl);

        // GitHub rejects API requests with no user agent.
        request.Headers.UserAgent.ParseAdd("TLOverlay-updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await _client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Sixty unauthenticated calls an hour per address, which a shared or
            // carrier-grade NAT address can exhaust without the player doing
            // anything unusual. The fix is to wait, so say that rather than
            // inviting a retry.
            string detail = (int)response.StatusCode is 403 or 429
                ? "ถูกจำกัดจำนวนคำขอชั่วคราว ลองใหม่ภายหลัง"
                : response.ReasonPhrase ?? string.Empty;

            throw new UpdateCheckException(
                $"ตรวจสอบเวอร์ชันใหม่ไม่สำเร็จ ({(int)response.StatusCode}) {detail}".TrimEnd());
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return SelectFromReleases(document.RootElement, current, includePrerelease);
    }

    /// <summary>
    /// Picks the newest usable release from a release list.
    ///
    /// Split out so it can be tested against captured payloads: everything that
    /// can go wrong here - a draft, a release whose assets are still uploading, a
    /// release with no checksums - is a shape of JSON rather than a network
    /// condition.
    /// </summary>
    internal static UpdateManifest? SelectFromReleases(
        JsonElement releases,
        Version current,
        bool includePrerelease)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        UpdateManifest? best = null;
        Version running = AppVersion.Normalize(current);

        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (Read(release, "draft")?.GetBoolean() == true)
            {
                continue;
            }

            bool prerelease = Read(release, "prerelease")?.GetBoolean() == true;

            if (prerelease && !includePrerelease)
            {
                continue;
            }

            string tag = Read(release, "tag_name")?.GetString() ?? string.Empty;
            Version version = AppVersion.Parse(tag);

            if (version <= running || (best is not null && version <= best.Version))
            {
                continue;
            }

            if (!TryFindAsset(release, ExecutableAssetName, out Uri? executable, out long size))
            {
                // A release that carries only a zip, or one still uploading. Not
                // a failure - just not one this updater can install.
                continue;
            }

            if (!TryFindAsset(release, ChecksumAssetName, out Uri? checksums, out _))
            {
                continue;
            }

            best = new UpdateManifest(
                version,
                tag,
                executable!,
                size,
                // The installer fetches and parses this. Doing it here would put
                // a second request inside what is otherwise a pure function over
                // the payload, and make this untestable without a network.
                ChecksumsUrl: checksums!,
                ReleasePage: ReleasePageOf(release, tag),
                Notes: Read(release, "body")?.GetString() ?? string.Empty,
                IsPrerelease: prerelease);
        }

        return best;
    }

    private static Uri ReleasePageOf(JsonElement release, string tag)
    {
        string? url = Read(release, "html_url")?.GetString();

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? page)
            ? page
            : new Uri($"https://github.com/zyser007/tloverlay/releases/tag/{tag}");
    }

    private static bool TryFindAsset(JsonElement release, string name, out Uri? url, out long size)
    {
        url = null;
        size = 0;

        if (Read(release, "assets") is not { ValueKind: JsonValueKind.Array } assets)
        {
            return false;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!string.Equals(Read(asset, "name")?.GetString(), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(Read(asset, "browser_download_url")?.GetString(), UriKind.Absolute, out Uri? parsed))
            {
                continue;
            }

            url = parsed;

            if (Read(asset, "size") is { } declared && declared.TryGetInt64(out long bytes))
            {
                size = bytes;
            }

            return true;
        }

        return false;
    }

    private static JsonElement? Read(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
            ? value
            : null;
}
