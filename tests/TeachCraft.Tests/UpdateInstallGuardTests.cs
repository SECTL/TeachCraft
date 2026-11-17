using TeachCraft.App.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 安装更新前的未保存窗口扫描（审计 P1-3）。
///
/// 判定逻辑抽成不依赖 Avalonia 的静态类，所以能在无 UI 线程的测试里直接断言；
/// 窗口枚举留在 App 层，由走查参数在真实运行中覆盖。
/// </summary>
public sealed class UpdateInstallGuardTests
{
    [Fact]
    public void FindUnsaved_ReturnsOnlyUnsavedTitles()
    {
        var result = UnsavedWindowGuard.FindUnsaved(new[]
        {
            ("归位 · 座位表", true),
            ("时序 · 课程表", false),
            ("竞名 · 运动会报名", true),
        });

        Assert.Equal(new[] { "归位 · 座位表", "竞名 · 运动会报名" }, result);
    }

    [Fact]
    public void FindUnsaved_EmptyInputYieldsEmpty()
    {
        Assert.Empty(UnsavedWindowGuard.FindUnsaved(Array.Empty<(string, bool)>()));
    }

    [Fact]
    public void FindUnsaved_AllCleanYieldsEmpty()
    {
        var result = UnsavedWindowGuard.FindUnsaved(new[] { ("归位", false), ("竞名", false) });
        Assert.Empty(result);
    }

    [Fact]
    public void HasUnsavedWork_NullOrUnknownContextIsFalse()
    {
        Assert.False(UnsavedWindowGuard.HasUnsavedWork(null));
        Assert.False(UnsavedWindowGuard.HasUnsavedWork(new object()));
        Assert.False(UnsavedWindowGuard.HasUnsavedWork("not a view model"));
    }
}
