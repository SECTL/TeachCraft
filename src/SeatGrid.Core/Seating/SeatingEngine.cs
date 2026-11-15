using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Seating;

public static class SeatingEngine
{
    /// <summary>
    /// 按给定顺序把学生填入座位。座位不够时多出的学生保持未安排；
    /// 学生不够时剩余座位留空。
    /// </summary>
    public static Dictionary<SeatPosition, Student> Arrange(
        IReadOnlyList<Student> students,
        SeatChartLayout layout,
        ArrangeMode mode) =>
        new(Arrange(students, layout, mode, ArrangeConstraints.Empty).Assignments);

    /// <summary>
    /// 带约束的排座：固定点预占 → 约束感知贪心 → 局部修复（有限轮交换）→ 报告。
    /// 不做通用 CSP；规模上界 80 人 / 320 座，全流程毫秒级。
    /// </summary>
    public static ArrangeResult Arrange(
        IReadOnlyList<Student> students,
        SeatChartLayout layout,
        ArrangeMode mode,
        ArrangeConstraints constraints)
    {
        constraints ??= ArrangeConstraints.Empty;
        var issues = new List<SeatIssue>();

        if (students.Count == 0)
            return new ArrangeResult(new Dictionary<SeatPosition, Student>(), issues, Array.Empty<Student>());

        var byId = students.ToDictionary(s => s.Id);
        var ordered = Order(students, mode, constraints.Seed);
        var allowed = BuildAllowedSets(students, layout, constraints, issues);

        var assigned = new Dictionary<SeatPosition, Student>();
        var placed = new HashSet<Guid>();

        // 1) 固定点预占：按名单顺序，先到先得。
        ReservePins(students, layout, constraints, byId, assigned, placed, issues);

        // 2) 约束感知贪心。
        var units = BuildUnits(ordered, mode, constraints, issues);
        var targets = mode is ArrangeMode.ScoreSnake or ArrangeMode.ScoreLayers
            ? ScorePlanner.TargetGroups(students, layout, mode)
            : new Dictionary<Guid, int>();

        if (mode is ArrangeMode.ScoreSnake or ArrangeMode.ScoreLayers && targets.Count == 0)
            issues.Add(new SeatIssue(SeatIssueKind.ScoreMissing,
                Array.Empty<string>(), "名单里没有成绩，已按名单顺序排座。"));

        var seats = layout.FillOrder().ToList();
        foreach (var unit in units)
        {
            if (unit.Members.All(m => placed.Contains(m.Id))) continue;

            var strict = FirstFreeAnchor(unit, seats, layout, assigned, constraints, allowed, targets);
            var relaxed = strict is null;
            var seat = strict ?? FirstFreeAnchor(unit, seats, layout, assigned, constraints, allowed, targets,
                relax: true);

            // 结对在单列布局里根本没有双人桌：退化为两人各自单人入座，保证一个都不丢。
            if (seat is null && unit.Members.Count == 2)
            {
                var firstSeat = FirstFreeAnchor(new SeatUnit(new[] { unit.Members[0] }), seats, layout,
                    assigned, constraints, allowed, targets, relax: true);
                if (firstSeat is not null)
                {
                    // 先占座，再给第二个人找——否则两人会拿到同一个空位。
                    assigned[firstSeat.Value] = unit.Members[0];
                    placed.Add(unit.Members[0].Id);

                    var mateSeat = FirstFreeAnchor(new SeatUnit(new[] { unit.Members[1] }), seats, layout,
                        assigned, constraints, allowed, targets, relax: true);
                    if (mateSeat is not null)
                    {
                        assigned[mateSeat.Value] = unit.Members[1];
                        placed.Add(unit.Members[1].Id);
                        var names = string.Join("、", unit.Members.Select(m => m.Name));
                        issues.Add(new SeatIssue(SeatIssueKind.DeskmateUnsatisfied,
                            unit.Members.Select(m => m.Name).ToList(),
                            $"{names} 未能坐成同桌：当前布局里没有双人桌，已各自单独入座。"));
                        continue;
                    }

                    // 第二个找不到座：退回第一个占位，让下面统一报「没有可用座位」。
                    assigned.Remove(firstSeat.Value);
                    placed.Remove(unit.Members[0].Id);
                }
            }

            if (seat is null)
            {
                var names = string.Join("、", unit.Members.Select(m => m.Name));
                issues.Add(new SeatIssue(SeatIssueKind.NoSeatLeft, unit.Members.Select(m => m.Name).ToList(),
                    $"{names} 没有可用的座位，已保留为未排。"));
                continue;
            }

            if (relaxed)
            {
                var names = string.Join("、", unit.Members.Select(m => m.Name));
                issues.Add(new SeatIssue(SeatIssueKind.ZoneUnsatisfied, unit.Members.Select(m => m.Name).ToList(),
                    $"{names} 的座位规则一个都满足不了，已先保证有座，其他规则在下面的说明里逐条列出。"));
            }

            PlaceUnit(unit, seat.Value, layout, assigned, placed);
        }

        // 3) 局部修复：有限轮两两交换，只碰非锁定座位。
        Repair(assigned, layout, constraints, allowed, issues);

        // 4) 收尾：未排座学生原样返回。
        var unseated = students.Where(s => !placed.Contains(s.Id)).ToList();

        // 软约束的如实报告：上期同桌、成绩目标组、区域放宽。
        ReportSoftViolations(assigned, layout, constraints, targets, issues);

        return new ArrangeResult(assigned, issues, unseated);
    }

    public static List<Student> Order(IReadOnlyList<Student> students, ArrangeMode mode) =>
        Order(students, mode, null);

    public static List<Student> Order(
        IReadOnlyList<Student> students, ArrangeMode mode, int? seed)
    {
        if (students.Count == 0) return new List<Student>();

        return mode switch
        {
            ArrangeMode.GenderAlternate => AlternateGender(students),
            ArrangeMode.GenderPair => PairGender(students),
            ArrangeMode.Random => RandomOrder(students, seed),
            ArrangeMode.Height => students
                .OrderBy(s => s.Height is null ? 1 : 0)
                .ThenBy(s => s.Height)
                .ToList(),
            ArrangeMode.Vision => students
                .OrderBy(s => s.Vision is null ? 1 : 0)
                .ThenBy(s => s.Vision)
                .ToList(),
            ArrangeMode.ScoreSnake or ArrangeMode.ScoreLayers => students
                .OrderBy(s => s.Score is null ? 1 : 0)
                .ThenByDescending(s => s.Score)
                .ToList(),
            ArrangeMode.RosterOrder => students.ToList(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "未知的排座方式；新增模式时请补上 Order 的分支。"),
        };
    }

    // ----------------------------- 各阶段 -----------------------------

    private static void ReservePins(
        IReadOnlyList<Student> students, SeatChartLayout layout, ArrangeConstraints constraints,
        Dictionary<Guid, Student> byId, Dictionary<SeatPosition, Student> assigned,
        HashSet<Guid> placed, List<SeatIssue> issues)
    {
        if (constraints.Pins.Count == 0) return;

        // 按名单顺序预占，保证「先到先得」可预期。
        foreach (var student in students)
        {
            if (!constraints.Pins.TryGetValue(student.Id, out var seat)) continue;

            if (!layout.Contains(seat))
            {
                issues.Add(new SeatIssue(SeatIssueKind.PinOutOfLayout, new[] { student.Name },
                    $"{student.Name} 锁定的座位（{seat}）超出当前布局，已取消锁定并重新排座。"));
                continue;
            }

            if (assigned.TryGetValue(seat, out var occupant))
            {
                issues.Add(new SeatIssue(SeatIssueKind.PinConflict,
                    new[] { occupant.Name, student.Name },
                    $"{occupant.Name} 与 {student.Name} 都被锁定到 {seat}，已保留 {occupant.Name}。"));
                continue;
            }

            assigned[seat] = student;
            placed.Add(student.Id);
        }
    }

    /// <summary>
    /// 找一个可用座位。<paramref name="relax"/> 为真时只要求「座位空着」——
    /// 硬约束一个都满足不了时用这一层兜底，保证「学生一定有座」，再由报告如实说明哪条没满足。
    /// </summary>
    private static SeatPosition? FirstFreeAnchor(
        SeatUnit unit, List<SeatPosition> seats, SeatChartLayout layout,
        Dictionary<SeatPosition, Student> assigned, ArrangeConstraints constraints,
        Dictionary<Guid, HashSet<SeatPosition>> allowed,
        IReadOnlyDictionary<Guid, int> targets,
        bool relax = false)
    {
        SeatPosition? best = null;
        var bestCost = int.MaxValue;

        foreach (var seat in seats)
        {
            if (relax)
            {
                if (assigned.ContainsKey(seat)) continue;
                if (unit.Members.Count == 2)
                {
                    var mate = layout.DeskmateOf(seat);
                    if (mate is null || assigned.ContainsKey(mate.Value)) continue;
                }
            }
            else if (!SeatFits(unit, seat, layout, assigned, constraints, allowed))
            {
                continue;
            }

            var cost = SoftCost(unit, seat, constraints, targets);
            if (cost == 0) return seat;                 // 早停：零代价座
            if (cost < bestCost)
            {
                bestCost = cost;
                best = seat;
            }
        }

        return best;
    }

    private static bool SeatFits(
        SeatUnit unit, SeatPosition seat, SeatChartLayout layout,
        Dictionary<SeatPosition, Student> assigned, ArrangeConstraints constraints,
        Dictionary<Guid, HashSet<SeatPosition>> allowed)
    {
        if (assigned.ContainsKey(seat)) return false;

        var first = unit.Members[0];
        if (!AllowedAt(first.Id, seat, allowed)) return false;
        if (ViolatesMutex(first.Id, seat, layout, assigned, constraints)) return false;

        if (unit.Members.Count == 2)
        {
            var mate = layout.DeskmateOf(seat);
            if (mate is null || assigned.ContainsKey(mate.Value)) return false;

            var second = unit.Members[1];
            if (!AllowedAt(second.Id, mate.Value, allowed)) return false;
            if (ViolatesMutex(second.Id, mate.Value, layout, assigned, constraints)) return false;
        }

        return true;
    }

    private static bool AllowedAt(Guid id, SeatPosition seat, Dictionary<Guid, HashSet<SeatPosition>> allowed) =>
        !allowed.TryGetValue(id, out var set) || set.Contains(seat);

    private static bool ViolatesMutex(
        Guid id, SeatPosition seat, SeatChartLayout layout,
        Dictionary<SeatPosition, Student> assigned, ArrangeConstraints constraints)
    {
        if (constraints.Mutex.Count == 0) return false;

        foreach (var neighbor in layout.NeighborsOf(seat))
        {
            if (assigned.TryGetValue(neighbor, out var other) && constraints.IsMutex(id, other.Id))
                return true;
        }

        return false;
    }

    private static int SoftCost(
        SeatUnit unit, SeatPosition seat, ArrangeConstraints constraints,
        IReadOnlyDictionary<Guid, int> targets)
    {
        var cost = 0;

        foreach (var member in unit.Members)
        {
            if (targets.TryGetValue(member.Id, out var target) && target != seat.Group) cost += 1;
        }

        return cost;
    }

    private static void PlaceUnit(
        SeatUnit unit, SeatPosition seat, SeatChartLayout layout,
        Dictionary<SeatPosition, Student> assigned, HashSet<Guid> placed)
    {
        assigned[seat] = unit.Members[0];
        placed.Add(unit.Members[0].Id);

        if (unit.Members.Count == 2)
        {
            var mate = layout.DeskmateOf(seat);
            if (mate is not null)
            {
                assigned[mate.Value] = unit.Members[1];
                placed.Add(unit.Members[1].Id);
            }
        }
    }

    private static void Repair(
        Dictionary<SeatPosition, Student> assigned, SeatChartLayout layout,
        ArrangeConstraints constraints, Dictionary<Guid, HashSet<SeatPosition>> allowed,
        List<SeatIssue> issues)
    {
        const int maxPasses = 4;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var improved = false;
            var positions = assigned.Keys.ToList();

            foreach (var a in positions)
            {
                if (!assigned.TryGetValue(a, out var studentA)) continue;
                if (constraints.IsPinned(studentA.Id)) continue;
                if (!HasViolation(a, layout, assigned, constraints, allowed)) continue;

                foreach (var b in positions)
                {
                    if (a == b) continue;
                    if (!assigned.TryGetValue(b, out var studentB)) continue;
                    if (constraints.IsPinned(studentB.Id)) continue;

                    var before = ViolationCount(a, layout, assigned, constraints, allowed) +
                                 ViolationCount(b, layout, assigned, constraints, allowed);

                    (assigned[a], assigned[b]) = (studentB, studentA);
                    var after = ViolationCount(a, layout, assigned, constraints, allowed) +
                                ViolationCount(b, layout, assigned, constraints, allowed);

                    if (after < before)
                    {
                        improved = true;
                        break;
                    }

                    (assigned[a], assigned[b]) = (studentA, studentB); // 回退
                }

                if (improved) break;
            }

            if (!improved) break;
        }
    }

    private static bool HasViolation(
        SeatPosition seat, SeatChartLayout layout, Dictionary<SeatPosition, Student> assigned,
        ArrangeConstraints constraints, Dictionary<Guid, HashSet<SeatPosition>> allowed) =>
        ViolationCount(seat, layout, assigned, constraints, allowed) > 0;

    private static int ViolationCount(
        SeatPosition seat, SeatChartLayout layout, Dictionary<SeatPosition, Student> assigned,
        ArrangeConstraints constraints, Dictionary<Guid, HashSet<SeatPosition>> allowed)
    {
        if (!assigned.TryGetValue(seat, out var student)) return 0;

        var count = 0;
        if (!AllowedAt(student.Id, seat, allowed)) count += 100;

        foreach (var neighbor in layout.NeighborsOf(seat))
        {
            if (!assigned.TryGetValue(neighbor, out var other)) continue;
            if (constraints.IsMutex(student.Id, other.Id)) count += 1000;
            if (constraints.IsAvoidDeskmate(student.Id, other.Id) && layout.AreDeskmates(seat, neighbor))
                count += 3;
        }

        if (constraints.Deskmates.Count > 0)
        {
            var mate = layout.DeskmateOf(seat);
            if (mate is not null && assigned.TryGetValue(mate.Value, out var deskMate))
            {
                if (constraints.IsDeskmate(student.Id, deskMate.Id) && !layout.AreDeskmates(seat, mate.Value))
                    count += 1000;
            }
        }

        return count;
    }

    private static void ReportSoftViolations(
        Dictionary<SeatPosition, Student> assigned, SeatChartLayout layout,
        ArrangeConstraints constraints, IReadOnlyDictionary<Guid, int> targets,
        List<SeatIssue> issues)
    {
        var byId = assigned.Values.ToDictionary(s => s.Id);

        // 互斥对未能分开
        foreach (var pair in constraints.Mutex)
        {
            if (!byId.TryGetValue(pair.A, out var a) || !byId.TryGetValue(pair.B, out var b)) continue;
            var seatA = SeatOf(assigned, a.Id);
            var seatB = SeatOf(assigned, b.Id);
            if (seatA is null || seatB is null) continue;
            if (layout.AreAdjacent(seatA.Value, seatB.Value))
            {
                issues.Add(new SeatIssue(SeatIssueKind.MutexUnsatisfied, new[] { a.Name, b.Name },
                    $"{a.Name} 与 {b.Name} 未能分开：可用座位里没有既不挨着彼此、又满足其他条件的位置。"));
            }
        }

        // 结对未能同桌
        foreach (var pair in constraints.Deskmates)
        {
            if (!byId.TryGetValue(pair.A, out var a) || !byId.TryGetValue(pair.B, out var b)) continue;
            var seatA = SeatOf(assigned, a.Id);
            var seatB = SeatOf(assigned, b.Id);
            if (seatA is null || seatB is null) continue;
            if (!layout.AreDeskmates(seatA.Value, seatB.Value))
            {
                issues.Add(new SeatIssue(SeatIssueKind.DeskmateUnsatisfied, new[] { a.Name, b.Name },
                    $"{a.Name} 与 {b.Name} 未能坐成同桌：这张表里没有空着的双人桌。"));
            }
        }

        // 上期同桌未能避开（中性提示）
        foreach (var pair in constraints.AvoidDeskmates)
        {
            if (!byId.TryGetValue(pair.A, out var a) || !byId.TryGetValue(pair.B, out var b)) continue;
            var seatA = SeatOf(assigned, a.Id);
            var seatB = SeatOf(assigned, b.Id);
            if (seatA is null || seatB is null) continue;
            if (layout.AreDeskmates(seatA.Value, seatB.Value))
            {
                issues.Add(new SeatIssue(SeatIssueKind.BalanceDegraded, new[] { a.Name, b.Name },
                    $"{a.Name} 与 {b.Name} 仍是同桌：其他安排占用太多，已优先满足硬性规则。"));
            }
        }

        // 成绩目标组未达成
        if (targets.Count > 0)
        {
            foreach (var (position, student) in assigned)
            {
                if (!targets.TryGetValue(student.Id, out var target)) continue;
                if (target == position.Group) continue;
                issues.Add(new SeatIssue(SeatIssueKind.BalanceDegraded, new[] { student.Name },
                    $"{student.Name} 未能进入第 {target + 1} 组：锁定或区域限制优先，各组均分可能略有偏差。"));
            }
        }
    }

    private static SeatPosition? SeatOf(Dictionary<SeatPosition, Student> assigned, Guid id)
    {
        foreach (var (position, student) in assigned)
            if (student.Id == id) return position;
        return null;
    }

    // ----------------------------- 区域与单元 -----------------------------

    private static Dictionary<Guid, HashSet<SeatPosition>> BuildAllowedSets(
        IReadOnlyList<Student> students, SeatChartLayout layout,
        ArrangeConstraints constraints, List<SeatIssue> issues)
    {
        var result = new Dictionary<Guid, HashSet<SeatPosition>>();
        if (constraints.Zones.Count == 0) return result;

        foreach (var student in students)
        {
            if (!constraints.Zones.TryGetValue(student.Id, out var zone) || zone.IsEmpty) continue;

            var set = new HashSet<SeatPosition>();
            foreach (var seat in layout.AllSeats())
                if (zone.Allows(seat)) set.Add(seat);

            if (set.Count == 0)
            {
                issues.Add(new SeatIssue(SeatIssueKind.ZoneUnsatisfied, new[] { student.Name },
                    $"{student.Name} 的座位区域里一个可用座位都没有，已放宽为全表可选。"));
                continue;
            }

            result[student.Id] = set;
        }

        return result;
    }

    private readonly record struct SeatUnit(IReadOnlyList<Student> Members);

    private static List<SeatUnit> BuildUnits(
        IReadOnlyList<Student> ordered, ArrangeMode mode,
        ArrangeConstraints constraints, List<SeatIssue> issues)
    {
        var units = new List<SeatUnit>();
        var used = new HashSet<Guid>();

        // 显式结对优先：无论什么模式，先成对。
        foreach (var pair in constraints.Deskmates)
        {
            var a = ordered.FirstOrDefault(s => s.Id == pair.A);
            var b = ordered.FirstOrDefault(s => s.Id == pair.B);
            if (a is null || b is null) continue;
            if (used.Contains(a.Id) || used.Contains(b.Id))
            {
                issues.Add(new SeatIssue(SeatIssueKind.RuleConflict, new[] { a.Name, b.Name },
                    $"{a.Name} 与 {b.Name} 的结对规则与其他结对冲突：一个座位只有一个同桌。"));
                continue;
            }

            used.Add(a.Id);
            used.Add(b.Id);
            units.Add(new SeatUnit(new[] { a, b }));
        }

        var rest = ordered.Where(s => !used.Contains(s.Id)).ToList();

        if (mode == ArrangeMode.GenderPair)
        {
            // 同性同桌：按性别成对，落单的单独成 unit。
            for (var i = 0; i < rest.Count;)
            {
                var current = rest[i];
                if (i + 1 < rest.Count && rest[i + 1].Gender == current.Gender && current.Gender != Gender.Unknown)
                {
                    units.Add(new SeatUnit(new[] { current, rest[i + 1] }));
                    i += 2;
                }
                else
                {
                    units.Add(new SeatUnit(new[] { current }));
                    i++;
                }
            }
        }
        else
        {
            units.AddRange(rest.Select(s => new SeatUnit(new[] { s })));
        }

        return units;
    }

    // ----------------------------- 既有排序 -----------------------------

    private static List<Student> RandomOrder(IReadOnlyList<Student> students, int? seed)
    {
        var random = seed is int value ? new Random(value) : Random.Shared;
        return students.OrderBy(_ => random.Next()).ToList();
    }

    private static List<Student> AlternateGender(IReadOnlyList<Student> students)
    {
        var males = students.Where(s => s.Gender == Gender.Male).ToList();
        var females = students.Where(s => s.Gender == Gender.Female).ToList();
        var unknowns = students.Where(s => s.Gender == Gender.Unknown).ToList();

        if (males.Count == 0 && females.Count == 0) return students.ToList();

        var result = new List<Student>(students.Count);
        var maleTurn = students[0].Gender != Gender.Female;

        while (males.Count > 0 || females.Count > 0)
        {
            if (maleTurn)
            {
                if (males.Count > 0) result.Add(males[0]);
                else result.Add(females[0]);
            }
            else
            {
                if (females.Count > 0) result.Add(females[0]);
                else result.Add(males[0]);
            }

            if (result[^1].Gender == Gender.Male) males.RemoveAt(0);
            else females.RemoveAt(0);

            maleTurn = !maleTurn;
        }

        result.AddRange(unknowns);
        return result;
    }

    /// <summary>
    /// 同性同桌：按「男男 女女 男男 女女 …」两两成对填充，性别未知的学生排在末尾；
    /// 一侧用尽后另一侧连排，人数落单时允许末尾出现不成对的混合对。
    /// </summary>
    private static List<Student> PairGender(IReadOnlyList<Student> students)
    {
        var males = students.Where(s => s.Gender == Gender.Male).ToList();
        var females = students.Where(s => s.Gender == Gender.Female).ToList();
        var unknowns = students.Where(s => s.Gender == Gender.Unknown).ToList();

        if (males.Count == 0 && females.Count == 0) return students.ToList();

        var result = new List<Student>(students.Count);
        var maleTurn = students[0].Gender != Gender.Female;

        while (males.Count > 0 || females.Count > 0)
        {
            var current = maleTurn ? males : females;
            if (current.Count == 0)
            {
                maleTurn = !maleTurn;
                current = maleTurn ? males : females;
            }

            var take = Math.Min(2, current.Count);
            result.AddRange(current.Take(take));
            current.RemoveRange(0, take);
            maleTurn = !maleTurn;
        }

        result.AddRange(unknowns);
        return result;
    }
}
