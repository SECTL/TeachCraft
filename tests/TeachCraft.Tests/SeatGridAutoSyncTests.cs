using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SeatGrid.App;
using SeatGrid.App.Cloud;
using SeatGrid.Core;
using SeatGrid.Core.Cloud;
using SeatGrid.Core.Models;
using SeatGrid.Core.Roster;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 归位座位表 / 约束的自动保存：默认关闭、一次改动一次延迟上传、失败只进状态栏。
/// 名单与姓名全部为合成数据。
/// </summary>
public sealed class SeatGridAutoSyncTests
{
    private static readonly string[] Names = { "苏晚晴", "陈屿", "林知夏", "沈青禾" };

    private static RosterImportResult Roster() => new(
        string.Empty, "合成名单",
        Names.Select((name, i) => new Student
        {
            Name = name,
            Gender = i % 2 == 0 ? Gender.Female : Gender.Male,
            Number = $"S{i + 1:D4}",
        }).ToList(),
        Array.Empty<string>());

    private static SeatGridViewModel Ready(AutoSyncFakeBridge bridge)
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster());
        vm.AttachCloud(bridge);
        vm.LinkSharedRoster("roster-1", "合成名单", 1, SeatGridRosterFields.None);
        return vm;
    }

    [Fact]
    public void ShouldAutoSync_AllConditionsMet_ReturnsTrue()
    {
        Assert.True(CloudAutoSyncPolicy.ShouldAutoSync(
            new CloudSyncSettings(Enabled: true, AutoSyncSeatDocument: true),
            isSignedIn: true, isExamMode: false, hasLinkedRoster: true));
    }

    [Theory]
    [InlineData(true, false, true, false, true)]   // 自动保存开关关闭
    [InlineData(false, true, true, false, true)]  // 云同步总开关关闭
    [InlineData(true, true, false, false, true)]  // 未登录
    [InlineData(true, true, true, true, true)]    // 考场快照
    [InlineData(true, true, true, false, false)]  // 未关联共享名单
    public void ShouldAutoSync_Blocked_ReturnsFalse(
        bool enabled, bool autoSync, bool signedIn, bool exam, bool roster)
    {
        Assert.False(CloudAutoSyncPolicy.ShouldAutoSync(
            new CloudSyncSettings(Enabled: enabled, AutoSyncSeatDocument: autoSync),
            signedIn, exam, roster));
    }

    [Fact]
    public async Task RecordChange_TriggersExactlyOneUpload_AfterDebounce()
    {
        var bridge = new AutoSyncFakeBridge { Available = true, AutoSyncEnabled = true };
        var vm = Ready(bridge);

        // 连续三次改动：防抖取消前两次，只上传最终状态。
        vm.AutoArrange();
        vm.AutoArrange();
        vm.AutoArrange();

        var call = await bridge.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("roster-1", call.Document.RosterId);
        Assert.Equal("doc-7", call.ReplacingId);
        Assert.Equal(3, call.ExpectedRevision);

        await vm.AutoSyncSettled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, bridge.CallCount);
        Assert.Equal(1, bridge.RememberCount);
        Assert.Equal("doc-8", bridge.RememberedId);
    }

    [Fact]
    public async Task AutoSyncFailure_DoesNotThrowOrMutateLocal()
    {
        var bridge = new AutoSyncFakeBridge
        {
            Available = true,
            AutoSyncEnabled = true,
            UploadException = new InvalidOperationException("模拟网络失败"),
        };
        var vm = Ready(bridge);

        var status = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SeatGridViewModel.StatusText) &&
                vm.StatusText.Contains("自动同步失败", StringComparison.Ordinal))
                status.TrySetResult(vm.StatusText);
        };

        vm.AutoArrange();
        var students = vm.StudentCount;
        var seats = vm.AssignedCount;

        var text = await status.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("自动同步失败", text);
        Assert.Contains("可手动上传", text);
        Assert.Equal(students, vm.StudentCount);
        Assert.Equal(seats, vm.AssignedCount);
        Assert.Equal(0, bridge.RememberCount);
    }

    [Fact]
    public void DisabledAutoSync_NeverCallsBridge()
    {
        var bridge = new AutoSyncFakeBridge { Available = true, AutoSyncEnabled = false };
        var vm = Ready(bridge);

        vm.AutoArrange();
        vm.AutoArrange();

        Assert.True(vm.AutoSyncSettled.IsCompletedSuccessfully);
        Assert.Equal(0, bridge.CallCount);
    }

    [Fact]
    public void Toggle_WritesBridgeSetting_AndReflectsState()
    {
        var bridge = new AutoSyncFakeBridge { Available = true };
        var vm = Ready(bridge);
        Assert.False(vm.IsAutoSyncEnabled);

        vm.IsAutoSyncEnabled = true;
        Assert.True(bridge.AutoSyncEnabled);
        Assert.True(vm.IsAutoSyncEnabled);

        vm.IsAutoSyncEnabled = false;
        Assert.False(bridge.AutoSyncEnabled);
    }

    [Fact]
    public void SettingsStore_RoundTripsAutoSyncFlag()
    {
        using var dir = new TempDir();
        var store = new FileCloudSyncSettingsStore(dir.Path);
        Assert.False(store.Load().AutoSyncSeatDocument);

        store.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1, AutoSyncSeatDocument: true));
        Assert.True(store.Load().AutoSyncSeatDocument);

        store.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        Assert.False(store.Load().AutoSyncSeatDocument);
    }

    private sealed class AutoSyncFakeBridge : ISeatGridCloudBridge, ISeatGridLastDocumentSource
    {
        public readonly record struct UploadCall(
            SeatGridCloudDocument Document, string? ReplacingId, int ExpectedRevision);

        public bool Available { get; set; }
        public bool AutoSyncEnabled { get; set; }
        public Exception? UploadException { get; set; }
        public string LastDocumentId { get; set; } = "doc-7";
        public int LastDocumentRevision { get; set; } = 3;

        private readonly List<UploadCall> _calls = new();
        public IReadOnlyList<UploadCall> Calls => _calls;
        public int CallCount => _calls.Count;
        public readonly TaskCompletionSource<UploadCall> FirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RememberCount { get; private set; }
        public string? RememberedId { get; private set; }
        public int RememberedRevision { get; private set; }

        public bool IsAvailable => Available;
        public bool IsSignedIn => Available;
        public bool IsAutoSyncEnabled => AutoSyncEnabled;
        public SeatGridRosterFields LastRosterFields { get; set; } = SeatGridRosterFields.None;
        public void RememberRosterFields(SeatGridRosterFields fields) => LastRosterFields = fields;
        public string StatusText => "合成云端";
        public string RosterConsentText => "合成同意文案";
        public string BuildSeatConsentText(string rosterName, int rosterVersion, string fieldsSummary) => "";
        public event EventHandler? StateChanged { add { } remove { } }

        public (string? Id, int Revision) GetLastDocument() => (LastDocumentId, LastDocumentRevision);
        public void SetAutoSyncEnabled(bool value) => AutoSyncEnabled = value;

        public Task<bool> EnsureSignedInAsync(Window owner) => Task.FromResult(Available);
        public void OpenAccountSettings(Window owner) { }

        public Task<IReadOnlyList<SeatGridRosterEntry>> ListRostersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SeatGridRosterEntry>>(Array.Empty<SeatGridRosterEntry>());
        public Task<SeatGridRosterPick?> DownloadRosterAsync(string rosterId, CancellationToken ct)
            => Task.FromResult<SeatGridRosterPick?>(null);
        public Task<SeatGridRosterEntry> UploadRosterAsync(
            string name, IReadOnlyList<SeatGridRosterStudent> projectedStudents, SeatGridRosterFields fields,
            string? existingId, int expectedVersion, CancellationToken ct)
            => Task.FromResult(new SeatGridRosterEntry(
                existingId ?? "new", name, expectedVersion + 1, DateTimeOffset.UtcNow,
                projectedStudents.Count, fields));
        public Task DeleteRosterAsync(string rosterId, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SeatGridCloudEntry>> ListDocumentsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SeatGridCloudEntry>>(Array.Empty<SeatGridCloudEntry>());
        public Task<SeatGridCloudPull?> DownloadDocumentAsync(string documentId, CancellationToken ct)
            => Task.FromResult<SeatGridCloudPull?>(null);

        public Task<SeatGridCloudSaveResult> UploadDocumentAsync(
            SeatGridCloudDocument document, string? replacingId, int expectedRevision, CancellationToken ct)
        {
            var call = new UploadCall(document, replacingId, expectedRevision);
            _calls.Add(call);
            FirstCall.TrySetResult(call);
            return UploadException is not null
                ? Task.FromException<SeatGridCloudSaveResult>(UploadException)
                : Task.FromResult(new SeatGridCloudSaveResult(
                    new SeatGridCloudEntry("doc-8", document.Name, DateTimeOffset.UtcNow, 100, expectedRevision + 1),
                    expectedRevision > 0));
        }

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct) => Task.CompletedTask;

        public void RememberDocument(string? id, int revision)
        {
            RememberCount++;
            RememberedId = id;
            RememberedRevision = revision;
        }
    }
}
