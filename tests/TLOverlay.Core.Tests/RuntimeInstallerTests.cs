using System.IO.Compression;
using System.Text;
using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class RuntimeInstallerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tloverlay-rt-{Guid.NewGuid():N}");

    public RuntimeInstallerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string BuildArchive(params string[] entryNames)
    {
        string path = Path.Combine(_directory, "release.zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (string name in entryNames)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.ASCII);
            writer.Write(name);
        }

        return path;
    }

    [Fact]
    public void FlattensTheNestedReleaseLayout()
    {
        // Release archives nest the binaries under a build-specific folder whose
        // name changes every release, so the app cannot depend on it.
        string archive = BuildArchive(
            "llama-b7421-bin-win-cpu-x64/llama-server.exe",
            "llama-b7421-bin-win-cpu-x64/ggml.dll",
            "llama-b7421-bin-win-cpu-x64/llama.dll");

        string runtime = Path.Combine(_directory, "runtime");

        string server = RuntimeInstaller.InstallFromArchive(archive, runtime);

        Assert.Equal(Path.Combine(runtime, "llama-server.exe"), server);
        Assert.True(File.Exists(server));

        // The server will not load without its sibling DLLs, so they must move too.
        Assert.True(File.Exists(Path.Combine(runtime, "ggml.dll")));
        Assert.True(File.Exists(Path.Combine(runtime, "llama.dll")));
    }

    [Fact]
    public void AcceptsAnArchiveThatIsAlreadyFlat()
    {
        string archive = BuildArchive("llama-server.exe", "ggml.dll");
        string runtime = Path.Combine(_directory, "runtime");

        string server = RuntimeInstaller.InstallFromArchive(archive, runtime);

        Assert.True(File.Exists(server));
        Assert.True(File.Exists(Path.Combine(runtime, "ggml.dll")));
    }

    [Fact]
    public void ArchiveWithoutTheServerIsRejected()
    {
        string archive = BuildArchive("docs/readme.txt");
        string runtime = Path.Combine(_directory, "runtime");

        var error = Assert.Throws<ModelDownloadException>(
            () => RuntimeInstaller.InstallFromArchive(archive, runtime));

        Assert.Contains("llama-server.exe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonZipInputIsRejectedWithAClearMessage()
    {
        string notAZip = Path.Combine(_directory, "broken.zip");
        File.WriteAllText(notAZip, "this is not a zip archive");

        Assert.Throws<ModelDownloadException>(
            () => RuntimeInstaller.InstallFromArchive(notAZip, Path.Combine(_directory, "runtime")));
    }
}
