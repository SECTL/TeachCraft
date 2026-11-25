using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkScanTaskPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hw-scan-prefs-" + Guid.NewGuid().ToString("N"));

    public HomeworkScanTaskPreferencesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }

    [Fact]
    public void CreateDefault_HasNonEmptyInboxFolder()
    {
        var preferences = ScanTaskPreferences.CreateDefault();

        Assert.False(string.IsNullOrWhiteSpace(preferences.InboxFolder));
        Assert.Contains("TeachCraft", preferences.InboxFolder);
    }

    [Fact]
    public void Store_RoundTripsAllSettings()
    {
        var path = Path.Combine(_root, "scan-task.json");
        var store = new ScanTaskPreferencesStore(path);
        var preferences = new ScanTaskPreferences
        {
            InboxFolder = Path.Combine(_root, "收件"),
            Source = ScanSource.FeederDuplex,
            PageSize = ScanPageSize.EightK,
            ResolutionDpi = 600,
            ColorMode = ScanColorMode.Grayscale,
            PageLimit = ScanPageLimit.All,
            ImportToCurrent = false,
        };

        Assert.True(store.TrySave(preferences));

        var loaded = store.Load();

        Assert.Equal(preferences.InboxFolder, loaded.InboxFolder);
        Assert.Equal(ScanSource.FeederDuplex, loaded.Source);
        Assert.Equal(ScanPageSize.EightK, loaded.PageSize);
        Assert.Equal(600, loaded.ResolutionDpi);
        Assert.Equal(ScanColorMode.Grayscale, loaded.ColorMode);
        Assert.Equal(ScanPageLimit.All, loaded.PageLimit);
        Assert.False(loaded.ImportToCurrent);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        var path = Path.Combine(_root, "scan-task.json");
        File.WriteAllText(path, "{ not json");

        var loaded = new ScanTaskPreferencesStore(path).Load();

        Assert.False(string.IsNullOrWhiteSpace(loaded.InboxFolder));
        Assert.Equal(300, loaded.ResolutionDpi);
        Assert.Equal(ScanSource.FeederSingle, loaded.Source);
    }

    [Fact]
    public void Normalized_RepairsEmptyFolderAndFlatbedLimit()
    {
        var preferences = new ScanTaskPreferences
        {
            InboxFolder = " ",
            Source = ScanSource.Flatbed,
            PageLimit = ScanPageLimit.All,
            ResolutionDpi = 1500,
        };

        var normalized = preferences.Normalized();

        Assert.False(string.IsNullOrWhiteSpace(normalized.InboxFolder));
        Assert.Equal(ScanPageLimit.FirstPage, normalized.PageLimit);
        Assert.Equal(1200, normalized.ResolutionDpi);
    }
}
