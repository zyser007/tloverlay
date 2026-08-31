using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Serilog;
using TLOverlay.Core.Setup;
using TLOverlay.Core.Update;

namespace TLOverlay.App.Services;

/// <summary>
/// The app's side of updating: when to look, what to skip, and how to restart
/// into the new build.
///
/// Checking is deliberately quiet and deliberately rare. This program runs
/// beside a game, so it must never take the network or the foreground at a
/// moment the player did not choose - the check happens once a day at startup,
/// nothing is downloaded until asked, and everything it finds is reported by
/// returning it rather than by putting a dialog on top of the game.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Once a day is often enough for a program that ships rarely.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private readonly AppSettings _settings;
    private readonly GitHubUpdateSource _source;
    private readonly UpdateInstaller _installer;

    public UpdateService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = new GitHubUpdateSource(_http);
        _installer = new UpdateInstaller(_http);
    }

    /// <summary>Where the running executable lives, or null in a debug host.</summary>
    public static string? ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;

            return string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? null
                : path;
        }
    }

    /// <summary>Whether the program can replace itself where it is installed.</summary>
    public static bool CanSelfUpdate =>
        ExecutablePath is { } path && UpdateInstaller.CanReplace(path);

    /// <summary>Removes the previous version, or restores it if we died mid-swap.</summary>
    public static void CleanUpAfterUpdate()
    {
        if (ExecutablePath is { } path)
        {
            UpdateInstaller.CleanUpAfterUpdate(path);
        }
    }

    /// <summary>
    /// Looks for a newer release, honouring the policy and the once-a-day rule.
    /// <paramref name="force"/> is the player pressing the button, which ignores
    /// both the interval and any version they skipped.
    /// </summary>
    public async Task<UpdateManifest?> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!force && _settings.Updates == UpdatePolicy.Off)
        {
            return null;
        }

        if (!force
            && _settings.LastUpdateCheckUtc is { } last
            && DateTimeOffset.UtcNow - last < CheckInterval)
        {
            return null;
        }

        UpdateManifest? found = await _source
            .CheckAsync(App.Version, includePrerelease: false, cancellationToken)
            .ConfigureAwait(false);

        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        SettingsStore.Save(App.DataDirectory, _settings);

        if (found is null)
        {
            Log.Information("Update check: {Version} is current.", App.Version);
            return null;
        }

        Log.Information("Update check: {Found} is available (running {Running}).", found.Version, App.Version);

        if (!force && string.Equals(_settings.SkippedVersion, found.Tag, StringComparison.OrdinalIgnoreCase))
        {
            // Said no to this one already. Asking again on every startup is how
            // an update prompt becomes something people learn to dismiss without
            // reading.
            return null;
        }

        return found;
    }

    /// <summary>Remembers that the player does not want this particular version.</summary>
    public void Skip(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        _settings.SkippedVersion = manifest.Tag;
        SettingsStore.Save(App.DataDirectory, _settings);
    }

    public void SetPolicy(UpdatePolicy policy)
    {
        _settings.Updates = policy;
        SettingsStore.Save(App.DataDirectory, _settings);
    }

    /// <summary>
    /// Downloads, verifies, smoke-tests and installs an update, then restarts
    /// into it. Returns false when the new build failed to prove itself, in
    /// which case nothing was replaced.
    /// </summary>
    public async Task<bool> InstallAndRestartAsync(
        UpdateManifest manifest,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (ExecutablePath is not { } executable)
        {
            throw new UpdateInstallException(
                "อัพเดทอัตโนมัติได้เฉพาะตอนรันจากไฟล์ .exe เท่านั้น — ดาวน์โหลดเองจากหน้า release แทน");
        }

        string staging = Path.Combine(AppPaths.DataDirectory, "updates");
        string staged = await _installer
            .StageAsync(manifest, staging, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!await UpdateInstaller.VerifyRunsAsync(staged, manifest.Version, cancellationToken).ConfigureAwait(false))
        {
            Log.Error("Update {Version} did not pass its smoke test; not installing.", manifest.Version);
            return false;
        }

        UpdateInstaller.Apply(staged, executable);
        Log.Information("Updated to {Version}; restarting.", manifest.Version);

        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        return true;
    }
}
