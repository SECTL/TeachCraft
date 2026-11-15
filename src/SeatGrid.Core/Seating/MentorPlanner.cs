using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Seating;

/// <summary>组长坐在组的哪里。</summary>
public enum MentorSeat
{
    /// <summary>组中心。</summary>
    Center,

    /// <summary>第一排中点。</summary>
    FrontRow,
}

/// <summary>
/// 小组导师制：每组成绩最高者任组长，组员按「离组长越近越靠前」环绕。
///
/// 这是**纯软约束**——它只影响「谁更优先拿到哪个候选座」，绝不推翻锁定、区域、
/// 互斥与结对。打开导师制不能把老师已经设好的硬规则弄坏，这是老师最不能接受的
/// 失败方式。
/// </summary>
public static class MentorPlanner
{
    /// <summary>每组组长的学生 Id。组内无成绩时该组不出现在结果里。</summary>
    public static IReadOnlyDictionary<int, Guid> Leaders(
        IReadOnlyList<Student> students, IReadOnlyDictionary<Guid, int> groupOf, List<SeatIssue> issues)
    {
        var result = new Dictionary<int, Guid>();
        var byGroup = students
            .Where(s => groupOf.ContainsKey(s.Id))
            .GroupBy(s => groupOf[s.Id]);

        foreach (var group in byGroup.OrderBy(g => g.Key))
        {
            var scored = group.Where(s => s.Score is not null).ToList();
            if (scored.Count == 0)
            {
                issues.Add(new SeatIssue(SeatIssueKind.MentorSkipped, Array.Empty<string>(),
                    $"第 {group.Key + 1} 组没有成绩，未设组长。"));
                continue;
            }

            // 同分取名单顺序靠前者（确定性）。
            var leader = scored
                .OrderByDescending(s => s.Score!.Value)
                .First();
            result[group.Key] = leader.Id;
        }

        return result;
    }

    /// <summary>组长目标座：组中心，或第一排中点。列落在双人桌左侧那张，与同桌共用一桌。</summary>
    public static SeatPosition LeaderSeat(SeatChartLayout layout, int group, MentorSeat kind)
    {
        var column = layout.Columns == 1 ? 0 : Math.Max(0, layout.Columns / 2 - 1);
        var row = kind == MentorSeat.FrontRow ? 0 : Math.Max(0, layout.Rows / 2);
        return new SeatPosition(group, Math.Min(row, layout.Rows - 1), column);
    }

    /// <summary>组内环绕顺序：离组长目标座越近越靠前，同距离成绩高者优先。</summary>
    public static IReadOnlyList<Student> RingOrder(
        IReadOnlyList<Student> group, SeatPosition leaderSeat, IReadOnlyDictionary<Guid, SeatPosition> seats)
    {
        return group
            .OrderBy(s => seats.TryGetValue(s.Id, out var seat) ? Chebyshev(seat, leaderSeat) : int.MaxValue)
            .ThenByDescending(s => s.Score ?? double.MinValue)
            .ToList();
    }

    private static int Chebyshev(SeatPosition a, SeatPosition b) =>
        Math.Max(Math.Abs(a.Row - b.Row), Math.Abs(a.Column - b.Column));
}
