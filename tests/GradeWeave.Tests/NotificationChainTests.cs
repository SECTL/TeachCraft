using System.ComponentModel;
using GradeWeave.App;
using GradeWeave.Core.Models;

namespace GradeWeave.Tests;

/// <summary>
/// 计算属性的通知链回归（第二轮审计 P3-6 / P3-7 / P3-10）。
///
/// 这三条的病灶是同一个形状：界面绑的是计算属性，值本身没变过对象引用，
/// 只改底层集合时 Avalonia 不会重新取值——界面上留着上一份数据的结论。
/// 断言直接盯 PropertyChanged 事件名，不依赖 Avalonia 运行时。
/// </summary>
public sealed class NotificationChainTests
{
    [Fact]
    public void StatusText_Change_RaisesBusyOverlayTitle()
    {
        // 遮罩标题读 StatusText。导出入口置忙前会写「正在导出…」，
        // 若不转播通知，遮罩上永远是窗口打开时那句旧状态（修复前必红）。
        var viewModel = new GradeWeaveViewModel();
        var raised = Track(viewModel);

        viewModel.SetStatus("正在导出 Excel 课表…");

        Assert.Contains(nameof(GradeWeaveViewModel.BusyOverlayTitle), raised);
        Assert.Equal("正在导出 Excel 课表…", viewModel.BusyOverlayTitle);
    }

    [Fact]
    public void LoadImportResult_SecondImport_RaisesImportWarningSummary()
    {
        // 第二次导入后折叠头要换成新条数；只通知 HasImportWarnings 时标题停在上一次（修复前必红）。
        var viewModel = new GradeWeaveViewModel();
        var first = DemoImportResult(warnings: ["第一次导入的提醒"]);
        viewModel.LoadImportResult(first, "第一次.xlsx");
        Assert.Equal("导入提醒（1 条）", viewModel.ImportWarningSummary);

        var second = DemoImportResult(warnings: ["第二条", "第三条"]);
        var raised = Track(viewModel);
        viewModel.LoadImportResult(second, "第二次.xlsx");

        Assert.Contains(nameof(GradeWeaveViewModel.ImportWarningSummary), raised);
        Assert.Equal("导入提醒（2 条）", viewModel.ImportWarningSummary);
    }

    [Fact]
    public void ToggleResearch_RaisesResearchOverview()
    {
        // 教研总览是 ToolTip 的计算属性：点格子改了教研分布却不发通知，
        // 悬浮提示会一直停在窗口打开时那一份（修复前必红）。
        var viewModel = new GradeWeaveViewModel();
        viewModel.LoadDemoProject();
        viewModel.SelectedResearchSubject = viewModel.ResearchSubjects.First();
        var raised = Track(viewModel);

        viewModel.ToggleResearch(1, 1);

        Assert.Contains(nameof(GradeWeaveViewModel.ResearchOverview), raised);
    }

    [Fact]
    public void ZeroHourSubjectsHint_ListsOnlySubjectsThatCannotBeScheduled()
    {
        // 周课时 0 且没有走班组的学科不会排进课表，05 步必须点名（P3-10）。
        var viewModel = new GradeWeaveViewModel();
        viewModel.LoadDemoProject();
        Assert.False(viewModel.HasZeroHourSubjects);

        viewModel.Project.Subjects.First(subject => subject.Id == "s9").WeeklyHours = 0;
        viewModel.RefreshAfterAssignmentEdit();

        Assert.True(viewModel.HasZeroHourSubjects);
        Assert.Contains("劳动", viewModel.ZeroHourSubjectsHint, StringComparison.Ordinal);
        Assert.Contains("不会排入课表", viewModel.ZeroHourSubjectsHint, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroHourSubjectsHint_IgnoresLayeredOnlySubject()
    {
        // 只走分层走班的学科，课时记在走班组上：周课时 0 是正常的，不能报成「不会排入课表」。
        var viewModel = new GradeWeaveViewModel();
        viewModel.LoadDemoProject();
        var english = viewModel.Project.Subjects.First(subject => subject.Id == "s3");
        Assert.Contains(viewModel.Project.LayeredGroups, group => group.SubjectId == english.Id);

        english.WeeklyHours = 0;
        viewModel.RefreshAfterAssignmentEdit();

        Assert.DoesNotContain("英语", viewModel.ZeroHourSubjectsHint, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroHourSubjectsHint_RaisesOnProjectReload()
    {
        // 导入 / 确认作息都会换一份工程；提示必须跟着刷新，不能停在上一份的结论。
        var viewModel = new GradeWeaveViewModel();
        var raised = Track(viewModel);
        viewModel.LoadImportResult(DemoImportResult(warnings: []), "导入.xlsx");

        Assert.Contains(nameof(GradeWeaveViewModel.HasZeroHourSubjects), raised);
    }

    private static List<string> Track(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is { } name) names.Add(name);
        };
        return names;
    }

    private static WeaveImportResult DemoImportResult(IReadOnlyList<string> warnings)
    {
        var project = WeaveSamples.CreateDemo(2);
        var result = new WeaveImportResult { Project = project };
        result.Warnings.AddRange(warnings);
        return result;
    }
}
