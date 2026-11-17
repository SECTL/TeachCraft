using GradeStrip.App.ViewModels;

namespace TeachCraft.Tests;

/// <summary>
/// 分晓的输出保护与关窗门控（审计 P0-2 / P0-1 最低版）。
///
/// 为什么测静态守卫而不是走 ExportAsync/ImportAsync 全流程：
/// 分晓的导入导出都包在 RunBusyAsync 里，而它第一件事就是
/// `Dispatcher.UIThread.InvokeAsync(...)`——无 UI 线程的测试环境会永久挂起。
/// 因此把守卫抽成纯函数单独断言；完整链路用走查参数在真实运行中验证。
///
/// 为什么放 TeachCraft.Tests 而不是 GradeStrip.Tests：后者只引用
/// GradeStrip.Core，看不到 App 层；本工程已按同一模式引用 MeetRoster.App。
/// </summary>
public sealed class GradeStripExportGuardTests
{
    [Fact]
    public void OverwriteGuard_RejectsExportOntoImportedSource()
    {
        var source = Path.Combine(Path.GetTempPath(), "exam-sheet.xlsx");
        Assert.True(MainViewModel.WouldOverwriteImportedSource(source, new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_IsCaseInsensitive()
    {
        var source = Path.Combine(Path.GetTempPath(), "Exam-Sheet.xlsx");
        var exportTo = Path.Combine(Path.GetTempPath(), "exam-sheet.XLSX");
        Assert.True(MainViewModel.WouldOverwriteImportedSource(exportTo, new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_NormalizesRelativePaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        var source = Path.Combine(cwd, "relative-source.xlsx");
        Assert.True(MainViewModel.WouldOverwriteImportedSource("relative-source.xlsx", new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_AllowsDistinctTarget()
    {
        var source = Path.Combine(Path.GetTempPath(), "exam-a.xlsx");
        var exportTo = Path.Combine(Path.GetTempPath(), "exam-a-成绩条.xlsx");
        Assert.False(MainViewModel.WouldOverwriteImportedSource(exportTo, new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_RejectsWhenAnySessionCollides()
    {
        // 多场考试：只要命中任意一场的源文件就要拒绝。
        var first = Path.Combine(Path.GetTempPath(), "exam-1.xlsx");
        var second = Path.Combine(Path.GetTempPath(), "exam-2.xlsx");
        Assert.True(MainViewModel.WouldOverwriteImportedSource(second, new[] { first, second }));
    }

    [Fact]
    public void OverwriteGuard_IgnoresBlankSourcePaths()
    {
        var exportTo = Path.Combine(Path.GetTempPath(), "out.xlsx");
        Assert.False(MainViewModel.WouldOverwriteImportedSource(exportTo, new[] { "", "  " }));
    }

    /// <summary>
    /// 关窗门控（审计 P0-1 最低版）。走查工具集只截图、不碰窗口生命周期，
    /// 所以门控抽成纯函数单测；走查通道必须放行，否则一次截图会被确认框卡住。
    /// </summary>
    [Theory]
    [InlineData(true, false, false, true)]   // 有数据、未确认过、非走查 → 拦一次
    [InlineData(false, false, false, false)] // 无数据 → 不拦
    [InlineData(true, true, false, false)]   // 已确认过（第二次 Close）→ 放行
    [InlineData(true, false, true, false)]   // 走查参数启动 → 放行
    public void ShouldConfirmOnClose_GatesAsExpected(
        bool hasData, bool closeConfirmed, bool isWalkthrough, bool expected)
    {
        Assert.Equal(expected,
            GradeStrip.App.Views.MainWindow.ShouldConfirmOnClose(hasData, closeConfirmed, isWalkthrough));
    }
}
