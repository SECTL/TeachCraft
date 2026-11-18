using System.Collections.Generic;
using System.Linq;
using GradeWeave.Core.Models;
using GradeWeave.Core.Scheduling;

namespace GradeWeave.App;

/// <summary>
/// 课程格的走班判定（审计 P2-6）。窗口左键放课、右键菜单与拖拽共用同一份判断。
/// 放在窗口外是为了能在没有 Avalonia 应用与渲染后端的测试里直接覆盖：
/// 活走班一律拒绝单独改动；组已被删除的孤儿走班课必须放行——否则那一格成了
/// 任何指针入口都动不了的死格，提示里指的「03 步走班组」也早已不存在。
/// </summary>
public static class ScheduleCellRules
{
    /// <summary>该格是否按「活走班」处理：时段被现存走班组占用，或格里有组仍存在的走班课。</summary>
    public static bool IsActiveLayeredSlot(
        WeaveProject project, IEnumerable<LessonPlacement> slotLessons, LayeredClassGroup? group) =>
        group is not null ||
        slotLessons.Any(lesson => lesson.IsLayered && !ConstraintRules.IsOrphanedLayeredLesson(project, lesson));

    /// <summary>格子里是否有组已删除的孤儿走班课。</summary>
    public static bool HasOrphanedLayeredLesson(
        WeaveProject project, IEnumerable<LessonPlacement> slotLessons) =>
        slotLessons.Any(lesson => ConstraintRules.IsOrphanedLayeredLesson(project, lesson));

    /// <summary>右键菜单里走班那一项的文案；空串表示这一格不该显示该项。</summary>
    public static string LayeredMenuHeader(
        WeaveProject project, IEnumerable<LessonPlacement> slotLessons, LayeredClassGroup? group)
    {
        if (IsActiveLayeredSlot(project, slotLessons, group)) return "走班时段由 03 步走班组管理";
        return HasOrphanedLayeredLesson(project, slotLessons) ? "清除残留走班课" : string.Empty;
    }
}
