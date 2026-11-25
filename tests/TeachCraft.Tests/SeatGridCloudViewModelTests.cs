using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using SeatGrid.App;
using SeatGrid.App.Cloud;
using SeatGrid.Core;
using SeatGrid.Core.Cloud;
using SeatGrid.Core.Exam;
using SeatGrid.Core.Export;
using SeatGrid.Core.Models;
using SeatGrid.Core.Roster;
using SeatGrid.Core.Seating;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class SeatGridCloudViewModelTests
{
    // 全部为合成姓名，不含任何真实师生信息。
    private static readonly string[] NamesA = { "苏晚晴", "陈屿", "林知夏", "沈青禾" };
    private static readonly string[] NamesB = { "陆明川", "顾南乔", "叶临溪" };

    private static RosterImportResult Roster(IEnumerable<string> names) => new(
        string.Empty, "合成名单",
        names.Select((name, i) => new Student
        {
            Name = name,
            Gender = i % 2 == 0 ? Gender.Female : Gender.Male,
            Number = $"S{i + 1:D4}",
            Height = 150 + i,
            Vision = 4.5 + i * 0.1,
            Score = 500 + i * 3,
            Notes = $"备注{i + 1}",
            OriginalGroup = $"原组{i % 3 + 1}",
        }).ToList(),
        Array.Empty<string>());

    private static SeatGridRosterPick Pick(
        string id, string name, int version, IEnumerable<string> names, SeatGridRosterFields fields) => new(
        id, name, version,
        names.Select((n, i) => new SeatGridRosterStudent(
            i + 1, n, i % 2 == 0 ? "女" : "男", $"S{i + 1:D4}",
            fields.Height ? 160 + i : null,
            fields.Vision ? 5.0 : null,
            fields.Score ? 600 : null,
            fields.Notes ? "快照备注" : null,
            fields.OriginalGroup ? "一组" : null)).ToList(),
        fields);

    private static SeatGridRosterInput Input(
        int version, string name, IEnumerable<string> names, SeatGridRosterFields fields) => new(
        version, name,
        fields.Height ? new[] { "height" } : Array.Empty<string>(),
        names.Select((n, i) => new SeatGridRosterStudent(
            i + 1, n, "女", $"S{i + 1:D4}",
            fields.Height ? 160 + i : null, null, null, null, null)).ToList());

    [Fact]
    public void CanUseCloud_False_WithoutBridge()
    {
        var vm = new SeatGridViewModel();
        Assert.False(vm.CanUseCloud);
        Assert.False(vm.HasLinkedRoster);
        Assert.DoesNotContain("上传到云端", vm.CloseWarning);
    }

    [Fact]
    public void CanUseCloud_False_WithNullBridge()
    {
        var vm = new SeatGridViewModel();
        vm.AttachCloud(new NullSeatGridCloudBridge());
        Assert.False(vm.CanUseCloud);
        Assert.True(vm.ShowCloudAccountLink);
        Assert.DoesNotContain("上传到云端", vm.CloseWarning);
    }

    [Fact]
    public void CanUseCloud_True_WhenBridgeAvailable()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        Assert.True(vm.CanUseCloud);
        Assert.False(vm.ShowCloudAccountLink);
        Assert.Contains("上传到云端", vm.CloseWarning);
    }

    [Fact]
    public void BuildCloudDocument_ThrowsInExamMode()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        vm.ApplySharedRoster(Pick("r1", "合成名单", 1, NamesA, SeatGridRosterFields.None));
        vm.EnterExamMode(new ExamLayoutOptions());

        var ex = Assert.Throws<SeatGridException>(() => vm.BuildCloudDocument("座位表"));
        Assert.Contains("考场快照", ex.Message);
    }

    [Fact]
    public void BuildCloudDocument_ThrowsWithLayoutOverride()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        vm.ApplySharedRoster(Pick("r1", "合成名单", 1, NamesA, SeatGridRosterFields.None));

        var settings = vm.ToSettings();
        settings.LayoutOverride = new SeatChartLayout(1, 1, 1);
        var examConfig = new SeatGridConfig(
            settings, vm.GetModels(), vm.GetCoreAssignments(), new List<string>());

        var ex = Assert.Throws<SeatGridException>(() => vm.BuildCloudDocument("座位表", examConfig));
        Assert.Contains("考场快照", ex.Message);
    }

    [Fact]
    public void BuildCloudDocument_ThrowsWithoutLinkedRoster()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });

        var ex = Assert.Throws<SeatGridException>(() => vm.BuildCloudDocument("座位表"));
        Assert.Equal("这份座位表还没有关联共享名单。请先把当前名单保存到共享名单库。", ex.Message);
    }

    [Fact]
    public void BuildCloudDocument_EmbedsLinkAndOnlySelectedFields()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        var fields = new SeatGridRosterFields(true, false, false, false, false);
        vm.ApplySharedRoster(Pick("roster-42", "合成八年级名单", 7, NamesA, fields));

        var doc = vm.BuildCloudDocument("合成座位表");
        Assert.Equal("roster-42", doc.RosterId);
        Assert.Equal(7, doc.RosterVersion);
        Assert.Equal("合成座位表", doc.Name);
        Assert.NotNull(doc.RosterSnapshot);
        Assert.Equal(new[] { "height" }, doc.RosterSnapshot!.FieldsPresent);
        Assert.All(doc.RosterSnapshot.Students, s =>
        {
            Assert.NotNull(s.Height);
            Assert.Null(s.Vision);
            Assert.Null(s.Score);
            Assert.Null(s.Notes);
            Assert.Null(s.OriginalGroup);
        });
    }

    [Fact]
    public void ApplySharedRoster_ReplacesStudentsClearsConstraintsAndSetsLink()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        vm.SelectSeat(vm.Seats.First(s => !s.IsEmpty));
        vm.ToggleSelectedSeatPin();
        Assert.True(vm.Constraints.Pins.Count > 0);

        var fields = new SeatGridRosterFields(false, false, true, false, false);
        vm.ApplySharedRoster(Pick("r2", "合成名单B", 3, NamesB, fields));

        Assert.Equal(NamesB.Length, vm.StudentCount);
        Assert.All(vm.AllStudents, s => Assert.Contains(s.Name, NamesB));
        Assert.Empty(vm.Constraints.Pins);
        Assert.True(vm.AssignedCount > 0);
        Assert.True(vm.HasLinkedRoster);
        Assert.Equal(fields, vm.LinkedFields);
        Assert.Contains("v3", vm.StatusText);
    }

    [Fact]
    public void ApplyCloudDocument_RestoresSeatsAndReportsVersionWarning()
    {
        var source = new SeatGridViewModel();
        source.LoadRoster(Roster(NamesA));
        source.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        source.ApplySharedRoster(Pick("r1", "合成名单", 2, NamesA, SeatGridRosterFields.None));
        var doc = source.BuildCloudDocument("合成座位表");

        var vm = new SeatGridViewModel();
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        var roster = Input(5, "合成名单", NamesA, SeatGridRosterFields.None);
        vm.ApplyCloudDocument(doc, roster, usedSnapshot: false);

        Assert.Equal(NamesA.Length, vm.StudentCount);
        Assert.Equal(source.AssignedCount, vm.AssignedCount);
        Assert.True(vm.HasLinkedRoster);
        Assert.Contains("版本 2", vm.StatusText);
        Assert.Contains("版本 5", vm.StatusText);
    }

    [Fact]
    public void ApplyCloudDocument_FallsBackToSnapshot()
    {
        var source = new SeatGridViewModel();
        source.LoadRoster(Roster(NamesA));
        source.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        source.ApplySharedRoster(Pick("r1", "合成名单", 2, NamesA, SeatGridRosterFields.None));
        var doc = source.BuildCloudDocument("合成座位表");

        var vm = new SeatGridViewModel();
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        vm.ApplyCloudDocument(doc, null, usedSnapshot: true, new[] { "名单暂时读不到" });

        Assert.Equal(NamesA.Length, vm.StudentCount);
        Assert.Contains("名单暂时读不到", vm.StatusText);
    }

    [Fact]
    public void LoadRoster_And_ApplyConfig_ClearRosterLink()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        vm.ApplySharedRoster(Pick("r1", "合成名单", 1, NamesA, SeatGridRosterFields.None));
        Assert.True(vm.HasLinkedRoster);

        vm.LoadRoster(Roster(NamesB));
        Assert.False(vm.HasLinkedRoster);

        vm.ApplySharedRoster(Pick("r2", "合成名单B", 1, NamesB, SeatGridRosterFields.None));
        Assert.True(vm.HasLinkedRoster);
        vm.ApplyConfig(new SeatGridConfig(
            vm.ToSettings(), vm.GetModels(), vm.GetCoreAssignments(), new List<string>()));
        Assert.False(vm.HasLinkedRoster);
    }

    [Fact]
    public void LinkSharedRoster_AllowsBuildCloudDocumentWithoutResettingSeatsOrConstraints()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(NamesA));
        vm.AttachCloud(new FakeSeatGridCloudBridge { Available = true });
        vm.SelectSeat(vm.Seats.First(s => !s.IsEmpty));
        vm.ToggleSelectedSeatPin();
        var pinnedCount = vm.Constraints.Pins.Count;
        var assignedBefore = vm.AssignedCount;

        var fields = new SeatGridRosterFields(true, false, false, false, false);
        vm.LinkSharedRoster("r-uploaded", "合成名单", 1, fields);

        Assert.True(vm.HasLinkedRoster);
        Assert.Equal("r-uploaded", vm.LinkedRosterId);
        Assert.Equal(fields, vm.LinkedFields);
        Assert.Equal(NamesA.Length, vm.StudentCount);
        Assert.Equal(assignedBefore, vm.AssignedCount);
        Assert.Equal(pinnedCount, vm.Constraints.Pins.Count);

        var doc = vm.BuildCloudDocument("合成座位表");
        Assert.Equal("r-uploaded", doc.RosterId);
        Assert.Equal(1, doc.RosterVersion);
        Assert.Equal(new[] { "height" }, doc.RosterSnapshot!.FieldsPresent);
    }

    private sealed class RoundTripAccount : ICloudAccountService
    {
        public CloudAccount? Current { get; } =
            new("synthetic-user", "示例教师", Array.Empty<string>(),
                SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow);
        public bool IsSignedIn => Current is not null;
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("synthetic-token");

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RoundTripSettings : ICloudSyncSettingsStore
    {
        public CloudSyncSettings Settings { get; private set; } = new(Enabled: true, ConsentVersion: 1);
        public CloudSyncSettings Load() => Settings;
        public void Save(CloudSyncSettings settings) => Settings = settings;
        public string Path => "memory";
    }

    [Fact]
    public async Task PullDocument_RememberedIdentity_SurvivesRestart()
    {
        var account = new RoundTripAccount();
        var settings = new RoundTripSettings();
        var seatStore = new MemoryDocumentStore();
        var rosters = new SectlSharedRosterSource(new MemoryDocumentStore());
        var bridge = new SeatGridCloudBridge(account, rosters, seatStore, settings);

        var created = await bridge.UploadDocumentAsync(
            new SeatGridCloudDocument { Name = "合成座位表" }, null, 0, CancellationToken.None);
        var pull = await bridge.DownloadDocumentAsync(created.Entry.Id, CancellationToken.None);
        Assert.NotNull(pull);

        bridge.RememberDocument(pull!.DocumentId, pull.Revision);

        var restarted = new SeatGridCloudBridge(account, rosters, seatStore, settings);
        var (id, revision) = restarted.GetLastDocument();
        Assert.Equal(created.Entry.Id, id);
        Assert.Equal(1, revision);
    }

    [Fact]
    public async Task ListRosters_LoadFailed_SurfacesErrorInStatusText()
    {
        var account = new RoundTripAccount();
        var settings = new RoundTripSettings();
        var store = new MemoryDocumentStore();
        store.Seed("broken", "{\"v\":1,\"kind\":\"teachcraft.roster.v1\"}");
        store.FailGetFor("broken");
        var bridge = new SeatGridCloudBridge(
            account, new SectlSharedRosterSource(store), new MemoryDocumentStore(), settings);

        var entries = await bridge.ListRostersAsync(CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("broken", entry.RosterId);
        Assert.Contains("读取失败", entry.StatusText);
    }

    private sealed class FakeSeatGridCloudBridge : ISeatGridCloudBridge
    {
        public bool Available { get; set; }
        public bool IsAvailable => Available;
        public bool IsSignedIn => Available;
        public string StatusText => Available ? "已登录" : "未接入";
        public string RosterConsentText => "合成同意文案";
        public bool AutoSyncEnabled { get; set; }
        public bool IsAutoSyncEnabled => AutoSyncEnabled;
        public void SetAutoSyncEnabled(bool value) => AutoSyncEnabled = value;
        public SeatGridRosterFields LastRosterFields { get; set; } = SeatGridRosterFields.None;
        public void RememberRosterFields(SeatGridRosterFields fields) => LastRosterFields = fields;

        public string BuildSeatConsentText(string rosterName, int rosterVersion, string fieldsSummary)
            => $"{rosterName} v{rosterVersion} {fieldsSummary}";

        public event EventHandler? StateChanged;

        public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

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
            => Task.FromResult(new SeatGridCloudSaveResult(
                new SeatGridCloudEntry(replacingId ?? "new", document.Name, DateTimeOffset.UtcNow, 100, 1),
                expectedRevision > 0));

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct) => Task.CompletedTask;

        public string? RememberedId { get; private set; }
        public int RememberedRevision { get; private set; }
        public void RememberDocument(string? id, int revision)
        {
            RememberedId = id;
            RememberedRevision = revision;
        }
    }
}
