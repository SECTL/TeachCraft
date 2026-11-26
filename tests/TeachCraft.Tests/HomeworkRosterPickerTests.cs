using Avalonia.Controls;
using Homework.App.Cloud;
using Homework.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>
/// 共享名单选择器的状态机。全部用内存假桥，不碰账号、不联网。
/// 姓名与班级均为合成数据（苏晚晴 / 陈屿 / 八年级1班 等）。
/// </summary>
public sealed class HomeworkRosterPickerTests
{
    private sealed class FakeBridge : IHomeworkRosterBridge
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsSignedIn { get; set; }
        public event EventHandler? StateChanged;

        public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public FakeBridge() => IsSignedIn = true;

        public IReadOnlyList<HomeworkRosterEntry> Entries { get; set; } = Array.Empty<HomeworkRosterEntry>();
        public Exception? ListError { get; set; }
        public RosterImportResult DownloadResult { get; set; } = new([], [], [], 0);
        public IReadOnlyList<string>? LastDownloadedIds { get; private set; }
        public int AccountSettingsCalls { get; private set; }

        public Task<bool> EnsureSignedInAsync(Window owner) => Task.FromResult(IsAvailable);

        public Task<IReadOnlyList<HomeworkRosterEntry>> ListRostersAsync(CancellationToken ct)
        {
            if (ListError is not null) throw ListError;
            return Task.FromResult(Entries);
        }

        public Task<RosterImportResult> DownloadAsync(IReadOnlyList<string> rosterIds, CancellationToken ct)
        {
            LastDownloadedIds = rosterIds;
            return Task.FromResult(DownloadResult);
        }

        public void OpenAccountSettings(Window owner) => AccountSettingsCalls++;
    }

    private static HomeworkRosterEntry Entry(string id, string name, int students = 10) =>
        new(id, name, 1, new DateTimeOffset(2026, 11, 24, 9, 12, 0, TimeSpan.Zero),
            students, "缺少可选字段：身高");

    [Fact]
    public async Task DuplicateName_SecondRowDisabledAndCannotBeSelected()
    {
        var bridge = new FakeBridge
        {
            Entries =
            [
                Entry("a", "八年级1班", 10),
                Entry("b", "八年级1班", 99),
                Entry("c", "九年级2班", 20),
            ],
        };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(vm.Rows[0].CanSelect);
        Assert.False(vm.Rows[1].CanSelect);
        Assert.Equal("同名班级只能选一份", vm.Rows[1].DisabledReason);
        Assert.True(vm.Rows[2].CanSelect);

        vm.Rows[1].IsSelected = true;
        Assert.False(vm.Rows[1].IsSelected);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task DuplicateName_WithSpaceOrCaseVariants_SecondRowDisabled()
    {
        var bridge = new FakeBridge
        {
            Entries =
            [
                Entry("a", "八年级1班", 10),
                Entry("b", "  八年级1班  ", 99),
                Entry("c", "Class A", 20),
                Entry("d", "class a", 30),
            ],
        };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(vm.Rows[0].CanSelect);
        Assert.False(vm.Rows[1].CanSelect);
        Assert.Equal("同名班级只能选一份", vm.Rows[1].DisabledReason);
        Assert.True(vm.Rows[2].CanSelect);
        Assert.False(vm.Rows[3].CanSelect);
    }

    [Fact]
    public async Task SelectAll_SkipsDisabledRows_AndComputesTotals()
    {
        var bridge = new FakeBridge
        {
            Entries =
            [
                Entry("a", "八年级1班", 10),
                Entry("b", "八年级1班", 99),
                Entry("c", "九年级2班", 20),
            ],
        };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);

        vm.SelectAll();

        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(30, vm.TotalStudents);
        Assert.True(vm.CanPull);
        Assert.Equal("将导入 2 个班、30 名学生。当前名单与已做的匹配会被替换。", vm.ConfirmText);
        Assert.Equal(["a", "c"], vm.SelectedIds.ToArray());

        var result = await vm.PullAsync(CancellationToken.None);
        Assert.Same(bridge.DownloadResult, result);
        Assert.Equal(["a", "c"], bridge.LastDownloadedIds!.ToArray());
    }

    [Fact]
    public async Task ClearSelection_ResetsCountsAndDisablesPull()
    {
        var bridge = new FakeBridge { Entries = [Entry("a", "八年级1班", 10)] };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);

        vm.SelectAll();
        Assert.True(vm.CanPull);

        vm.ClearSelection();

        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal(0, vm.TotalStudents);
        Assert.False(vm.CanPull);
        Assert.Null(await vm.PullAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EmptyLibrary_ShowsEmptyState()
    {
        var vm = new HomeworkRosterPickerViewModel(new FakeBridge());
        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(vm.IsEmpty);
        Assert.False(vm.ShowList);
        Assert.Contains("共享名单库还是空的", vm.StatusText);
        Assert.False(vm.CanPull);
    }

    [Fact]
    public async Task ListFailure_ShowsErrorAndDisablesPull()
    {
        var bridge = new FakeBridge { ListError = new InvalidOperationException("读取失败") };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(vm.HasError);
        Assert.False(vm.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));
        Assert.False(vm.CanPull);
    }

    [Fact]
    public async Task ListFailure_ClearsStaleRowsAndDisablesPull()
    {
        var bridge = new FakeBridge { Entries = [Entry("a", "八年级1班", 10)] };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);
        Assert.Single(vm.Rows);
        vm.SelectAll();
        Assert.True(vm.CanPull);

        bridge.ListError = new InvalidOperationException("list failed");
        await vm.RefreshAsync(CancellationToken.None);

        Assert.True(vm.HasError);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal(0, vm.TotalStudents);
        Assert.False(vm.CanPull);
        Assert.Null(await vm.PullAsync(CancellationToken.None));
        Assert.Contains("读取共享名单失败", vm.StatusText);
    }

    [Fact]
    public async Task UnavailableBridge_ShowsSignInHint()
    {
        var bridge = new FakeBridge { IsAvailable = false, IsSignedIn = false };
        var vm = new HomeworkRosterPickerViewModel(bridge);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.Equal("登录后可以拉取共享名单", vm.StatusText);
        Assert.Empty(vm.Rows);
        Assert.False(vm.CanPull);
    }

    [Fact]
    public void BridgeInterface_HasNoUploadSaveOrPutApi()
    {
        var names = typeof(IHomeworkRosterBridge)
            .GetMethods()
            .Concat(typeof(IHomeworkRosterBridge).GetProperties().Select(p => p.GetMethod!))
            .Select(m => m.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Upload", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Save", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Put", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NullBridge_IsNoOp()
    {
        var bridge = NullHomeworkRosterBridge.Instance;
        Assert.False(bridge.IsAvailable);
        Assert.Empty(await bridge.ListRostersAsync(CancellationToken.None));
        Assert.Empty((await bridge.DownloadAsync(["a"], CancellationToken.None)).Students);
        Assert.False(await bridge.EnsureSignedInAsync(null!));
        bridge.OpenAccountSettings(null!);
    }

    [Fact]
    public async Task SignedInWithSyncOff_ShowsEnableSyncHint_AndAutoRefreshesWhenEnabled()
    {
        var bridge = new FakeBridge
        {
            IsSignedIn = true,
            IsAvailable = false,
            Entries = [Entry("r1", "八年级1班", 36)],
        };
        using var vm = new HomeworkRosterPickerViewModel(bridge);

        await vm.RefreshAsync(CancellationToken.None);
        Assert.Contains("开启云同步", vm.StatusText);
        Assert.False(vm.IsAvailable);

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeworkRosterPickerViewModel.IsBusy) && !vm.IsBusy && vm.Rows.Count > 0)
                refreshed.TrySetResult();
        };

        bridge.IsAvailable = true;
        bridge.RaiseStateChanged();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsAvailable);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("八年级1班", row.Name);
    }
}
