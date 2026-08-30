using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TLOverlay.Core.Translation;

/// <summary>
/// Owns the llama-server.exe child process.
///
/// The process is put in a Win32 job object with
/// KILL_ON_JOB_CLOSE so it dies with us even if the app is killed from Task
/// Manager or crashes. Without that, a stray multi-gigabyte server keeps
/// holding VRAM after the overlay is gone - which players notice immediately as
/// missing performance in the next game they launch.
/// </summary>
public sealed class LlamaServerProcess : IAsyncDisposable
{
    private readonly LlamaServerOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private SafeJobHandle? _job;
    private bool _disposed;

    public LlamaServerProcess(LlamaServerOptions options, ILogger<LlamaServerProcess>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<LlamaServerProcess>.Instance;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Starts the server if needed and waits for its health endpoint. Returns
    /// false rather than throwing when the model or executable is missing, so
    /// the UI can show a "set up your model" state instead of a crash dialog.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (await IsHealthyAsync(client, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsHealthyAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (!File.Exists(_options.ExecutablePath))
            {
                _logger.LogError("llama-server executable not found at {Path}. Run tools/fetch-models.ps1.", _options.ExecutablePath);
                return false;
            }

            if (!File.Exists(_options.ModelPath))
            {
                _logger.LogError("Model file not found at {Path}. Run tools/fetch-models.ps1.", _options.ModelPath);
                return false;
            }

            Start();
            return await WaitForHealthyAsync(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void Start()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_options.ModelPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(_options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--n-gpu-layers");
        startInfo.ArgumentList.Add(_options.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _job = JobObject.CreateKillOnCloseJob();

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start llama-server.");

        // Draining the pipes keeps the child from blocking on a full buffer.
        process.OutputDataReceived += (_, e) => LogChild(e.Data);
        process.ErrorDataReceived += (_, e) => LogChild(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (_job is not null)
        {
            JobObject.Assign(_job, process);
        }

        _process = process;
        _logger.LogInformation("Started llama-server (pid {Pid}) on port {Port}.", process.Id, _options.Port);
    }

    private void LogChild(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _logger.LogDebug("llama-server: {Line}", line);
        }
    }

    private async Task<bool> WaitForHealthyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                _logger.LogError("llama-server exited during startup with code {Code}.", _process.ExitCode);
                return false;
            }

            if (await IsHealthyAsync(client, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogError("llama-server did not become healthy within {Timeout}.", _options.StartupTimeout);
        return false;
    }

    private async Task<bool> IsHealthyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(_options.BaseAddress, "health"), cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var process = _process;
        _process = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }

        // Closing the job handle is the backstop that reaps anything Kill missed.
        _job?.Dispose();
        _job = null;
        _startGate.Dispose();
    }
}

internal sealed class SafeJobHandle : SafeHandle
{
    public SafeJobHandle(IntPtr handle)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => JobObject.CloseHandle(handle);
}

internal static class JobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    public static SafeJobHandle? CreateKillOnCloseJob()
    {
        IntPtr handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var job = new SafeJobHandle(handle);

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job.DangerousGetHandle(), JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                job.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    public static void Assign(SafeJobHandle job, Process process)
    {
        AssignProcessToJobObject(job.DangerousGetHandle(), process.Handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
