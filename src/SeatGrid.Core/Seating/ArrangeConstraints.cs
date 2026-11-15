using System;
using System.Collections.Generic;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Seating;

/// <summary>
/// 一对学生。构造时把两个 Guid 排序，使 (A,B) 与 (B,A) 相等，
/// 这样互斥 / 结对 / 上期同桌都能用同一个键做 O(1) 判定。
/// </summary>
public readonly record struct StudentPair(Guid A, Guid B)
{
    public static StudentPair Create(Guid a, Guid b) => a.CompareTo(b) <= 0 ? new(a, b) : new(b, a);
}

/// <summary>
/// 排座约束。全部是可选集合，默认构造即「无约束」，行为与旧版顺序填充一致。
/// 注意：锁的是「学生-座位绑定」而不是座位本身——同一个座位若被两名学生锁定，先到先得。
/// </summary>
public sealed class ArrangeConstraints
{
    public static ArrangeConstraints Empty { get; } = new();

    /// <summary>学生 Id → 座位。重排时这些学生固定不动。</summary>
    public IReadOnlyDictionary<Guid, SeatPosition> Pins { get; init; } =
        new Dictionary<Guid, SeatPosition>();

    /// <summary>绝不相邻：前后左右都不挨着。</summary>
    public IReadOnlyList<StudentPair> Mutex { get; init; } = Array.Empty<StudentPair>();

    /// <summary>必须同桌：两人坐同一张双人桌。</summary>
    public IReadOnlyList<StudentPair> Deskmates { get; init; } = Array.Empty<StudentPair>();

    /// <summary>尽量不安排同桌（上期同桌）。软约束，排不下时如实报告。</summary>
    public IReadOnlyList<StudentPair> AvoidDeskmates { get; init; } = Array.Empty<StudentPair>();

    /// <summary>学生 Id → 允许 / 禁止的座位矩形。</summary>
    public IReadOnlyDictionary<Guid, StudentZone> Zones { get; init; } =
        new Dictionary<Guid, StudentZone>();

    /// <summary>随机模式的种子；为空时用 Random.Shared（测试应显式给种子）。</summary>
    public int? Seed { get; init; }

    public bool IsEmpty =>
        Pins.Count == 0 && Mutex.Count == 0 && Deskmates.Count == 0 &&
        AvoidDeskmates.Count == 0 && Zones.Count == 0;

    private HashSet<(Guid, Guid)>? _mutexKeys;
    private HashSet<(Guid, Guid)>? _deskmateKeys;
    private HashSet<(Guid, Guid)>? _avoidKeys;

    public bool IsPinned(Guid id) => Pins.ContainsKey(id);

    public bool IsMutex(Guid a, Guid b) =>
        (_mutexKeys ??= BuildKeys(Mutex)).Contains(Normalize(a, b));

    public bool IsDeskmate(Guid a, Guid b) =>
        (_deskmateKeys ??= BuildKeys(Deskmates)).Contains(Normalize(a, b));

    public bool IsAvoidDeskmate(Guid a, Guid b) =>
        (_avoidKeys ??= BuildKeys(AvoidDeskmates)).Contains(Normalize(a, b));

    /// <summary>修复阶段用：软策略不得搬动锁定学生，但硬约束仍需生效。</summary>
    public ArrangeConstraints WithoutPins() => new()
    {
        Mutex = Mutex,
        Deskmates = Deskmates,
        AvoidDeskmates = AvoidDeskmates,
        Zones = Zones,
        Seed = Seed,
    };

    private static (Guid, Guid) Normalize(Guid a, Guid b) =>
        a.CompareTo(b) <= 0 ? (a, b) : (b, a);

    private static HashSet<(Guid, Guid)> BuildKeys(IReadOnlyList<StudentPair> pairs)
    {
        var set = new HashSet<(Guid, Guid)>(pairs.Count);
        foreach (var pair in pairs) set.Add(Normalize(pair.A, pair.B));
        return set;
    }
}
