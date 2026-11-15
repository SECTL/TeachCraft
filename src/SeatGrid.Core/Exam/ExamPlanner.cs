using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Exam;

/// <summary>考场布局选项。</summary>
public sealed record ExamLayoutOptions(int MaxColumns = 8, bool Serpentine = true);

/// <summary>
/// 一份考场安排：独立的布局、座位分配与座号。
/// 它与日常座位是两套数据——进出考场不改动日常 `_assignments`。
/// </summary>
public sealed record ExamPlan(
    SeatChartLayout Layout,
    IReadOnlyDictionary<SeatPosition, Student> Assignments,
    IReadOnlyDictionary<SeatPosition, int> SeatNumbers,
    IReadOnlyList<string> Notes);

/// <summary>
/// 考场模式：把日常座位换成「单人桌标准考场」。
///
/// 设计上是一个独立快照而不是日常布局的变形——考场的组/排/列与座位语义都不同，
/// 塞进同一份 assignments 会让「退出考场」依赖一次可逆变换，任何手动改动都会污染还原。
/// 渲染仍走同一个 <see cref="Rendering.SeatChartComposer.Compose"/>，所以
/// 「界面与导出共用一份文档」这条约束自然成立。
/// </summary>
public static class ExamPlanner
{
    public static ExamPlan Build(
        IReadOnlyList<Student> students, ExamLayoutOptions? options = null)
    {
        options ??= new ExamLayoutOptions();
        var notes = new List<string>();

        var count = students.Count;
        if (count == 0)
        {
            return new ExamPlan(new SeatChartLayout(1, 1, 1), new Dictionary<SeatPosition, Student>(),
                new Dictionary<SeatPosition, int>(), new[] { "名单为空，无法生成考场。" });
        }

        // 按人数算单人桌布局：列数取 sqrt(N) 再夹到上限，排数向上取整。
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count)), 1, options.MaxColumns);
        var rows = (int)Math.Ceiling(count / (double)columns);

        if (rows > 10)
            notes.Add($"按 {columns} 列排，需要 {rows} 排；超出常见教室排数，请确认场地够用。");

        var layout = new SeatChartLayout(1, rows, columns);

        var ordered = students.ToList();
        var assignments = new Dictionary<SeatPosition, Student>(count);
        var seatNumbers = NumberSeats(layout, options.Serpentine);

        // 座号 1..N 依次落座：按日常座位顺序传入的名单顺序排。
        var byNumber = seatNumbers.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        for (var i = 0; i < ordered.Count && i < byNumber.Count; i++)
            assignments[byNumber[i]] = ordered[i];

        return new ExamPlan(layout, assignments, seatNumbers, notes);
    }

    /// <summary>
    /// 从日常座位推导考场安排：保持「谁坐前排」的顺序不变（按当前排/组/列排序），
    /// 再按座号依次落座。
    /// </summary>
    public static ExamPlan FromAssignments(
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<SeatPosition, Student> daily,
        ExamLayoutOptions? options = null)
    {
        var ordered = daily
            .OrderBy(kv => kv.Key.Row)
            .ThenBy(kv => kv.Key.Group)
            .ThenBy(kv => kv.Key.Column)
            .Select(kv => kv.Value)
            .ToList();

        // 未排座的学生接在后面，不丢人。
        foreach (var student in students)
        {
            if (ordered.All(s => s.Id != student.Id)) ordered.Add(student);
        }

        return Build(ordered, options);
    }

    /// <summary>座号：1 基。蛇形逐排往返，或按列从上到下。</summary>
    public static IReadOnlyDictionary<SeatPosition, int> NumberSeats(
        SeatChartLayout layout, bool serpentine)
    {
        var result = new Dictionary<SeatPosition, int>(layout.SeatCount);
        var number = 1;

        if (serpentine)
        {
            for (var row = 0; row < layout.Rows; row++)
            {
                if (row % 2 == 0)
                {
                    for (var column = 0; column < layout.Columns; column++)
                        result[new SeatPosition(0, row, column)] = number++;
                }
                else
                {
                    for (var column = layout.Columns - 1; column >= 0; column--)
                        result[new SeatPosition(0, row, column)] = number++;
                }
            }
        }
        else
        {
            for (var column = 0; column < layout.Columns; column++)
                for (var row = 0; row < layout.Rows; row++)
                    result[new SeatPosition(0, row, column)] = number++;
        }

        return result;
    }
}
