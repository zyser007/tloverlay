using System.Text.Json;
using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class LlamaReleaseResolverTests
{
    /// <summary>Shaped like a real llama.cpp release payload.</summary>
    private const string ReleaseJson = """
        {
          "tag_name": "b7421",
          "assets": [
            { "name": "llama-b7421-bin-ubuntu-x64.zip",           "browser_download_url": "https://example.invalid/ubuntu.zip",  "size": 10 },
            { "name": "llama-b7421-bin-win-cpu-x64.zip",          "browser_download_url": "https://example.invalid/cpu.zip",     "size": 20 },
            { "name": "llama-b7421-bin-win-cuda-cu12.4-x64.zip",  "browser_download_url": "https://example.invalid/cuda.zip",    "size": 30 },
            { "name": "llama-b7421-bin-win-vulkan-x64.zip",       "browser_download_url": "https://example.invalid/vulkan.zip",  "size": 40 },
            { "name": "llama-b7421-bin-win-cpu-arm64.zip",        "browser_download_url": "https://example.invalid/arm.zip",     "size": 50 }
          ]
        }
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void PicksTheWindowsCpuArchive()
    {
        var asset = LlamaReleaseResolver.Select(Parse(ReleaseJson), LlamaBackend.Cpu);

        Assert.Equal("llama-b7421-bin-win-cpu-x64.zip", asset.Name);
        Assert.Equal(20, asset.SizeBytes);
    }

    [Fact]
    public void PicksTheCudaArchiveWhenGpuIsRequested()
    {
        var asset = LlamaReleaseResolver.Select(Parse(ReleaseJson), LlamaBackend.Cuda);

        Assert.Equal("llama-b7421-bin-win-cuda-cu12.4-x64.zip", asset.Name);
    }

    [Fact]
    public void NeverPicksALinuxBuild()
    {
        var asset = LlamaReleaseResolver.Select(Parse(ReleaseJson), LlamaBackend.Cpu);

        Assert.DoesNotContain("ubuntu", asset.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TreatsAWindowsBuildWithNoBackendInItsNameAsCpu()
    {
        // llama.cpp has shipped the plain build without "cpu" in the filename.
        const string Json = """
            {
              "assets": [
                { "name": "llama-b9000-bin-win-x64.zip", "browser_download_url": "https://example.invalid/plain.zip", "size": 1 },
                { "name": "llama-b9000-bin-win-cuda-x64.zip", "browser_download_url": "https://example.invalid/cuda.zip", "size": 2 }
              ]
            }
            """;

        var asset = LlamaReleaseResolver.Select(Parse(Json), LlamaBackend.Cpu);

        Assert.Equal("llama-b9000-bin-win-x64.zip", asset.Name);
    }

    [Fact]
    public void MissingMatchNamesWhatTheReleaseActuallyContains()
    {
        // Asset names have been renamed more than once upstream; an error that
        // just says "not found" would be undiagnosable from a bug report.
        const string Json = """
            { "assets": [ { "name": "llama-bin-macos-arm64.zip", "browser_download_url": "https://example.invalid/mac.zip", "size": 1 } ] }
            """;

        var error = Assert.Throws<ModelDownloadException>(
            () => LlamaReleaseResolver.Select(Parse(Json), LlamaBackend.Cpu));

        Assert.Contains("llama-bin-macos-arm64.zip", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWithNoAssetListIsRejected()
    {
        var error = Assert.Throws<ModelDownloadException>(
            () => LlamaReleaseResolver.Select(Parse("""{ "tag_name": "b1" }"""), LlamaBackend.Cpu));

        Assert.NotEmpty(error.Message);
    }
}
