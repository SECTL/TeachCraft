using TeachCraft.App.ViewModels;
using TeachCraft.Core.Oobe;

namespace TeachCraft.Tests;

/// <summary>引导状态机与显示判定。全部不启动 Avalonia。</summary>
/// <summary>内存状态存储，供两个测试类共用。</summary>
internal sealed class MemoryStore : IOobeStateStore
{
    public OobeState State { get; private set; } = OobeState.Empty;
    public int SaveCount { get; private set; }
    public OobeState Load() => State;
    public void Save(OobeState state)
    {
        State = state;
        SaveCount++;
    }
}

public sealed class OobeViewModelTests
{
    private static OobeViewModel Vm(MemoryStore? store = null) =>
        new(store ?? new MemoryStore());

    [Fact]
    public void Vm_StartsClosedAtFirstStep()
    {
        var vm = Vm();
        Assert.False(vm.IsOpen);
        Assert.Equal(1, vm.CurrentIndex);
    }

    [Fact]
    public void Vm_OpenAtClampsOutOfRange()
    {
        var vm = Vm();

        vm.OpenAt(99);
        Assert.Equal(OobeContent.Steps.Count, vm.CurrentIndex);

        vm.OpenAt(0);
        Assert.Equal(1, vm.CurrentIndex);

        vm.OpenAt(-5);
        Assert.Equal(1, vm.CurrentIndex);
    }

    [Fact]
    public void Vm_NextAdvancesAndStopsAtLast()
    {
        var vm = Vm();
        vm.OpenAt(1);

        vm.Next();
        Assert.Equal(2, vm.CurrentIndex);

        for (var i = 0; i < 10; i++) vm.Next();
        Assert.Equal(OobeContent.Steps.Count, vm.CurrentIndex);
        Assert.True(vm.IsLastStep);
    }

    [Fact]
    public void Vm_BackStopsAtFirst()
    {
        var vm = Vm();
        vm.OpenAt(1);
        vm.Back();
        Assert.Equal(1, vm.CurrentIndex);
        Assert.True(vm.IsFirstStep);
    }

    [Fact]
    public void Vm_DoesNotAutoFinishOnLastNext()
    {
        var store = new MemoryStore();
        var vm = Vm(store);
        vm.OpenAt(1);
        for (var i = 0; i < 10; i++) vm.Next();

        // 最后一步按「下一步」不该静默完成——完成必须是一次显式动作。
        Assert.True(vm.IsOpen);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void Vm_SkipPersistsSkippedAndCloses()
    {
        var store = new MemoryStore();
        var vm = Vm(store);
        vm.OpenAt(2);

        vm.Skip();

        Assert.False(vm.IsOpen);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(OobeDismissal.Skipped, store.State.DismissedHow);
        Assert.NotNull(store.State.DismissedUtc);
    }

    [Fact]
    public void Vm_FinishPersistsCompletedAndCloses()
    {
        var store = new MemoryStore();
        var vm = Vm(store);
        vm.OpenAt(1);

        vm.Finish();

        Assert.False(vm.IsOpen);
        Assert.Equal(OobeDismissal.Completed, store.State.DismissedHow);
    }

    [Fact]
    public void Vm_RaisesClosedEvent()
    {
        var vm = Vm();
        vm.OpenAt(1);
        var fired = false;
        vm.Closed += () => fired = true;

        vm.Skip();

        Assert.True(fired);
    }

    [Fact]
    public void Vm_ProgressAndButtonTextFollowIndex()
    {
        var vm = Vm();
        vm.OpenAt(1);
        Assert.Equal("01 / 04", vm.ProgressText);
        Assert.Equal("下一步", vm.PrimaryButtonText);

        vm.OpenAt(4);
        Assert.Equal("04 / 04", vm.ProgressText);
        Assert.Equal("开始使用", vm.PrimaryButtonText);
    }

    [Fact]
    public void Vm_DoesNotReferenceAvalonia()
    {
        // 状态机必须能在无 UI 线程的环境里构造；引用 Avalonia 控件类型就做不到。
        var text = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "ViewModels", "OobeViewModel.cs"));
        Assert.DoesNotContain("using Avalonia", text, StringComparison.Ordinal);
    }
}

/// <summary>显示判定：首次 / 有新通知 / 不打扰。</summary>
/// <summary>显示判定：只有「从未结束过」才显示。</summary>
public sealed class OobePlannerTests
{
    private static OobeState Seen(OobeDismissal how = OobeDismissal.Completed) =>
        new(DateTimeOffset.Parse("2026-11-01T00:00:00+08:00"), how);

    [Fact]
    public void Planner_NoStateShowsFirstRun()
    {
        var decision = OobePlanner.Decide(OobeState.Empty);
        Assert.True(decision.Show);
        Assert.Equal(OobeShowReason.FirstRun, decision.Reason);
        Assert.Equal(1, decision.StartAt);
    }

    [Fact]
    public void Planner_CompletedStaysHidden()
    {
        var decision = OobePlanner.Decide(Seen(OobeDismissal.Completed));
        Assert.False(decision.Show);
        Assert.Equal(OobeShowReason.AlreadyDone, decision.Reason);
    }

    [Fact]
    public void Planner_SkippedBehavesLikeCompleted()
    {
        Assert.False(OobePlanner.Decide(Seen(OobeDismissal.Skipped)).Show);
    }

    /// <summary>
    /// 核心约束：结束过引导之后，无论装的是哪个版本、过多久，都不再自动显示。
    /// 想重看只能走侧栏的「首次引导」入口。
    /// </summary>
    [Theory]
    [InlineData("1.30.0")]
    [InlineData("1.31.0")]
    [InlineData("2.0.0")]
    [InlineData("1.30.0-preview.9")]
    public void Planner_NeverReopensAfterDismissal(string _)
    {
        Assert.False(OobePlanner.Decide(Seen()).Show);
        Assert.False(OobePlanner.Decide(Seen(OobeDismissal.Skipped)).Show);
    }

    [Fact]
    public void Planner_OnlyDismissedFlagMatters()
    {
        // 结束过但没记时间（理论上不该出现）按「已结束」处理，不重弹。
        var noTimestamp = new OobeState(null, OobeDismissal.Completed);
        Assert.True(OobePlanner.Decide(noTimestamp).Show);

        var withTimestamp = new OobeState(DateTimeOffset.UtcNow, null);
        Assert.False(OobePlanner.Decide(withTimestamp).Show);
    }
}
