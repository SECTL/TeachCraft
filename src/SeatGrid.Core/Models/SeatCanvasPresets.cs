using System;
using System.Collections.Immutable;

namespace SeatGrid.Core.Models;

/// <summary>
/// 异形教室的快捷预设。
///
/// 这些不是「形状」概念——U 型 / 多岛 / 长短排全都能由禁用格 + 走廊拼出来，
/// 预设只是「一次性把 canvas 调成某个样子」的快捷方式，产出的仍是普通
/// <see cref="SeatChartCanvas"/>。因此它零持久化成本，也不会与手工改的格子失配。
/// </summary>
public static class SeatCanvasPresets
{
    /// <summary>
    /// U 型：把中间若干组靠讲台（或靠后墙）的前几排设为过道，并把中间那条组间过道加宽。
    /// 组数少于 3 时返回 null——不产出半张图。
    /// </summary>
    public static SeatChartCanvas? UShape(SeatChartLayout layout, bool openTowardPodium = false)
    {
        if (layout.Groups < 3) return null;

        var canvas = layout.Surface;
        var middleStart = layout.Groups / 2 - 1;
        var middleCount = Math.Max(1, layout.Groups / 2);
        var depth = Math.Max(1, layout.Rows / 2);

        for (var group = middleStart; group < middleStart + middleCount && group < layout.Groups; group++)
        {
            for (var i = 0; i < depth; i++)
            {
                var row = openTowardPodium ? layout.Rows - 1 - i : i;
                if (row < 0 || row >= layout.Rows) continue;
                for (var column = 0; column < layout.Columns; column++)
                    canvas = canvas.WithDisabled(new SeatPosition(group, row, column), true);
            }
        }

        // 中间那条组间过道加宽，让 U 的两条竖边更明显。
        var corridorGroup = Math.Min(layout.Groups - 2, middleStart);
        return canvas.WithGroupCorridor(corridorGroup, 1.0f);
    }

    /// <summary>多岛：每 islandGroups 组为一岛，岛与岛之间的过道加宽到 1.2 格。</summary>
    public static SeatChartCanvas? Islands(SeatChartLayout layout, int islandGroups = 2)
    {
        if (layout.Groups < islandGroups * 2) return null;

        var canvas = layout.Surface;
        for (var group = islandGroups - 1; group < layout.Groups - 1; group += islandGroups)
            canvas = canvas.WithGroupCorridor(group, 1.2f);

        return canvas;
    }

    /// <summary>长短排：偶数排末尾少若干座（把最后一列之后的若干格设为过道）。</summary>
    public static SeatChartCanvas? StaggeredRows(SeatChartLayout layout, int shortBy = 1)
    {
        if (layout.Columns < 2) return null;

        var canvas = layout.Surface;
        var take = Math.Min(shortBy, layout.Columns - 1);
        for (var row = 1; row < layout.Rows; row += 2)
        {
            for (var i = 0; i < take; i++)
            {
                var column = layout.Columns - 1 - i;
                for (var group = 0; group < layout.Groups; group++)
                    canvas = canvas.WithDisabled(new SeatPosition(group, row, column), true);
            }
        }

        return canvas;
    }

    /// <summary>报告厅式：后若干排整排设为过道（只坐前半段）。</summary>
    public static SeatChartCanvas? FrontFilled(SeatChartLayout layout, int emptyRows = 2)
    {
        if (layout.Rows <= emptyRows) return null;

        var canvas = layout.Surface;
        for (var i = 0; i < emptyRows; i++)
        {
            var row = layout.Rows - 1 - i;
            for (var group = 0; group < layout.Groups; group++)
                for (var column = 0; column < layout.Columns; column++)
                    canvas = canvas.WithDisabled(new SeatPosition(group, row, column), true);
        }

        return canvas;
    }
}
