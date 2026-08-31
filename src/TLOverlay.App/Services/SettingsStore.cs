using System.IO;
using System.Text.Json;
using TLOverlay.Core.Update;

namespace TLOverlay.App.Services;

public sealed class AppSettings
{
    /// <summary>
    /// Shape of this file, so a future version can migrate rather than guess.
    ///
    /// It exists because the app can now update itself: a build the player did
    /// not install by hand will read a file written by the one before it, and
    /// the first sign that something was lost should not be a profile that
    /// silently reverted to defaults.
    /// </summary>
    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;

    public TranslatorSettings Translator { get; set; } = new();

    /// <summary>Profile to use when no per-game profile matches.</summary>
    public string DefaultProfileName { get; set; } = "Default";

    /// <summary>
    /// Folder that holds runtime\ and models\. Null means the per-user data
    /// directory.
    ///
    /// Separate from the data directory because the model is gigabytes and the
    /// system drive is often the one that is full, while settings, profiles and
    /// logs are tiny and belong where Windows expects them.
    /// </summary>
    public string? InstallRoot { get; set; }

    /// <summary>
    /// What to do about new versions. Notify by default: a background download
    /// of seventy megabytes is not something to start on a player's connection
    /// while they are in a game.
    /// </summary>
    public UpdatePolicy Updates { get; set; } = UpdatePolicy.Notify;

    /// <summary>A release tag the player said no to, so it is not offered again.</summary>
    public string? SkippedVersion { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
}

/// <summary>Reads and writes settings.json under %LocalAppData%\TLOverlay.</summary>
public static class SettingsStore
{
    /// <summary>Bumped whenever the meaning of an existing field changes.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string PathFor(string directory) => Path.Combine(directory, "settings.json");

    public static AppSettings Load(string directory)
    {
        string path = PathFor(directory);

        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                    ?? new AppSettings();

                if (settings.SchemaVersion != CurrentSchemaVersion)
                {
                    // Keep what the previous version wrote, byte for byte, before
                    // this one starts writing its own shape over it. Cheap
                    // insurance on a file that holds where the model lives and
                    // every per-game setting.
                    Backup(path, settings.SchemaVersion);
                    settings.SchemaVersion = CurrentSchemaVersion;
                }

                return settings;
            }
        }
        catch (JsonException)
        {
            // Corrupt settings should not stop the app from starting.
        }
        catch (IOException)
        {
        }

        return new AppSettings();
    }

    public static void Save(string directory, AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathFor(directory), JsonSerializer.Serialize(settings, Options));
    }

    private static void Backup(string path, int fromSchemaVersion)
    {
        try
        {
            File.Copy(path, $"{path}.v{fromSchemaVersion}.bak", overwrite: true);
        }
        catch (IOException)
        {
            // A backup that cannot be written is not a reason to refuse to start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
