using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Seating;

/// <summary>组内成绩顺序。</summary>
public enum WithinGroupOrder
{
    /// <summary>高低交替（首尾配对）：前排与同桌天然异质，推荐。</summary>
    Fold,

    /// <summary>从高到低。</summary>
    Descending,

    /// <summary>从低到高。</summary>
    Ascending,
}

/// <summary>
/// 成绩分桶：把学生按成绩分配到目标组（蛇形轮转）或三层（每组等高/中/低），
/// 并给出组内顺序。无成绩者不参与分桶，排在所有有成绩者之后。
/// </summary>
public static class ScorePlanner
{
    /// <summary>每名学生的目标组（0 基）。无成绩的学生不在字典里。</summary>
    public static IReadOnlyDictionary<Guid, int> TargetGroups(
        IReadOnlyList<Student> students, SeatChartLayout layout, ArrangeMode mode)
    {
        var result = new Dictionary<Guid, int>();
        var scored = Ordered(students);
        if (scored.Count == 0 || layout.Groups <= 0) return result;

        if (mode == ArrangeMode.ScoreLayers)
        {
            var layers = SplitLayers(scored);
            foreach (var layer in layers)
                AssignSnake(layer, layout.Groups, result);
        }
        else
        {
            AssignSnake(scored, layout.Groups, result);
        }

        return result;
    }

    /// <summary>蛇形分桶：偶数轮正序、奇数轮倒序，保证各组人数与均分都接近。</summary>
    private static void AssignSnake(
        IReadOnlyList<Student> ordered, int groups, Dictionary<Guid, int> target)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var round = i / groups;
            var k = i % groups;
            var group = round % 2 == 0 ? k : groups - 1 - k;
            target[ordered[i].Id] = group;
        }
    }

    /// <summary>三层：按名次切成高 / 中 / 低三段（各 1/3，余数给中段）。</summary>
    private static List<List<Student>> SplitLayers(IReadOnlyList<Student> ordered)
    {
        var n = ordered.Count;
        var high = n / 3;
        var low = n / 3;
        var mid = n - high - low;

        return new List<List<Student>>
        {
            ordered.Take(high).ToList(),
            ordered.Skip(high).Take(mid).ToList(),
            ordered.Skip(high + mid).ToList(),
        };
    }

    /// <summary>组内顺序：Fold 首尾配对交替，其余为字面义。</summary>
    public static IReadOnlyList<Student> OrderWithin(
        IReadOnlyList<Student> group, WithinGroupOrder within)
    {
        var sorted = Ordered(group);
        if (within == WithinGroupOrder.Descending) return sorted;
        if (within == WithinGroupOrder.Ascending) return sorted.AsEnumerable().Reverse().ToList();

        // Fold：最高、最低、次高、次低……
        var result = new List<Student>(sorted.Count);
        var lo = 0;
        var hi = sorted.Count - 1;
        while (lo <= hi)
        {
            result.Add(sorted[lo++]);
            if (lo <= hi) result.Add(sorted[hi--]);
        }

        return result;
    }

    /// <summary>各组均分（只统计有成绩者）；供界面展示与测试断言。</summary>
    public static IReadOnlyDictionary<int, double> GroupAverages(
        IReadOnlyDictionary<SeatPosition, Student> assignments, int groups)
    {
        var sums = new double[Math.Max(1, groups)];
        var counts = new int[Math.Max(1, groups)];

        foreach (var (position, student) in assignments)
        {
            if (student.Score is null) continue;
            if (position.Group < 0 || position.Group >= groups) continue;
            sums[position.Group] += student.Score.Value;
            counts[position.Group]++;
        }

        var result = new Dictionary<int, double>();
        for (var g = 0; g < groups; g++)
            result[g] = counts[g] == 0 ? 0 : Math.Round(sums[g] / counts[g], 1);
        return result;
    }

    /// <summary>成绩降序；同分按传入顺序（名单顺序），保证确定性。</summary>
    private static List<Student> Ordered(IReadOnlyList<Student> students) =>
        students.Where(s => s.Score is not null)
            .OrderByDescending(s => s.Score!.Value)
            .ToList();
}
