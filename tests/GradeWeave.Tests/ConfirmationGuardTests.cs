using GradeWeave.App;
using GradeWeave.Core.Models;
using GradeWeave.Core.Timetable;

namespace GradeWeave.Tests;

/// <summary>
/// 临时活动删除确认与作息预设替换确认的回归（审计 P2-7 / P2-8，上轮 T10②③ 回补）。
/// 两个窗口都不做 headless 构造：可测面放在 VM 纯状态与注入钩子上。
/// </summary>
public sealed class ConfirmationGuardTests
{
    // --------------------------- T14 活动删除确认 ---------------------------

    [Fact]
    public void DeleteConfirmForReplaceActivityWarnsOriginalLessonsNotRestored()
    {
        var (title, message) = ActivityEditorWindow.DescribeDeleteConfirm(true, "大扫除");
        Assert.Contains("被移除的原课不会自动恢复", message, StringComparison.Ordinal);
        Assert.Contains("大扫除", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteConfirmForOverlayActivityStaysLight()
    {
        var (title, message) = ActivityEditorWindow.DescribeDeleteConfirm(false, "书法课");
        Assert.DoesNotContain("不会自动恢复", message, StringComparison.Ordinal);
        Assert.Contains("书法课", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteConfirmFollowsExistingActivityModeNotFormSelection()
    {
        // 删除作用于打开时的存量活动：表单被改成叠加型后，确认仍按存量活动的替换型分级。
        var project = WeaveSamples.CreateDemo();
        var replace = new ActivityBlock
        {
            Name = "运动会入场",
            DayIndex = 2,
            StartTime = "08:00",
            EndTime = "08:40",
            Period = 1,
            Mode = ActivityMode.Replace,
        };
        project.Activities.Add(replace);

        var viewModel = new ActivityEditorViewModel(project, replace);
        Assert.True(viewModel.IsEditing);
        Assert.True(viewModel.IsExistingReplace);

        viewModel.ModeIndex = 0; // 老师在表单里把模式改成「只叠加提醒」
        Assert.True(viewModel.IsExistingReplace, "删除确认必须按存量活动的模式分级");
        Assert.Equal("运动会入场", viewModel.ExistingActivityName);
    }

    [Fact]
    public void DeleteConfirmForOverlayExistingActivityIsNotDanger()
    {
        // CreateDemo 自带的「书法课」就是叠加型：确认分级应落在轻确认一侧。
        var project = WeaveSamples.CreateDemo();
        var overlay = project.Activities.Single(item => item.Name == "书法课");

        var viewModel = new ActivityEditorViewModel(project, overlay);
        Assert.False(viewModel.IsExistingReplace);

        var (_, message) = ActivityEditorWindow.DescribeDeleteConfirm(viewModel.IsExistingReplace, viewModel.ExistingActivityName);
        Assert.DoesNotContain("被移除的原课不会自动恢复", message, StringComparison.Ordinal);
    }

    // --------------------------- T15 套用预设确认 ---------------------------

    [Fact]
    public async Task ApplyPresetWithDirtyDraftRequiresConfirmation()
    {
        var project = WeaveSamples.CreateDemo();
        var viewModel = new TimetableEditorViewModel(project);
        Assert.False(viewModel.IsDirty);

        viewModel.Name = "第1节改到 08:10"; // 制造草稿改动
        Assert.True(viewModel.IsDirty);

        string? askedAbout = null;
        viewModel.ConfirmOverwritePreset = presetName =>
        {
            askedAbout = presetName;
            return Task.FromResult(false); // 老师点「取消」
        };

        var before = viewModel.Rows.Select(row => row.Row.Name).ToList();
        await viewModel.ApplyPresetConfirmed();

        Assert.Equal("九年级标准作息", askedAbout, StringComparer.Ordinal);
        // 取消后草稿原样：行集合不被预设替换。
        Assert.Equal(before, viewModel.Rows.Select(row => row.Row.Name).ToList());
    }

    [Fact]
    public async Task ApplyPresetAfterConfirmationReplacesDraft()
    {
        var project = WeaveSamples.CreateDemo();
        var viewModel = new TimetableEditorViewModel(project);
        viewModel.Name = "改动过";

        viewModel.ConfirmOverwritePreset = _ => Task.FromResult(true);
        await viewModel.ApplyPresetConfirmed();

        // grade-9 预设有 17 行（早读…晚修2），替换后草稿整体变成预设内容。
        var gradeNine = WeaveTimetables.FromPreset("grade-9");
        Assert.Equal(gradeNine.Rows.Count, viewModel.Rows.Count);
        Assert.Equal("九年级作息", viewModel.TableName);
    }

    [Fact]
    public async Task ApplyPresetWithoutDirtyDraftSkipsConfirmation()
    {
        var project = WeaveSamples.CreateDemo();
        var viewModel = new TimetableEditorViewModel(project);
        Assert.False(viewModel.IsDirty);

        var confirmed = false;
        viewModel.ConfirmOverwritePreset = _ =>
        {
            confirmed = true;
            return Task.FromResult(false);
        };

        await viewModel.ApplyPresetConfirmed();

        Assert.False(confirmed, "草稿没有改动时不应打扰确认");
        Assert.Equal("九年级作息", viewModel.TableName);
    }

    [Fact]
    public async Task ApplyPresetWithoutHookStillAppliesWhenClean()
    {
        // 确认钩子未注入（或窗口尚未接线）时，无改动的直达路径不能被破坏。
        var project = WeaveSamples.CreateDemo();
        var viewModel = new TimetableEditorViewModel(project);
        Assert.False(viewModel.IsDirty);
        Assert.Null(viewModel.ConfirmOverwritePreset);

        await viewModel.ApplyPresetConfirmed();
        Assert.Equal("九年级作息", viewModel.TableName);
    }

    [Fact]
    public async Task ApplyPresetWithDirtyDraftAndNoHookLeavesDraftAlone()
    {
        // 有改动且没有任何确认手段时宁可不动草稿——不能在老师不知情时替换。
        var project = WeaveSamples.CreateDemo();
        var viewModel = new TimetableEditorViewModel(project);
        viewModel.Name = "改动过"; // 改的是选中行的名称，草稿随之变脏

        await viewModel.ApplyPresetConfirmed();

        Assert.True(viewModel.IsDirty);
        Assert.Equal("改动过", viewModel.SelectedRow!.Row.Name);
    }

    [Fact]
    public async Task DirtyDraftSwitchingPresetAsksAboutNewlySelectedOne()
    {
        // 老师先改了草稿、再换下拉里的另一个预设：确认要点名当下选中的预设。
        var project = WeaveSamples.CreateDemo();
        var viewModel = new TimetableEditorViewModel(project);
        viewModel.Name = "改动过";
        viewModel.SelectedPreset = viewModel.Presets.First(option => option.Preset.Id == "lesson-only");

        string? askedAbout = null;
        viewModel.ConfirmOverwritePreset = presetName =>
        {
            askedAbout = presetName;
            return Task.FromResult(false);
        };

        await viewModel.ApplyPresetConfirmed();
        Assert.Equal("仅正课七节", askedAbout, StringComparer.Ordinal);
    }
}
