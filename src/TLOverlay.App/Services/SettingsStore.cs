using System.IO;
using System.Text.Json;

namespace TLOverlay.App.Services;

public sealed class AppSettings
{
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
}

/// <summary>Reads and writes settings.json under %AppData%\TLOverlay.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string PathFor(string directory) => Path.Combine(directory, "settings.json");

    public static AppSettings Load(string directory)
    {
        string path = PathFor(directory);

        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
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
}
