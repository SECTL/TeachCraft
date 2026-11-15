using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Seating;

/// <summary>
/// 座位矩形（全部 0 基、闭区间）。From 大于 To 时构造器自动交换，越界在 Normalize 里夹紧。
/// </summary>
public readonly record struct SeatRect(
    int GroupFrom, int GroupTo, int RowFrom, int RowTo, int ColumnFrom, int ColumnTo)
{
    public bool Contains(SeatPosition p) =>
        p.Group >= GroupFrom && p.Group <= GroupTo &&
        p.Row >= RowFrom && p.Row <= RowTo &&
        p.Column >= ColumnFrom && p.Column <= ColumnTo;

    /// <summary>夹紧到布局范围内；区间方向反了先交换。布局小于 1 时退化为单个 (0,0,0)。</summary>
    public SeatRect Normalize(SeatChartLayout layout)
    {
        var g0 = Math.Min(GroupFrom, GroupTo);
        var g1 = Math.Max(GroupFrom, GroupTo);
        var r0 = Math.Min(RowFrom, RowTo);
        var r1 = Math.Max(RowFrom, RowTo);
        var c0 = Math.Min(ColumnFrom, ColumnTo);
        var c1 = Math.Max(ColumnFrom, ColumnTo);

        return new SeatRect(
            Math.Clamp(g0, 0, Math.Max(0, layout.Groups - 1)),
            Math.Clamp(g1, 0, Math.Max(0, layout.Groups - 1)),
            Math.Clamp(r0, 0, Math.Max(0, layout.Rows - 1)),
            Math.Clamp(r1, 0, Math.Max(0, layout.Rows - 1)),
            Math.Clamp(c0, 0, Math.Max(0, layout.Columns - 1)),
            Math.Clamp(c1, 0, Math.Max(0, layout.Columns - 1)));
    }

    public static SeatRect RowBand(SeatChartLayout layout, int fromRow, int toRow) =>
        new(0, layout.Groups - 1, fromRow, toRow, 0, layout.Columns - 1);

    public static SeatRect GroupBand(SeatChartLayout layout, int group) =>
        new(group, group, 0, layout.Rows - 1, 0, layout.Columns - 1);

    public static SeatRect ColumnBand(SeatChartLayout layout, int column) =>
        new(0, layout.Groups - 1, 0, layout.Rows - 1, column, column);
}

/// <summary>一名学生的座位区域：Allowed 非空时取交集，再减去 Denied。</summary>
public sealed record StudentZone(
    IReadOnlyList<SeatRect> Allowed,
    IReadOnlyList<SeatRect> Denied)
{
    public static StudentZone Empty { get; } = new(Array.Empty<SeatRect>(), Array.Empty<SeatRect>());

    public bool IsEmpty => Allowed.Count == 0 && Denied.Count == 0;

    public bool Allows(SeatPosition p)
    {
        if (Allowed.Count > 0 && !Allowed.Any(r => r.Contains(p))) return false;
        return !Denied.Any(r => r.Contains(p));
    }

    public StudentZone Normalize(SeatChartLayout layout) => new(
        Allowed.Select(r => r.Normalize(layout)).ToList(),
        Denied.Select(r => r.Normalize(layout)).ToList());
}
