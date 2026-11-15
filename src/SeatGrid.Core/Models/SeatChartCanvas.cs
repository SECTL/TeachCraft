using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SeatGrid.Core.Models;

/// <summary>一个格子在画布上的性质。过道格不坐人，也不出现在自动排座里。</summary>
public enum SeatCellKind
{
    Seat,
    Aisle,
}

/// <summary>教室的四条边。Front = 讲台那一侧（物理 Row = 0 方向）。</summary>
public enum RoomEdge
{
    Front,
    Back,
    Left,
    Right,
}

/// <param name="Edge">贴在教室的哪一边。</param>
/// <param name="OffsetRatio">沿该边 0–1 的位置比例，0 = 左端 / 上端。</param>
/// <param name="Label">「前门」「后门」，导出时原样印。</param>
public sealed record RoomDoor(RoomEdge Edge, double OffsetRatio, string Label);

/// <summary>
/// 教室画布：哪些格子不坐人、每条边界多宽、门窗在哪。
///
/// **不可变**——所有变换返回新实例。这一点是硬要求：
/// <see cref="SeatChartSettings.Clone"/> 走 MemberwiseClone 会共享同一个引用，
/// 只要有一处就地修改，克隆体就会互相污染。
/// 用 FrozenSet 而不是 HashSet 是因为 ComposeDocument / RenderPng 在后台线程跑，
/// 而 UI 线程可能同时在改画布；FrozenSet 是并发安全的读快照。
/// </summary>
public sealed class SeatChartCanvas
{
    public static SeatChartCanvas Empty { get; } = new();

    /// <summary>不是座位的格子（过道 / 讲台前空地 / 缺角）。</summary>
    public FrozenSet<SeatPosition> DisabledSeats { get; init; } = FrozenSet<SeatPosition>.Empty;

    /// <summary>第 i 排之后的过道高度（单位：格）。长度 = Rows。</summary>
    public ImmutableArray<float> RowCorridors { get; init; } = ImmutableArray<float>.Empty;

    /// <summary>组内第 i 列之后的过道宽度（单位：格）。长度 = Columns。</summary>
    public ImmutableArray<float> ColumnCorridors { get; init; } = ImmutableArray<float>.Empty;

    /// <summary>第 g 组之后的过道宽度（单位：格）。长度 = Groups。</summary>
    public ImmutableArray<float> GroupCorridors { get; init; } = ImmutableArray<float>.Empty;

    public ImmutableArray<RoomDoor> Doors { get; init; } = ImmutableArray<RoomDoor>.Empty;

    public bool IsDisabled(SeatPosition position) => DisabledSeats.Contains(position);

    public bool IsEmpty =>
        DisabledSeats.Count == 0 && RowCorridors.IsDefaultOrEmpty && ColumnCorridors.IsDefaultOrEmpty &&
        GroupCorridors.IsDefaultOrEmpty && Doors.IsDefaultOrEmpty;

    public float RowCorridor(int row) => row >= 0 && row < RowCorridors.Length ? RowCorridors[row] : 0f;

    public float ColumnCorridor(int column) =>
        column >= 0 && column < ColumnCorridors.Length ? ColumnCorridors[column] : 0f;

    public float GroupCorridor(int group) =>
        group >= 0 && group < GroupCorridors.Length ? GroupCorridors[group] : 0f;

    public SeatChartCanvas WithDisabled(SeatPosition position, bool disabled)
    {
        var next = new HashSet<SeatPosition>(DisabledSeats);
        if (disabled) next.Add(position);
        else next.Remove(position);
        return With(DisabledSeats: next.ToFrozenSet());
    }

    public SeatChartCanvas Toggle(SeatPosition position) => WithDisabled(position, !IsDisabled(position));

    public SeatChartCanvas WithRowCorridor(int row, float cells) =>
        With(RowCorridors: SetAt(RowCorridors, row, ClampCorridor(cells)));

    public SeatChartCanvas WithColumnCorridor(int column, float cells) =>
        With(ColumnCorridors: SetAt(ColumnCorridors, column, ClampCorridor(cells)));

    public SeatChartCanvas WithGroupCorridor(int group, float cells) =>
        With(GroupCorridors: SetAt(GroupCorridors, group, ClampCorridor(cells)));

    public SeatChartCanvas WithDoors(ImmutableArray<RoomDoor> doors) => With(Doors: doors);

    /// <summary>按新布局裁剪：越界的禁用格与走廊丢掉，门窗比例夹紧到 0–1。</summary>
    public SeatChartCanvas Normalize(SeatChartLayout layout)
    {
        var seats = DisabledSeats.Where(layout.Contains).ToFrozenSet();
        var rows = Fit(RowCorridors, layout.Rows);
        var columns = Fit(ColumnCorridors, layout.Columns);
        var groups = Fit(GroupCorridors, layout.Groups);
        var doors = Doors.IsDefault
            ? ImmutableArray<RoomDoor>.Empty
            : Doors.Select(d => d with { OffsetRatio = Math.Clamp(d.OffsetRatio, 0, 1) })
                .ToImmutableArray();

        if (seats.Count == DisabledSeats.Count && rows.SequenceEqual(RowCorridors) &&
            columns.SequenceEqual(ColumnCorridors) && groups.SequenceEqual(GroupCorridors) &&
            doors.SequenceEqual(Doors))
            return this;

        return new SeatChartCanvas
        {
            DisabledSeats = seats,
            RowCorridors = rows,
            ColumnCorridors = columns,
            GroupCorridors = groups,
            Doors = doors,
        };
    }

    // 结构相等：两个内容相同但构造顺序不同的画布必须相等。
    public bool Equals(SeatChartCanvas? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return DisabledSeats.Count == other.DisabledSeats.Count
               && DisabledSeats.SetEquals(other.DisabledSeats)
               && RowCorridors.SequenceEqual(other.RowCorridors)
               && ColumnCorridors.SequenceEqual(other.ColumnCorridors)
               && GroupCorridors.SequenceEqual(other.GroupCorridors)
               && Doors.SequenceEqual(other.Doors);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        // 顺序无关：逐个 XOR 元素的哈希。
        var seatHash = 0;
        foreach (var seat in DisabledSeats) seatHash ^= seat.GetHashCode();
        hash.Add(seatHash);
        foreach (var value in RowCorridors) hash.Add(value);
        foreach (var value in ColumnCorridors) hash.Add(value);
        foreach (var value in GroupCorridors) hash.Add(value);
        foreach (var door in Doors) hash.Add(door);
        return hash.ToHashCode();
    }

    public override bool Equals(object? obj) => Equals(obj as SeatChartCanvas);

    private SeatChartCanvas With(
        FrozenSet<SeatPosition>? DisabledSeats = null,
        ImmutableArray<float>? RowCorridors = null,
        ImmutableArray<float>? ColumnCorridors = null,
        ImmutableArray<float>? GroupCorridors = null,
        ImmutableArray<RoomDoor>? Doors = null) => new()
    {
        DisabledSeats = DisabledSeats ?? this.DisabledSeats,
        RowCorridors = RowCorridors ?? this.RowCorridors,
        ColumnCorridors = ColumnCorridors ?? this.ColumnCorridors,
        GroupCorridors = GroupCorridors ?? this.GroupCorridors,
        Doors = Doors ?? this.Doors,
    };

    private static float ClampCorridor(float cells) => Math.Clamp(cells, 0f, 2f);

    private static ImmutableArray<float> SetAt(ImmutableArray<float> source, int index, float value)
    {
        if (index < 0) return source;

        var length = Math.Max(source.Length, index + 1);
        var builder = ImmutableArray.CreateBuilder<float>(length);
        for (var i = 0; i < length; i++)
            builder.Add(i < source.Length ? source[i] : 0f);
        builder[index] = value;
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<float> Fit(ImmutableArray<float> source, int length)
    {
        if (source.IsDefaultOrEmpty) return ImmutableArray<float>.Empty;
        if (source.Length == length) return source;

        var builder = ImmutableArray.CreateBuilder<float>(length);
        for (var i = 0; i < length; i++)
            builder.Add(i < source.Length ? source[i] : 0f);
        return builder.MoveToImmutable();
    }
}
