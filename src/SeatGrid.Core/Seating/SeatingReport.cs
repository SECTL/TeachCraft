using System.Collections.Generic;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Seating;

/// <summary>排座时没能满足的规则类型。UI 按类型配中文提示。</summary>
public enum SeatIssueKind
{
    /// <summary>锁定的座位超出当前布局。</summary>
    PinOutOfLayout,

    /// <summary>两名学生被锁定到同一个座位。</summary>
    PinConflict,

    /// <summary>座位不够，有学生没排上。</summary>
    NoSeatLeft,

    /// <summary>区域限制放宽了（该生被排到允许区域之外）。</summary>
    ZoneUnsatisfied,

    /// <summary>互斥对没能分开。</summary>
    MutexUnsatisfied,

    /// <summary>结对没能坐成同桌。</summary>
    DeskmateUnsatisfied,

    /// <summary>规则本身冲突（自配对、同时互斥又结对、结对链等）。</summary>
    RuleConflict,

    /// <summary>导师制跳过（组内无成绩、组长座被占）。</summary>
    MentorSkipped,

    /// <summary>没有成绩，成绩模式退化为名单顺序。</summary>
    ScoreMissing,

    /// <summary>因锁定或区域，该生未能进入目标组。</summary>
    BalanceDegraded,
}

/// <summary>一条排座说明。Message 是给老师看的中文，Students 用于界面高亮。</summary>
public sealed record SeatIssue(SeatIssueKind Kind, IReadOnlyList<string> Students, string Message);

/// <summary>排座结果：座位分配 + 没能满足的规则 + 未排座学生（绝不静默丢人）。</summary>
public sealed record ArrangeResult(
    IReadOnlyDictionary<SeatPosition, Student> Assignments,
    IReadOnlyList<SeatIssue> Issues,
    IReadOnlyList<Student> Unseated);
