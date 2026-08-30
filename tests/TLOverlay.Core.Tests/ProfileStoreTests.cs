using TLOverlay.Core.Profiles;
using Xunit;

namespace TLOverlay.Core.Tests;

public class ProfileStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tloverlay-profiles-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void SavedProfileRoundTrips()
    {
        var store = new ProfileStore(_directory);
        var profile = new GameProfile
        {
            Name = "Chrono Vale",
            ProcessName = "ChronoVale",
            DisplayMode = OverlayDisplayMode.Inline,
            FontSize = 26,
            Regions = [new CaptureRegion("Dialogue", 0.1, 0.7, 0.8, 0.2)],
            Glossary = [new GlossaryTerm { Source = "Vale", Target = null }],
        };

        string path = store.Save(profile);
        var loaded = store.TryLoad(path);

        Assert.NotNull(loaded);
        Assert.Equal("Chrono Vale", loaded!.Name);
        Assert.Equal(OverlayDisplayMode.Inline, loaded.DisplayMode);
        Assert.Equal(26, loaded.FontSize);
        Assert.Single(loaded.Regions);
        Assert.Equal(0.7, loaded.Regions[0].Y);
        Assert.Single(loaded.Glossary);
    }

    [Fact]
    public void LoadAllFindsEverySavedProfile()
    {
        var store = new ProfileStore(_directory);
        store.Save(GameProfile.CreateDefault("One"));
        store.Save(GameProfile.CreateDefault("Two"));

        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void CorruptProfileIsSkippedRatherThanThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ this is not json");

        var store = new ProfileStore(_directory);
        store.Save(GameProfile.CreateDefault("Good"));

        var profiles = store.LoadAll();

        Assert.Single(profiles);
        Assert.Equal("Good", profiles[0].Name);
    }

    [Fact]
    public void MissingDirectoryLoadsNothing()
    {
        Assert.Empty(new ProfileStore(_directory).LoadAll());
    }

    [Fact]
    public void TitleSpecificProfileBeatsProcessOnlyProfile()
    {
        var profiles = new List<GameProfile>
        {
            new() { Name = "Launcher catch-all", ProcessName = "game" },
            new() { Name = "Chapter II", ProcessName = "game", WindowTitleContains = "Chapter II" },
        };

        var matched = ProfileStore.Match(profiles, "game", "My Game - Chapter II");

        Assert.Equal("Chapter II", matched?.Name);
    }

    [Fact]
    public void FallsBackToProcessOnlyProfileWhenTitleDoesNotMatch()
    {
        var profiles = new List<GameProfile>
        {
            new() { Name = "Launcher catch-all", ProcessName = "game" },
            new() { Name = "Chapter II", ProcessName = "game", WindowTitleContains = "Chapter II" },
        };

        var matched = ProfileStore.Match(profiles, "game", "My Game - Chapter I");

        Assert.Equal("Launcher catch-all", matched?.Name);
    }

    [Fact]
    public void UnknownProcessMatchesNothing()
    {
        var profiles = new List<GameProfile> { new() { Name = "A", ProcessName = "game" } };

        Assert.Null(ProfileStore.Match(profiles, "other", "whatever"));
    }

    [Fact]
    public void ProfileNamesWithPathCharactersAreSafeToSave()
    {
        var store = new ProfileStore(_directory);

        string path = store.Save(GameProfile.CreateDefault("Game: The/Sequel"));

        Assert.True(File.Exists(path));
        Assert.Equal(_directory, Path.GetDirectoryName(path));
    }
}
