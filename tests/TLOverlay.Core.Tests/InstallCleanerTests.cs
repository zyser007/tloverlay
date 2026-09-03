using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class InstallCleanerTests
{
    [Fact]
    public void DeletingTheModelTakesTheHalfFinishedDownloadWithIt()
    {
        using var folder = new TempTree();

        string model = folder.Write("models/translator.gguf", "weights");
        string partial = folder.Write("models/translator.gguf.partial", "half a download");

        InstallCleaner.DeleteModel(model);

        Assert.False(File.Exists(model));

        // Invisible in the app and possibly most of a gigabyte: someone clearing
        // space would never think to look for it.
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void DeletingAModelThatIsNotThereIsNotAnError()
    {
        using var folder = new TempTree();

        InstallCleaner.DeleteModel(Path.Combine(folder.Path, "models", "gone.gguf"));
        InstallCleaner.DeleteModel(null);
        InstallCleaner.DeleteModel("   ");
    }

    [Fact]
    public void TheReportedModelSizeCoversBothFiles()
    {
        using var folder = new TempTree();

        string model = folder.Write("models/translator.gguf", new string('w', 100));
        folder.Write("models/translator.gguf.partial", new string('p', 50));

        Assert.Equal(150, InstallCleaner.ModelSize(model));
    }

    [Fact]
    public void DeletingTheServerRemovesTheWholeRuntimeFolder()
    {
        using var folder = new TempTree();

        string exe = folder.Write("runtime/llama-server.exe", "binary");
        folder.Write("runtime/ggml.dll", "library");

        InstallCleaner.DeleteRuntime(exe, folder.Path);

        // The exe does not run without the DLLs beside it, so leaving them would
        // reclaim a fraction of the space while looking like a full cleanup.
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "runtime")));
    }

    [Fact]
    public void AServerSomewhereElseLosesOnlyItsExecutable()
    {
        using var folder = new TempTree();

        string exe = folder.Write("elsewhere/llama-server.exe", "binary");
        string neighbour = folder.Write("elsewhere/someone-elses-file.txt", "not ours");

        InstallCleaner.DeleteRuntime(exe, folder.Path);

        Assert.False(File.Exists(exe));

        // That folder is the player's, and may hold anything.
        Assert.True(File.Exists(neighbour));
    }

    [Fact]
    public void TheReportedServerSizeCoversTheFolderItWouldDelete()
    {
        using var folder = new TempTree();

        string exe = folder.Write("runtime/llama-server.exe", new string('a', 60));
        folder.Write("runtime/ggml.dll", new string('b', 40));

        Assert.Equal(100, InstallCleaner.RuntimeSize(exe, folder.Path));

        // Outside the managed folder only the executable would go, so only it is
        // counted.
        string other = folder.Write("elsewhere/llama-server.exe", new string('c', 30));
        Assert.Equal(30, InstallCleaner.RuntimeSize(other, folder.Path));
    }

    [Fact]
    public void SizeOfAnythingMissingIsZero()
    {
        Assert.Equal(0, InstallCleaner.SizeOf(null));
        Assert.Equal(0, InstallCleaner.SizeOf("   "));
        Assert.Equal(0, InstallCleaner.SizeOf(Path.Combine(Path.GetTempPath(), $"tloverlay-none-{Guid.NewGuid():N}")));
        Assert.Equal(0, InstallCleaner.RuntimeSize(null, null));
        Assert.Equal(0, InstallCleaner.ModelSize(null));
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tloverlay-clean-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, string contents)
        {
            string full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
            return full;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
