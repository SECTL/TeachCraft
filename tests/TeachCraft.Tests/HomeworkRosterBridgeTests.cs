using Avalonia.Controls;
using Homework.Core.Roster;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 宿主桥的映射与只读语义。所有服务都是内存假件：没有 HTTP、没有本机账号目录。
/// 姓名与班级均为合成数据（苏晚晴 / 陈屿 / 许砚 / 八年级1班 等）。
/// </summary>
public sealed class HomeworkRosterBridgeTests
{
    private sealed class FakeAccount : ICloudAccountService
    {
        public bool IsSignedIn { get; set; }
        public string? LastSignOutNotice => null;
        public CloudAccount? Current { get; set; }
        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeSettings : ICloudSyncSettingsStore
    {
        public bool Enabled { get; set; }
        public CloudSyncSettings Load() => new(Enabled: Enabled);
        public void Save(CloudSyncSettings settings) => Enabled = settings.Enabled;
        public string Path => "memory";
    }

    private sealed class FakeRosterSource : ISharedRosterSource
    {
        public Dictionary<string, SharedRosterDocument> Docs { get; } = new();
        public int WriteCalls { get; private set; }

        public Task<IReadOnlyList<SharedRosterInfo>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SharedRosterInfo>>(Docs.Values.Select(ToInfo).ToArray());

        public Task<SharedRosterDocument?> GetAsync(string rosterId, CancellationToken ct)
            => Task.FromResult(Docs.TryGetValue(rosterId, out var doc) ? doc : null);

        public Task<SharedRosterInfo> SaveAsync(
            SharedRosterDocument roster, RosterFieldSelection selection,
            string? existingId, int expectedVersion, CancellationToken ct)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }

        public Task DeleteAsync(string rosterId, CancellationToken ct)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }

        private static SharedRosterInfo ToInfo(SharedRosterDocument doc) => new(
            doc.RosterId, doc.Name, doc.Version, doc.UpdatedAtUtc, doc.Students.Count, 100, doc.Fields);
    }

    private static SharedRosterDocument Doc(
        string id, string name, RosterFieldPresence fields,
        params (string Name, string Number)[] students) => new()
    {
        RosterId = id,
        Name = name,
        Version = 2,
        UpdatedAtUtc = new DateTimeOffset(2026, 11, 24, 9, 12, 0, TimeSpan.Zero),
        Fields = fields,
        Students = students.Select((student, index) => new SharedRosterStudent
        {
            Index = index + 1,
            Name = student.Name,
            Number = student.Number,
        }).ToList(),
    };

    private static (HomeworkRosterBridge Bridge, FakeRosterSource Source, FakeAccount Account, FakeSettings Settings)
        CreateBridge(bool signedIn = true, bool enabled = true)
    {
        var account = new FakeAccount { IsSignedIn = signedIn };
        var settings = new FakeSettings { Enabled = enabled };
        var source = new FakeRosterSource();
        return (new HomeworkRosterBridge(account, source, settings), source, account, settings);
    }

    [Fact]
    public async Task ListRosters_MapsFieldsSummaryFromMissingOptionalFields()
    {
        var (bridge, source, _, _) = CreateBridge();
        source.Docs["a"] = Doc("a", "八年级1班", RosterFieldPresence.None, ("苏晚晴", "01"));
        source.Docs["b"] = Doc("b", "九年级2班", RosterFieldPresence.All, ("陈屿", "02"));

        var entries = await bridge.ListRostersAsync(CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Equal("a", entries[0].RosterId);
        Assert.Equal("八年级1班", entries[0].Name);
        Assert.Equal(2, entries[0].Version);
        Assert.Equal(1, entries[0].StudentCount);
        Assert.Equal("缺少可选字段：身高、视力、成绩、备注、原小组", entries[0].FieldsSummary);
        Assert.Equal("可选字段齐全", entries[1].FieldsSummary);
    }

    [Fact]
    public async Task ListRosters_LoadFailed_SurfacesErrorInsteadOfEmptyRow()
    {
        var account = new FakeAccount { IsSignedIn = true };
        var settings = new FakeSettings { Enabled = true };
        var store = new MemoryDocumentStore();
        store.Seed("broken", "{\"v\":1,\"kind\":\"teachcraft.roster.v1\"}");
        store.FailGetFor("broken");
        var bridge = new HomeworkRosterBridge(account, new SectlSharedRosterSource(store), settings);

        var entries = await bridge.ListRostersAsync(CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("broken", entry.RosterId);
        Assert.Contains("读取失败", entry.FieldsSummary);
    }

    [Fact]
    public async Task Download_MapsDocumentToRosterImportResult()
    {
        var (bridge, source, _, _) = CreateBridge();
        source.Docs["a"] = Doc("a", "八年级1班", RosterFieldPresence.None,
            ("苏晚晴", "01"), ("陈屿", "02"));

        var result = await bridge.DownloadAsync(["a"], CancellationToken.None);

        Assert.Single(result.Classes);
        Assert.Equal("八年级1班", result.Classes[0].Name);
        Assert.Equal(2, result.Students.Count);
        Assert.Equal(["苏晚晴", "陈屿"], result.Students.Select(s => s.Name).ToArray());
        Assert.Equal(["01", "02"], result.Students.Select(s => s.Number).ToArray());
        Assert.All(result.Students, s => Assert.Equal(result.Classes[0].Id, s.ClassId));
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.SkippedRows);
    }

    [Fact]
    public async Task Download_MultipleClasses_MergeInInputOrder()
    {
        var (bridge, source, _, _) = CreateBridge();
        source.Docs["a"] = Doc("a", "八年级1班", RosterFieldPresence.None, ("苏晚晴", "01"));
        source.Docs["b"] = Doc("b", "九年级2班", RosterFieldPresence.None, ("陈屿", "02"), ("许砚", "03"));

        var result = await bridge.DownloadAsync(["a", "b"], CancellationToken.None);

        Assert.Equal(["八年级1班", "九年级2班"], result.Classes.Select(c => c.Name).ToArray());
        Assert.Equal(3, result.Students.Count);
        Assert.Equal(result.Classes[0].Id, result.Students[0].ClassId);
        Assert.Equal(result.Classes[1].Id, result.Students[1].ClassId);
        Assert.Equal(result.Classes[1].Id, result.Students[2].ClassId);
    }

    [Fact]
    public async Task Download_MissingRoster_WarnsAndSkips()
    {
        var (bridge, source, _, _) = CreateBridge();
        source.Docs["present"] = Doc("present", "八年级1班", RosterFieldPresence.None, ("苏晚晴", "01"));

        var result = await bridge.DownloadAsync(["missing", "present"], CancellationToken.None);

        Assert.Single(result.Classes);
        Assert.Single(result.Students);
        Assert.Contains(CloudErrorText.NotFound, result.Warnings[0]);
        Assert.Contains("missing", result.Warnings[0]);
        Assert.Contains(SharedRosterImport.MissingRosterMarker, result.Warnings[0]);
        Assert.Equal(1, result.SkippedRows);
    }

    [Fact]
    public async Task Download_MultipleMissingRosters_CountEachAsSkipped()
    {
        var (bridge, _, _, _) = CreateBridge();

        var result = await bridge.DownloadAsync(["m1", "m2"], CancellationToken.None);

        Assert.Empty(result.Classes);
        Assert.Empty(result.Students);
        Assert.Equal(2, result.SkippedRows);
        Assert.Equal(2, result.Warnings.Count(
            w => w.Contains(SharedRosterImport.MissingRosterMarker, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Download_NeverCallsWriteApis()
    {
        var (bridge, source, _, _) = CreateBridge();
        source.Docs["a"] = Doc("a", "八年级1班", RosterFieldPresence.None, ("苏晚晴", "01"));

        await bridge.DownloadAsync(["a"], CancellationToken.None);

        Assert.Equal(0, source.WriteCalls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void IsAvailable_RequiresSignedInAndEnabled(bool signedIn, bool enabled, bool expected)
    {
        var (bridge, _, _, _) = CreateBridge(signedIn, enabled);
        Assert.Equal(expected, bridge.IsAvailable);
    }

    [Fact]
    public async Task EnsureSignedIn_WhenAlreadySignedIn_ReturnsTrueWithoutOpeningWindow()
    {
        var (bridge, _, _, _) = CreateBridge(signedIn: true);
        Assert.True(await bridge.EnsureSignedInAsync(null!));
    }
}
