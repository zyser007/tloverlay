using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using TLOverlay.Core.Setup;

namespace TLOverlay.Core.Update;

/// <summary>An update could not be fetched, verified or installed.</summary>
public sealed class UpdateInstallException : Exception
{
    public UpdateInstallException(string message)
        : base(message)
    {
    }

    public UpdateInstallException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Fetches a release and puts it in place of the running program.
///
/// Windows will not let a running executable be overwritten, but it will let one
/// be renamed. That is the whole trick, and it is why there is no separate
/// updater program to install, keep in sync, or fail to clean up: the current
/// exe steps aside as .old, the new one takes its name, and the restarted app
/// deletes the leftover.
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>What the outgoing executable is renamed to.</summary>
    public const string PreviousSuffix = ".old";

    private readonly HttpClient _client;
    private readonly ModelDownloader _downloader;

    public UpdateInstaller(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _downloader = new ModelDownloader(client);
    }

    /// <summary>
    /// Downloads the new executable and returns where it was put, having checked
    /// that it is what the release says it is.
    /// </summary>
    public async Task<string> StageAsync(
        UpdateManifest manifest,
        string stagingDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        Directory.CreateDirectory(stagingDirectory);

        string expected = await FetchChecksumAsync(manifest, cancellationToken).ConfigureAwait(false);
        string staged = Path.Combine(stagingDirectory, $"TLOverlay-{manifest.Version}.exe");

        // The signature check inside the downloader rejects an HTML error page
        // dressed as an executable before it is ever renamed into place.
        await _downloader.DownloadAsync(
            manifest.ExecutableUrl,
            staged,
            FileSignature.WindowsExecutable,
            progress,
            cancellationToken).ConfigureAwait(false);

        string actual = await ComputeSha256Async(staged, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staged);

            throw new UpdateInstallException(
                "ไฟล์ที่ดาวน์โหลดมาไม่ตรงกับลายเซ็นของ release — ยกเลิกการอัพเดทเพื่อความปลอดภัย");
        }

        return staged;
    }

    /// <summary>
    /// Runs the staged build with --version and checks that it starts and says
    /// what it should.
    ///
    /// Worth the two seconds it costs. A publish can be broken in ways that only
    /// show up at startup - the WPF-and-trimming case did exactly that - and
    /// finding out here means the player keeps a working program instead of
    /// being left with one that no longer opens.
    /// </summary>
    public static async Task<bool> VerifyRunsAsync(
        string executablePath,
        Version expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,

            // Stdout only. A redirected pipe nobody drains is a deadlock waiting
            // for a child that writes more than it holds, and there is nothing
            // here worth reading on stderr.
            RedirectStandardError = false,
        };

        startInfo.ArgumentList.Add("--version");

        try
        {
            using Process? process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return false;
            }

            return process.ExitCode == 0
                && AppVersion.Parse(output.Trim()) == AppVersion.Normalize(expected);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the program can replace itself where it is installed.
    ///
    /// False under Program Files without elevation, and on read-only media. The
    /// honest answer there is to send the player to the download page rather
    /// than to ask for administrator rights an overlay has no business holding.
    /// </summary>
    public static bool CanReplace(string executablePath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));

        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        return InstallLocation.IsWritable(directory);
    }

    /// <summary>
    /// Swaps the staged build in for the running one. The caller restarts from
    /// <paramref name="executablePath"/> and exits.
    /// </summary>
    public static void Apply(string stagedPath, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(stagedPath))
        {
            throw new UpdateInstallException("ไม่พบไฟล์ที่ดาวน์โหลดไว้ — ลองดาวน์โหลดใหม่อีกครั้ง");
        }

        string previous = executablePath + PreviousSuffix;

        // A leftover from an earlier update, now that nothing is holding it.
        TryDelete(previous);

        try
        {
            File.Move(executablePath, previous, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateInstallException(
                $"เขียนทับโปรแกรมที่ {executablePath} ไม่ได้ — ลองย้ายโปรแกรมไปโฟลเดอร์ที่เขียนได้ แล้วอัพเดทใหม่",
                ex);
        }

        try
        {
            File.Move(stagedPath, executablePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the working program back rather than leaving the folder with
            // no executable in it at all.
            TryRestore(previous, executablePath);

            throw new UpdateInstallException("ติดตั้งเวอร์ชันใหม่ไม่สำเร็จ — เวอร์ชันเดิมยังใช้งานได้ตามปกติ", ex);
        }
    }

    /// <summary>
    /// Clears the previous version, and rescues the install if an update was
    /// interrupted between the two renames.
    ///
    /// Called on every startup. The rescue matters more than the tidying: that
    /// window is the one moment where the folder can hold a .old and no program.
    /// </summary>
    public static void CleanUpAfterUpdate(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        string previous = executablePath + PreviousSuffix;

        if (!File.Exists(previous))
        {
            return;
        }

        if (!File.Exists(executablePath))
        {
            TryRestore(previous, executablePath);
            return;
        }

        TryDelete(previous);
    }

    private async Task<string> FetchChecksumAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        string text;

        try
        {
            text = await _client.GetStringAsync(manifest.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateInstallException("ดาวน์โหลดไฟล์ลายเซ็นของ release ไม่สำเร็จ", ex);
        }

        string? hash = FindChecksum(text, GitHubUpdateSource.ExecutableAssetName);

        return hash ?? throw new UpdateInstallException(
            $"release นี้ไม่มีลายเซ็นของ {GitHubUpdateSource.ExecutableAssetName} — ดาวน์โหลดเองจากหน้า release แทน");
    }

    /// <summary>
    /// Reads one hash out of a sha256sum-style file: "&lt;hash&gt; *&lt;name&gt;",
    /// with either the binary star or a plain space, as both tools produce.
    /// </summary>
    internal static string? FindChecksum(string text, string fileName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = line.IndexOf(' ', StringComparison.Ordinal);

            if (split <= 0)
            {
                continue;
            }

            string hash = line[..split];
            string name = line[(split + 1)..].TrimStart('*', ' ');

            // Names may be written with a path in front of them.
            name = Path.GetFileName(name);

            if (hash.Length == 64
                && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }

        return null;
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    private static void TryRestore(string previous, string executablePath)
    {
        try
        {
            File.Move(previous, executablePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still running, or held by a scanner. It gets another chance on the
            // next startup.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
