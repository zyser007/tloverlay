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
    public void ADraggedPanelPlacementSurvivesASaveAndLoad()
    {
        // RelativeRect is a positional record, so this also proves the
        // serialiser can rebuild it through its constructor.
        var store = new ProfileStore(_directory);
        var profile = GameProfile.CreateDefault("Placed");
        profile.PanelBounds = new RelativeRect(0.21, 0.62, 0.5, 0.14);

        var loaded = store.TryLoad(store.Save(profile));

        Assert.NotNull(loaded?.PanelBounds);
        Assert.Equal(0.21, loaded!.PanelBounds!.X, precision: 6);
        Assert.Equal(0.14, loaded.PanelBounds.Height, precision: 6);
    }

    [Fact]
    public void AProfileWithNoPanelPlacementLoadsAsNull()
    {
        var store = new ProfileStore(_directory);

        var loaded = store.TryLoad(store.Save(GameProfile.CreateDefault("Unplaced")));

        Assert.NotNull(loaded);
        Assert.Null(loaded!.PanelBounds);
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

    [Fact]
    public void AProfileFileWithNullCollectionsStillLoads()
    {
        // A hand-edited or half-written file, or one from a version that did not
        // have these fields. Deserialization hands the setter null, and without a
        // guard there the failure surfaces much later as a NullReferenceException
        // with nothing pointing back at the file.
        var store = new ProfileStore(_directory);
        string path = store.Save(GameProfile.CreateDefault("Nulls"));

        File.WriteAllText(path, """
            {
              "name": "Nulls",
              "regions": null,
              "glossary": null
            }
            """);

        GameProfile? loaded = store.LoadAll().SingleOrDefault(p => p.Name == "Nulls");

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Regions);
        Assert.Empty(loaded.Glossary);

        // The one that used to throw.
        Assert.Null(loaded.Region);
    }
}
