using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace TLOverlay.Core.Setup;

/// <summary>Magic bytes a finished download must start with.</summary>
public enum FileSignature
{
    None,

    /// <summary>"GGUF" - a llama.cpp model file.</summary>
    Gguf,

    /// <summary>"PK\x03\x04" - a zip archive.</summary>
    Zip,

    /// <summary>"MZ" - a Windows executable.</summary>
    WindowsExecutable,
}

public sealed record DownloadProgress(long BytesCompleted, long? TotalBytes, double BytesPerSecond)
{
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesCompleted / TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Remaining
    {
        get
        {
            if (TotalBytes is not > 0 || BytesPerSecond <= 0)
            {
                return null;
            }

            double seconds = (TotalBytes.Value - BytesCompleted) / BytesPerSecond;

            // Guard the ends rather than pattern-match: a stalled transfer gives
            // an infinity here, and TimeSpan.FromSeconds throws on one.
            if (double.IsNaN(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            {
                return null;
            }

            return TimeSpan.FromSeconds(seconds);
        }
    }
}

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message)
        : base(message)
    {
    }

    public ModelDownloadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Downloads a large file with resume support.
///
/// Built for the one case that actually matters here: a two-gigabyte model over
/// a home connection, where the transfer will sometimes be interrupted and
/// starting again from zero is not acceptable.
/// </summary>
public sealed class ModelDownloader
{
    /// <summary>Suffix for the in-progress file.</summary>
    public const string PartialSuffix = ".partial";

    private const int BufferSize = 128 * 1024;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly HttpClient _client;

    public ModelDownloader(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static string PartialPathFor(string destinationPath) => destinationPath + PartialSuffix;

    /// <summary>
    /// Fetches <paramref name="source"/> to <paramref name="destinationPath"/>,
    /// resuming from a previous attempt when one is present.
    /// </summary>
    /// <exception cref="ModelDownloadException">
    /// The transfer failed, or the finished file did not start with the expected
    /// signature.
    /// </exception>
    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        FileSignature expected = FileSignature.None,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partialPath = PartialPathFor(destinationPath);
        long alreadyHave = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (alreadyHave > 0)
        {
            request.Headers.Range = new RangeHeaderValue(alreadyHave, null);
        }

        HttpResponseMessage response;
        try
        {
            // Headers-only completion, or the runtime buffers the whole file into
            // memory before we ever see a byte.
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelDownloadException($"เชื่อมต่อไม่สำเร็จ: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ModelDownloadException(
                    $"เซิร์ฟเวอร์ตอบกลับ {(int)response.StatusCode} ({response.ReasonPhrase}) สำหรับ {source}");
            }

            // A server that ignores Range answers 200 with the whole file. Appending
            // to what we already have would silently produce a corrupt result, so
            // start the file over instead.
            bool resuming = response.StatusCode == HttpStatusCode.PartialContent && alreadyHave > 0;
            long startOffset = resuming ? alreadyHave : 0;

            long? total = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength is { } length ? startOffset + length : null);

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                resuming ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);

            await CopyAsync(input, output, startOffset, total, progress, cancellationToken).ConfigureAwait(false);
        }

        if (expected != FileSignature.None && !await HasSignatureAsync(partialPath, expected).ConfigureAwait(false))
        {
            // Hugging Face answers an auth wall or a redirect with 200 and an HTML
            // body, which lands here as a .gguf full of markup. Deleting it matters:
            // keeping it would make every retry resume an HTML file forever.
            TryDelete(partialPath);

            throw new ModelDownloadException(
                "ไฟล์ที่โหลดมาไม่ใช่ไฟล์โมเดลที่ถูกต้อง (อาจโดนหน้าล็อกอินหรือ redirect) — ลองใหม่หรือโหลดเองแล้วใช้ปุ่มเลือกไฟล์");
        }

        File.Move(partialPath, destinationPath, overwrite: true);
    }

    private static async Task CopyAsync(
        Stream input,
        Stream output,
        long startOffset,
        long? total,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long completed = startOffset;

        var clock = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;
        long lastReportedBytes = startOffset;

        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            completed += read;

            TimeSpan now = clock.Elapsed;
            if (progress is not null && now - lastReport >= ProgressInterval)
            {
                double seconds = (now - lastReport).TotalSeconds;
                double speed = seconds > 0 ? (completed - lastReportedBytes) / seconds : 0;

                progress.Report(new DownloadProgress(completed, total, speed));

                lastReport = now;
                lastReportedBytes = completed;
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        double totalSeconds = clock.Elapsed.TotalSeconds;
        progress?.Report(new DownloadProgress(
            completed,
            total ?? completed,
            totalSeconds > 0 ? (completed - startOffset) / totalSeconds : 0));
    }

    /// <summary>
    /// Checks the first bytes of a file. Four bytes is enough to tell a real
    /// model from an error page, and costs nothing next to the download.
    /// </summary>
    public static async Task<bool> HasSignatureAsync(string path, FileSignature expected)
    {
        if (expected == FileSignature.None)
        {
            return true;
        }

        byte[] magic = expected switch
        {
            FileSignature.Gguf => "GGUF"u8.ToArray(),
            FileSignature.Zip => [0x50, 0x4B, 0x03, 0x04],
            FileSignature.WindowsExecutable => "MZ"u8.ToArray(),
            _ => [],
        };

        if (magic.Length == 0 || !File.Exists(path))
        {
            return false;
        }

        var head = new byte[magic.Length];

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (await stream.ReadAsync(head).ConfigureAwait(false) != magic.Length)
            {
                return false;
            }
        }

        return head.AsSpan().SequenceEqual(magic);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing useful to do; the next attempt overwrites it anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
