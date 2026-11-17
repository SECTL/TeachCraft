using GradeWeave.App;
using GradeWeave.Core.Models;

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

    private static async Task<GradeWeaveViewModel> GenerateDemoAsync()
    {
        var viewModel = new GradeWeaveViewModel();
        viewModel.LoadDemoProject();
        await viewModel.GenerateAsync();
        return viewModel;
    }
}
