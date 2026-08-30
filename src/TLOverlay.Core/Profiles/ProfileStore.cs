using System.Text.Json;
using System.Text.Json.Serialization;

namespace TLOverlay.Core.Profiles;

/// <summary>
/// Loads and saves per-game profiles as JSON under %AppData%\TLOverlay\profiles.
/// Plain files rather than a database so a player can hand a working profile for
/// a game to someone else.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TLOverlay",
        "profiles");

    public string Directory => _directory;

    public IReadOnlyList<GameProfile> LoadAll()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return Array.Empty<GameProfile>();
        }

        var profiles = new List<GameProfile>();

        foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            var profile = TryLoad(path);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    public GameProfile? TryLoad(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException)
        {
            // A hand-edited profile should not take the app down.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string Save(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        System.IO.Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, SanitizeFileName(profile.Name) + ".json");

        // Write-then-move so a crash mid-save cannot leave a truncated profile.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, SerializerOptions));
        File.Move(temporary, path, overwrite: true);

        return path;
    }

    /// <summary>
    /// Picks the profile that best matches a running game: an explicit title
    /// match beats a bare process match, so per-title profiles win over a
    /// catch-all for the same launcher.
    /// </summary>
    public static GameProfile? Match(IEnumerable<GameProfile> profiles, string? processName, string? windowTitle)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        GameProfile? processOnly = null;

        foreach (var profile in profiles)
        {
            bool processMatches = !string.IsNullOrWhiteSpace(profile.ProcessName)
                && string.Equals(profile.ProcessName, processName, StringComparison.OrdinalIgnoreCase);

            if (!processMatches)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(profile.WindowTitleContains))
            {
                if (windowTitle is not null
                    && windowTitle.Contains(profile.WindowTitleContains, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }

                continue;
            }

            processOnly ??= profile;
        }

        return processOnly;
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }
}
