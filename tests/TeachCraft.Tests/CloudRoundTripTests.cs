using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SeatGrid.Core.Cloud;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 云端名单 / 座位表的往返与重启连续性回归。全部走内存存储与合成姓名，不联网、不落盘。
/// </summary>
public sealed class CloudRoundTripTests
{
    private sealed class FakeAccount : ICloudAccountService
    {
        public CloudAccount? Current { get; } =
            new("synthetic-user", "示例教师", Array.Empty<string>(),
                SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow);

        public bool IsSignedIn => Current is not null;
        public string? LastSignOutNotice => null;
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("synthetic-token");

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSettings : ICloudSyncSettingsStore
    {
        public CloudSyncSettings Settings { get; private set; } = new(Enabled: true, ConsentVersion: 1);
        public CloudSyncSettings Load() => Settings;
        public void Save(CloudSyncSettings settings) => Settings = settings;
        public string Path => "memory";
    }

    private static SeatGridCloudBridge CreateBridge(
        ICloudAccountService account, ISharedRosterSource rosters,
        ICloudDocumentStore seatDocuments, ICloudSyncSettingsStore settings)
        => new(account, rosters, seatDocuments, settings);

    [Fact]
    public async Task UploadThenDownloadRoster_CarriesRealIdVersionAndOnlyCheckedFields()
    {
        var account = new FakeAccount();
        var settings = new FakeSettings();
        var rosterStore = new MemoryDocumentStore();
        var seatStore = new MemoryDocumentStore();
        var rosters = new SectlSharedRosterSource(rosterStore);
        var bridge = CreateBridge(account, rosters, seatStore, settings);

        // 没有已关联名单：勾选框初始全关，但传入的学生带全部可选值。
        var fullStudents = new[]
        {
            new SeatGridRosterStudent(1, "苏晚晴", "女", "S0001", 170.5, 5.0, 612.5, "合成备注", "一组"),
        };
        var heightOnly = new SeatGridRosterFields(true, false, false, false, false);

        var entry = await bridge.UploadRosterAsync(
            "合成名单", fullStudents, heightOnly, null, 0, CancellationToken.None);
        var pick = await bridge.DownloadRosterAsync(entry.RosterId, CancellationToken.None);

        Assert.NotNull(pick);
        Assert.False(string.IsNullOrEmpty(pick!.RosterId));
        Assert.True(pick.Version >= 1);
        var student = Assert.Single(pick.Students);
        Assert.Equal(170.5, student.Height);
        Assert.Null(student.Score);
        Assert.Null(student.Vision);
        Assert.Null(student.Notes);
        Assert.Null(student.OriginalGroup);
    }

    [Fact]
    public async Task UploadDocument_AfterRestart_UpdatesPersistedDocInsteadOfCreatingNewOne()
    {
        var account = new FakeAccount();
        var settings = new FakeSettings();
        var rosterStore = new MemoryDocumentStore();
        var seatStore = new MemoryDocumentStore();
        var rosters = new SectlSharedRosterSource(rosterStore);

        var first = CreateBridge(account, rosters, seatStore, settings);
        var created = await first.UploadDocumentAsync(
            new SeatGridCloudDocument { Name = "合成座位表" }, null, 0, CancellationToken.None);
        Assert.Equal(1, created.Entry.Revision);

        // 模拟重启：新桥接实例，只有设置与云端存储是同一份。
        var restarted = CreateBridge(account, rosters, seatStore, settings);
        var (id, revision) = restarted.GetLastDocument();
        Assert.Equal(created.Entry.Id, id);
        Assert.Equal(1, revision);

        var updated = await restarted.UploadDocumentAsync(
            new SeatGridCloudDocument { Name = "合成座位表（改）" }, id, revision, CancellationToken.None);
        Assert.Equal(created.Entry.Id, updated.Entry.Id);
        Assert.Equal(2, updated.Entry.Revision);
        Assert.True(updated.Overwritten);

        var documents = await restarted.ListDocumentsAsync(CancellationToken.None);
        var doc = Assert.Single(documents);
        Assert.Equal(created.Entry.Id, doc.Id);
        Assert.Equal(2, doc.Revision);
    }

    [Fact]
    public void Settings_RoundTrip_PreservesLastDocumentRevision()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "teachcraft-cloud-roundtrip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FileCloudSyncSettingsStore(directory);
            var original = new CloudSyncSettings(
                Enabled: true, ConsentVersion: 2,
                LastDocumentId: "synthetic-doc-id", LastDocumentRevision: 7);

            store.Save(original);
            var loaded = store.Load();

        Assert.Equal("synthetic-doc-id", loaded.LastDocumentId);
        Assert.Equal(7, loaded.LastDocumentRevision);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAndDownloadDocument_TracksAndClearsDocumentIdentity()
    {
        var account = new FakeAccount();
        var settings = new FakeSettings();
        var rosterStore = new MemoryDocumentStore();
        var seatStore = new MemoryDocumentStore();
        var rosters = new SectlSharedRosterSource(rosterStore);
        var bridge = CreateBridge(account, rosters, seatStore, settings);

        var created = await bridge.UploadDocumentAsync(
            new SeatGridCloudDocument { Name = "合成座位表" }, null, 0, CancellationToken.None);

        var pull = await bridge.DownloadDocumentAsync(created.Entry.Id, CancellationToken.None);
        Assert.NotNull(pull);
        Assert.Equal(created.Entry.Id, pull!.DocumentId);
        Assert.Equal(1, pull.Revision);

        await bridge.DeleteDocumentAsync(created.Entry.Id, CancellationToken.None);
        var (id, revision) = bridge.GetLastDocument();
        Assert.Null(id);
        Assert.Equal(0, revision);
    }

    [Fact]
    public async Task UploadRoster_DuplicateStudentName_ThrowsUserFacingCloudAuthException()
    {
        var account = new FakeAccount();
        var settings = new FakeSettings();
        var bridge = CreateBridge(
            account, new SectlSharedRosterSource(new MemoryDocumentStore()), new MemoryDocumentStore(), settings);
        var duplicates = new[]
        {
            new SeatGridRosterStudent(1, "苏晚晴", "女", "S001", null, null, null, null, null),
            new SeatGridRosterStudent(2, "苏晚晴", "女", "S002", null, null, null, null, null),
        };

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => bridge.UploadRosterAsync(
            "重名名单", duplicates, SeatGridRosterFields.None, null, 0, CancellationToken.None));
        Assert.Equal(CloudErrorText.RosterNameDuplicate, ex.Message);
        Assert.Equal(CloudErrorText.RosterNameDuplicate, CloudErrorText.DescribeForUi(ex, "上传共享名单"));
    }
}
