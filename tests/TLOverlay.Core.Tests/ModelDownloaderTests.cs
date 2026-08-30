using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class ModelDownloaderTests : IDisposable
{
    private static readonly Uri Source = new("https://example.invalid/model.gguf");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tloverlay-dl-{Guid.NewGuid():N}");

    private string Destination => Path.Combine(_directory, "translator.gguf");

    private string Partial => ModelDownloader.PartialPathFor(Destination);

    public ModelDownloaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A payload that starts with the GGUF magic bytes, like a real model.</summary>
    private static byte[] Model(int length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes("GGUF").CopyTo(bytes, 0);

        for (int i = 4; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static HttpResponseMessage Ok(byte[] payload, int chunkSize = 4096, Action<int>? afterChunk = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(payload, chunkSize, afterChunk)),
        };

        response.Content.Headers.ContentLength = payload.Length;
        return response;
    }

    private static HttpResponseMessage Partial206(byte[] whole, long from)
    {
        byte[] tail = whole[(int)from..];

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(new ChunkedStream(tail, 4096)),
        };

        response.Content.Headers.ContentLength = tail.Length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, whole.Length - 1, whole.Length);
        return response;
    }

    private static (ModelDownloader Downloader, StubHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        return (new ModelDownloader(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task CompletedDownloadLeavesOnlyTheFinalFile()
    {
        byte[] payload = Model(50_000);
        var (downloader, _) = Build(_ => Ok(payload));

        await downloader.DownloadAsync(Source, Destination, FileSignature.Gguf);

        Assert.True(File.Exists(Destination));
        Assert.False(File.Exists(Partial));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task ResumesFromAPartialFile()
    {
        byte[] payload = Model(50_000);
        const int Have = 20_000;

        await File.WriteAllBytesAsync(Partial, payload[..Have]);

        var (downloader, handler) = Build(_ => Partial206(payload, Have));

        await downloader.DownloadAsync(Source, Destination, FileSignature.Gguf);

        // The whole point: the request asked to continue, and the file on disk is
        // the two halves joined in the right order.
        Assert.Equal(Have, handler.Requests[0].Headers.Range?.Ranges.Single().From);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsInsteadOfAppending()
    {
        byte[] payload = Model(30_000);

        await File.WriteAllBytesAsync(Partial, payload[..10_000]);

        // Answering 200 to a Range request means "here is the whole thing".
        // Appending it would produce a 40,000-byte corrupt file.
        var (downloader, _) = Build(_ => Ok(payload));

        await downloader.DownloadAsync(Source, Destination, FileSignature.Gguf);

        Assert.Equal(payload.Length, new FileInfo(Destination).Length);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task RejectsAnErrorPageDisguisedAsAModel()
    {
        // Hugging Face answers an auth wall with 200 and HTML.
        byte[] html = Encoding.UTF8.GetBytes("<!doctype html><html><body>Sign in</body></html>");
        var (downloader, _) = Build(_ => Ok(html));

        var error = await Assert.ThrowsAsync<ModelDownloadException>(
            () => downloader.DownloadAsync(Source, Destination, FileSignature.Gguf));

        Assert.False(File.Exists(Destination));

        // The bad partial must be gone, or every retry would resume the HTML.
        Assert.False(File.Exists(Partial));
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public async Task ValidationFailureLeavesAnExistingModelIntact()
    {
        await File.WriteAllBytesAsync(Destination, Model(1000));

        byte[] html = Encoding.UTF8.GetBytes("<html>nope</html>");
        var (downloader, _) = Build(_ => Ok(html));

        await Assert.ThrowsAsync<ModelDownloadException>(
            () => downloader.DownloadAsync(Source, Destination, FileSignature.Gguf));

        Assert.Equal(1000, new FileInfo(Destination).Length);
    }

    [Fact]
    public async Task CancellationKeepsThePartialFileSoTheRetryResumes()
    {
        byte[] payload = Model(200_000);
        using var cancellation = new CancellationTokenSource();

        var (downloader, _) = Build(_ => Ok(payload, chunkSize: 8192, afterChunk: read =>
        {
            if (read >= 32_768)
            {
                cancellation.Cancel();
            }
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(Source, Destination, FileSignature.Gguf, null, cancellation.Token));

        Assert.False(File.Exists(Destination));
        Assert.True(File.Exists(Partial));
        Assert.True(new FileInfo(Partial).Length > 0);
    }

    [Fact]
    public async Task ReportsIncreasingProgressWithATotal()
    {
        byte[] payload = Model(400_000);
        var reports = new List<DownloadProgress>();

        var (downloader, _) = Build(_ => Ok(payload, chunkSize: 4096));

        await downloader.DownloadAsync(
            Source,
            Destination,
            FileSignature.Gguf,
            new Progress<DownloadProgress>(reports.Add));

        Assert.NotEmpty(reports);

        var last = reports[^1];
        Assert.Equal(payload.Length, last.BytesCompleted);
        Assert.Equal(payload.Length, last.TotalBytes);
        Assert.Equal(1.0, last.Fraction);
    }

    [Fact]
    public async Task HttpErrorStatusIsReportedWithItsCode()
    {
        var (downloader, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty),
        });

        var error = await Assert.ThrowsAsync<ModelDownloadException>(
            () => downloader.DownloadAsync(Source, Destination));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignatureCheckDistinguishesModelsFromArchives()
    {
        string gguf = Path.Combine(_directory, "a.bin");
        string zip = Path.Combine(_directory, "b.bin");

        await File.WriteAllBytesAsync(gguf, Model(16));
        await File.WriteAllBytesAsync(zip, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);

        Assert.True(await ModelDownloader.HasSignatureAsync(gguf, FileSignature.Gguf));
        Assert.False(await ModelDownloader.HasSignatureAsync(gguf, FileSignature.Zip));
        Assert.True(await ModelDownloader.HasSignatureAsync(zip, FileSignature.Zip));
        Assert.False(await ModelDownloader.HasSignatureAsync(zip, FileSignature.Gguf));
    }

    [Fact]
    public async Task ATruncatedFileFailsTheSignatureCheck()
    {
        string tiny = Path.Combine(_directory, "tiny.bin");
        await File.WriteAllBytesAsync(tiny, [0x47, 0x47]);

        Assert.False(await ModelDownloader.HasSignatureAsync(tiny, FileSignature.Gguf));
    }
}
