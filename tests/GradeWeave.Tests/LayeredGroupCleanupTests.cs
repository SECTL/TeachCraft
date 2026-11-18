using GradeWeave.App;
using GradeWeave.Core.Models;
using GradeWeave.Core.Scheduling;

namespace GradeWeave.Tests;

/// <summary>
/// 走班组删除与残留走班课清理回归：走班课是 IsFixed=true 的固定课，重排不会清掉。
/// 删组必须连带清掉该组的走班课，否则课时成了任何入口都清不掉的死格；
/// 组已不存在的孤儿走班课必须允许单独清除，而不是让提示指向一个不存在的组。
/// </summary>
public sealed class LayeredGroupCleanupTests
{
    [Fact]
    public async Task DeleteLayeredGroup_RemovesItsLayeredLessons()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var groupLessons = viewModel.Project.Lessons
            .Where(item => item.IsLayered && item.GroupId == group.Id)
            .ToList();
        Assert.NotEmpty(groupLessons);

        viewModel.DeleteLayeredGroupCommand.Execute(null);

        Assert.DoesNotContain(viewModel.Project.LayeredGroups, item => item.Id == group.Id);
        Assert.DoesNotContain(viewModel.Project.Lessons, item => item.IsLayered && item.GroupId == group.Id);
        Assert.Contains("已删除走班组", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains($"同时清除了该组 {groupLessons.Count} 节走班课", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearLessonAt_AllowsOrphanedLayeredLesson()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);

        // 直接从工程里摘走组（模拟旧文件或其他入口删组没清课），课时成了孤儿。
        viewModel.Project.LayeredGroups.Remove(group);

        viewModel.ClearLessonAt(lesson.ClassId, lesson.DayIndex, lesson.Period);

        Assert.Null(viewModel.Project.LessonAt(lesson.ClassId, lesson.DayIndex, lesson.Period));
        Assert.Contains("残留", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearLessonAt_StillRejectsLayeredLessonOfExistingGroup()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);

        viewModel.ClearLessonAt(lesson.ClassId, lesson.DayIndex, lesson.Period);

        Assert.NotNull(viewModel.Project.LessonAt(lesson.ClassId, lesson.DayIndex, lesson.Period));
        Assert.Contains("走班", viewModel.StatusText, StringComparison.Ordinal);
    }

    // ------------------- 指针入口与 ClearLessonAt 判定对齐（审计 P2-6） -------------------

    [Fact]
    public async Task ActiveLayeredLesson_IsNotEditableAndKeepsPointerMenu()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);
        var slotLessons = SlotLessons(viewModel, lesson);

        Assert.False(ConstraintRules.IsOrphanedLayeredLesson(viewModel.Project, lesson));
        Assert.True(ScheduleCellRules.IsActiveLayeredSlot(viewModel.Project, slotLessons, group));
        Assert.Equal("走班时段由 03 步走班组管理", ScheduleCellRules.LayeredMenuHeader(
            viewModel.Project, slotLessons, group));
        Assert.False(ScheduleCellRules.HasOrphanedLayeredLesson(viewModel.Project, slotLessons));
    }

    [Fact]
    public async Task OrphanedLayeredLesson_IsEditableAndMenuOffersClear()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);
        viewModel.Project.LayeredGroups.Remove(group);
        var slotLessons = SlotLessons(viewModel, lesson);

        // 修复前这里全是 false / 空串：左键、右键、拖动三条指针入口一起把孤儿课锁死。
        Assert.True(ConstraintRules.IsOrphanedLayeredLesson(viewModel.Project, lesson));
        Assert.False(ScheduleCellRules.IsActiveLayeredSlot(viewModel.Project, slotLessons, null));
        Assert.True(ScheduleCellRules.HasOrphanedLayeredLesson(viewModel.Project, slotLessons));
        Assert.Equal("清除残留走班课", ScheduleCellRules.LayeredMenuHeader(
            viewModel.Project, slotLessons, null));
    }

    [Fact]
    public async Task OrphanedLayeredLesson_IsDraggableAndReplaced()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);
        viewModel.Project.LayeredGroups.Remove(group);

        var (toDay, toPeriod) = NormalSlot(viewModel, lesson);
        viewModel.MoveLesson(lesson.ClassId, lesson.DayIndex, lesson.Period, toDay, toPeriod);

        var moved = viewModel.Project.LessonAt(lesson.ClassId, toDay, toPeriod);
        Assert.NotNull(moved);
        Assert.Equal(lesson.SubjectId, moved!.SubjectId);
        Assert.Contains("已把", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveLayeredLesson_IsStillRejectedByDrag()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);

        var (toDay, toPeriod) = NormalSlot(viewModel, lesson);
        var targetBefore = viewModel.Project.LessonAt(lesson.ClassId, toDay, toPeriod);
        viewModel.MoveLesson(lesson.ClassId, lesson.DayIndex, lesson.Period, toDay, toPeriod);

        Assert.Same(targetBefore, viewModel.Project.LessonAt(lesson.ClassId, toDay, toPeriod));
        Assert.Contains("走班", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanedLayeredLesson_CanBePlacedOverByLessonEditor()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);
        viewModel.Project.LayeredGroups.Remove(group);

        var editor = new LessonEditorViewModel(
            viewModel.Project, lesson.ClassId, lesson.DayIndex, lesson.Period);
        Assert.True(editor.CanClear, "孤儿走班课必须能在放课窗口里清掉");

        viewModel.PlaceLesson(lesson.ClassId, lesson.DayIndex, lesson.Period, "s1", "t1");

        var placed = viewModel.Project.LessonAt(lesson.ClassId, lesson.DayIndex, lesson.Period);
        Assert.NotNull(placed);
        Assert.False(placed!.IsLayered);
        Assert.Equal("s1", placed.SubjectId);
    }

    [Fact]
    public async Task ActiveLayeredLesson_CannotBeClearedFromLessonEditor()
    {
        var viewModel = await GenerateDemoAsync();
        var group = viewModel.Project.LayeredGroups[0];
        var lesson = viewModel.Project.Lessons.First(item => item.IsLayered && item.GroupId == group.Id);

        var editor = new LessonEditorViewModel(
            viewModel.Project, lesson.ClassId, lesson.DayIndex, lesson.Period);
        Assert.False(editor.CanClear, "活走班课仍由 03 步走班组管理");
    }

    private static List<LessonPlacement> SlotLessons(GradeWeaveViewModel viewModel, LessonPlacement lesson) =>
        viewModel.Project.Lessons
            .Where(item => item.ClassId == lesson.ClassId &&
                           item.DayIndex == lesson.DayIndex &&
                           item.Period == lesson.Period)
            .ToList();

    /// <summary>找同班一个普通课格（非走班、非活动）当拖动目标；演示课表已排满，没有空格可用。</summary>
    private static (int Day, int Period) NormalSlot(GradeWeaveViewModel viewModel, LessonPlacement source)
    {
        var periods = viewModel.Project.Periods.Where(item => !item.IsExtension).Select(item => item.Index);
        foreach (var day in Enumerable.Range(1, 5))
        {
            foreach (var period in periods)
            {
                if (day == source.DayIndex && period == source.Period) continue;

                var candidate = viewModel.Project.LessonAt(source.ClassId, day, period);
                if (candidate is null || candidate.IsActivity || candidate.IsLayered) continue;

                LayeredClassGroup? groupAt = null;
                ConstraintRules.TryGetLayeredGroupAt(viewModel.Project, source.ClassId, day, period, out groupAt);
                if (groupAt is null) return (day, period);
            }
        }

        throw new InvalidOperationException("演示课表没有普通课格可用于拖动测试。");
    }

    private static async Task<GradeWeaveViewModel> GenerateDemoAsync()
    {
        var viewModel = new GradeWeaveViewModel();
        viewModel.LoadDemoProject();
        await viewModel.GenerateAsync();
        return viewModel;
    }
}
