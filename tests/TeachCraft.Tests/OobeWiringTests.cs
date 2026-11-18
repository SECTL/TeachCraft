using TeachCraft.App.ViewModels;
using TeachCraft.Core.Oobe;

namespace TeachCraft.Tests;

/// <summary>
/// OOBE 布线级回归（第二轮审计 P1-6）：
/// App 层此前在正常启动时也传 showOobe=true，引导每次启动都强制弹出，
/// OobePlanner「结束过就不再打扰」的判定根本轮不到执行。
///
/// 修好后正常启动传 null，判定权交回 MainViewModel——由 OobeStateStore 的真实状态决定。
/// 这两条断言钉住取值链的最后一段：null 传入 → 按 store 状态决定 → 遮罩开 / 不开。
/// App.axaml.cs 的组合表达式本身（四种取值）由走查通道取证，本测试锁其语义结果。
/// </summary>
public sealed class OobeWiringTests
{
    [Fact]
    public void NormalStart_CompletedStore_OverlayStaysClosed()
    {
        // 用户已经看过（或跳过）引导，正常启动不再弹——修复前此处必红：
        // 正常启动误传 showOobe=true，无论 store 记了什么都会被强制打开。
        var store = new MemoryStore();
        store.Save(new OobeState(DateTimeOffset.Parse("2026-11-18T08:00:00+08:00"), OobeDismissal.Completed));

        var vm = new MainViewModel(oobeStateStore: store, showOobe: null);

        Assert.False(vm.Oobe.IsOpen);
    }

    [Fact]
    public void NormalStart_EmptyStore_OverlayOpensFirstRun()
    {
        // 从未结束过引导的正常启动要弹（首次运行体验），判定权交回 store 之后不能反向误伤。
        var store = new MemoryStore();

        var vm = new MainViewModel(oobeStateStore: store, showOobe: null);

        Assert.True(vm.Oobe.IsOpen);
        Assert.Equal(1, vm.Oobe.CurrentIndex);
        Assert.Equal(OobeShowReason.FirstRun, vm.Oobe.Reason);
    }
}
