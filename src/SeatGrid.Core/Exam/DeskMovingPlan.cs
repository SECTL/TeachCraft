using System;
using System.Collections.Generic;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Exam;

/// <summary>桌椅搬运计划：搬出几张、补进几张、按哪几排搬。</summary>
public sealed record DeskMovingPlan(
    int DailyDesks,
    int ExamDesks,
    int KeepDesks,
    int MoveOutDesks,
    int MoveInDesks,
    IReadOnlyList<string> Steps);

/// <summary>
/// 桌椅搬动量：只做静态指引，不做路径动画。
/// 软件里没有教室平面图与桌椅编号，做出来的「路径」一定是假的；
/// 老师真正需要的是「搬出几张、补进几张、从哪排搬」这一句。
/// </summary>
public static class DeskMovingPlanCalculator
{
    public static DeskMovingPlan Build(
        SeatChartLayout daily, SeatChartLayout exam, bool dailyIsDoubleDesk)
    {
        var dailyDesks = daily.UsableSeatCount;
        var examDesks = exam.UsableSeatCount;

        var keep = Math.Min(dailyDesks, examDesks);
        var moveOut = Math.Max(0, dailyDesks - examDesks);
        var moveIn = Math.Max(0, examDesks - dailyDesks);

        var steps = new List<string> { $"保留前排 {keep} 张桌椅不动" };

        if (moveOut > 0)
            steps.Add($"把第 {exam.Rows + 1} 排起的 {moveOut} 张桌椅搬到走廊");

        if (moveIn > 0)
            steps.Add($"从隔壁借 {moveIn} 张桌椅，补到第 {daily.Rows + 1} 排之后");

        if (dailyIsDoubleDesk && exam.Columns > daily.Columns)
            steps.Add($"每张双人桌拆成两张单人桌，共拆 {daily.Groups * daily.Rows} 张");

        steps.Add("按考场座位表核对座号，从第 1 排开始贴桌贴");

        return new DeskMovingPlan(dailyDesks, examDesks, keep, moveOut, moveIn, steps);
    }
}
