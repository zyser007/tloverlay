using System.Diagnostics;

namespace TLOverlay.App.Services;

/// <summary>
/// What this app and its model server are costing in memory, right now.
///
/// On screen rather than in a log because of how the one bad case went: a leak
/// in the capture path took the app past ten gigabytes, and nothing in the app
/// said so - it took a screenshot of Task Manager from a user to find out. A
/// number on the panel makes the next one visible in seconds, and tells a player
/// on a small machine whether the model they picked actually fits.
/// </summary>
public static class MemoryReadout
{
    private const string ServerProcessName = "llama-server";

    /// <summary>Working set of this process, in bytes.</summary>
    public static long AppBytes
    {
        get
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                return self.WorkingSet64;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Working set of the model server, or zero when it is not running.
    ///
    /// Found by name rather than by holding the child's handle: the readout is a
    /// diagnostic, and it should not need the translator stack to be wired all
    /// the way through the UI to say something useful.
    /// </summary>
    public static long ModelServerBytes
    {
        get
        {
            long total = 0;

            try
            {
                foreach (Process process in Process.GetProcessesByName(ServerProcessName))
                {
                    using (process)
                    {
                        total += process.WorkingSet64;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between listing and reading it.
            }
            catch (PlatformNotSupportedException)
            {
            }

            return total;
        }
    }

    /// <summary>Physical memory on this machine, or zero when it cannot be read.</summary>
    public static long MachineBytes
    {
        get
        {
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return total > 0 ? total : 0;
        }
    }

    public static string Format(long bytes)
    {
        const double Mb = 1024 * 1024;
        const double Gb = Mb * 1024;

        return bytes >= Gb
            ? $"{bytes / Gb:F1} GB"
            : $"{bytes / Mb:F0} MB";
    }
}
