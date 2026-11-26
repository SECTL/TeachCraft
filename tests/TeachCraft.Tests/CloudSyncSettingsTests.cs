using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>云端同步偏好：默认全关、损坏即默认、保存能往返。全程用临时目录。</summary>
public sealed class CloudSyncSettingsTests : IDisposable
{
    private readonly string _directory;

    public CloudSyncSettingsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "teachcraft-cloud-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (Exception) { /* 临时目录清不掉不影响测试结论。 */ }
    }

    [Fact]
    public void Default_IsDisabled_WithAllOptionalFieldsOff()
    {
        var settings = CloudSyncSettings.Default;

        Assert.False(settings.Enabled);
        Assert.Equal(0, settings.ConsentVersion);
        Assert.Null(settings.LastDocumentId);
        Assert.False(settings.LastRosterHeight);
        Assert.False(settings.LastRosterVision);
        Assert.False(settings.LastRosterScore);
        Assert.False(settings.LastRosterNotes);
        Assert.False(settings.LastRosterOriginalGroup);
    }

    [Fact]
    public void FreshStore_LoadsDisabledDefault()
    {
        var store = new FileCloudSyncSettingsStore(_directory);
        Assert.False(store.Load().Enabled);
    }

    [Fact]
    public void CorruptFile_LoadsDefault()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ broken");
        var store = new FileCloudSyncSettingsStore(_directory);

        var loaded = store.Load();

        Assert.False(loaded.Enabled);
        Assert.Equal(0, loaded.ConsentVersion);
    }

    [Fact]
    public void Save_RoundTrip_PreservesValues()
    {
        var store = new FileCloudSyncSettingsStore(_directory);
        var original = new CloudSyncSettings(
            Enabled: true,
            ConsentVersion: 2,
            LastDocumentId: "synthetic-doc-id",
            LastRosterHeight: true,
            LastRosterVision: false,
            LastRosterScore: true,
            LastRosterNotes: false,
            LastRosterOriginalGroup: true);

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void CorruptFile_LoadsDefault_AndReportsWarning()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ broken");
        var store = new FileCloudSyncSettingsStore(_directory);

        var loaded = store.Load();

        Assert.False(loaded.Enabled);
        Assert.Equal(0, loaded.ConsentVersion);
        Assert.Equal(FileCloudSyncSettingsStore.SettingsLoadFailedReason, store.LastError);
    }

    [Fact]
    public void TrySave_WhenParentIsFile_ReturnsFalseAndSetsLastError()
    {
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new FileCloudSyncSettingsStore(blocker);

        var saved = store.TrySave(new CloudSyncSettings(Enabled: true));

        Assert.False(saved);
        Assert.False(string.IsNullOrEmpty(store.LastError));
        Assert.Contains("保存本机设置失败", store.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, store.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySave_Succeeds_ClearsPreviousErrorAndRoundTrips()
    {
        var store = new FileCloudSyncSettingsStore(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ broken");
        store.Load();
        Assert.NotNull(store.LastError);

        var original = new CloudSyncSettings(Enabled: true, ConsentVersion: 3, LastDocumentId: "synthetic-doc-id");
        Assert.True(store.TrySave(original));

        Assert.Null(store.LastError);
        Assert.Equal(original, store.Load());
    }

    [Fact]
    public void DefaultInterfaceMembers_KeepSimpleImplementersWorking()
    {
        ICloudSyncSettingsStore store = new NoopCloudSyncSettingsStore();

        Assert.True(store.TrySave(CloudSyncSettings.Default));
        Assert.Null(store.LastError);
    }
}
