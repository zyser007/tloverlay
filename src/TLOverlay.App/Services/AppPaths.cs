using System.IO;
using TLOverlay.Core.Setup;

namespace TLOverlay.App.Services;

/// <summary>
/// Every location the app writes to.
///
/// All of it lives under %LocalAppData%\TLOverlay rather than beside the
/// executable. Beside-the-exe fails in the situations users actually hit -
/// running straight out of a zip, installing under Program Files - and it fails
/// after a two-gigabyte download rather than before it. Local rather than
/// Roaming matters too: roaming profiles on a managed domain would try to
/// synchronise the model.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLOverlay");

    public static string ModelsDirectory => Path.Combine(DataDirectory, "models");

    public static string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string ProfilesDirectory => Path.Combine(DataDirectory, "profiles");

    /// <summary>
    /// True when the app was launched from an archiver's temp copy, where
    /// everything it writes is deleted the moment the archiver exits.
    /// </summary>
    public static bool IsRunningFromArchive => InstallLocation.IsRunningFromArchive(AppContext.BaseDirectory);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);

        // Logs and profiles used to be left to whoever wrote to them first. The
        // file logger creates its own folder but swallows the failure if it
        // cannot, which is a bad trade for the one folder you go looking for
        // after a crash.
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
    }
}
