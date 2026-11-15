using System;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Rendering;

/// <summary>
/// 几何的唯一真相：格子大小、各边界间距、整张表的宽高。
///
/// 此前这套计算有三份互不相等的副本——XAML 的 <c>Spacing=6/22</c>、
/// <c>SeatGridViewModel.UpdatePodium</c> 的硬编码 <c>6/22/44</c>、
/// 以及 <see cref="SeatChartRenderer"/> 的 <c>cell*0.07 / cell*0.28</c>
/// （<c>0.07*80 = 5.6</c> 而 XAML 写 6）。三份各自演化，「界面与导出一致」
/// 这条约束就破了。现在界面、PNG / PDF、Excel 全部读这里。
///
/// 偏移数组按**显示序**生成：学生视角下排是倒的，若按物理序算偏移再倒着画，
/// 最后一条排距会跑到最前面。把 view 传进来直接产出显示序数组，三处循环都是
/// 天然的显示序，按下标取即可。
/// </summary>
public static class SeatChartGeometry
{
    /// <summary>默认列距（格子比例）。等于旧渲染器的 0.07。</summary>
    public const float DefaultColumnGapCells = 0.07f;

    /// <summary>默认组距（格子比例）。等于旧渲染器的 0.28。</summary>
    public const float DefaultGroupGapCells = 0.28f;

    /// <summary>默认排距（格子比例）。等于旧渲染器的 0.07。</summary>
    public const float DefaultRowGapCells = 0.07f;

    public const float MinGapPx = 3f;
    public const float GroupGapMinPx = 16f;

    /// <summary>
    /// 按显示序排好的前缀和。Column[i] 是第 i 个显示列相对组内原点的左边界，
    /// 长度 = Columns + 1（末项 = 组宽）；Row 同理；Group 长度 = Groups + 1。
    /// </summary>
    public readonly record struct Offsets(
        float[] Column,
        float[] Row,
        float[] Group,
        float Cell,
        float GroupWidth,
        float GridWidth,
        float GridHeight);

    public static Offsets Measure(SeatChartLayout layout, SeatChartSettings settings, ViewMode view)
    {
        var cell = settings.CellSizePx;
        var groups = SeatChartComposer.GroupOrder(layout, view);
        var columns = SeatChartComposer.ColumnOrder(layout, view);
        var rows = SeatChartComposer.RowOrder(layout, view);

        var columnOffsets = new float[columns.Length + 1];
        for (var i = 0; i < columns.Length; i++)
        {
            columnOffsets[i + 1] = columnOffsets[i] + cell;

            // 列过道挂在它后面那一段上（最后一条也要算）。
            columnOffsets[i + 1] += CorridorPx(layout.Surface.ColumnCorridor(columns[i]), cell);

            // 默认列距只在「还有下一列」时计入。
            if (i + 1 < columns.Length)
                columnOffsets[i + 1] += DefaultColumnGapPx(settings, cell);
        }

        var groupWidth = columnOffsets[^1];

        var groupOffsets = new float[groups.Length + 1];
        for (var i = 0; i < groups.Length; i++)
        {
            groupOffsets[i + 1] = groupOffsets[i] + groupWidth;

            // 组间过道挂在它后面那一段上（最后一组也要算，走廊才能显示）。
            groupOffsets[i + 1] += CorridorPx(layout.Surface.GroupCorridor(groups[i]), cell, GroupGapMinPx);

            // 默认组距只在「还有下一组」时计入。
            if (i + 1 < groups.Length)
                groupOffsets[i + 1] += DefaultGroupGapPx(settings, cell);
        }

        var gridWidth = groupOffsets[^1];

        var rowOffsets = new float[rows.Length + 1];
        var tierStep = cell * settings.TierRowGrowthPercent / 100f;
        for (var i = 0; i < rows.Length; i++)
        {
            // 阶梯从前往后加深：一律按物理排号算深度。
            // 教师视角的显示序是倒的（讲台在下），用 i 会让阶梯反向。
            var depth = rows[i];
            rowOffsets[i + 1] = rowOffsets[i] + cell + depth * tierStep;

            // 过道挂在它后面那一段上：即使这是最后一排也要算（否则走廊在末尾会消失）。
            rowOffsets[i + 1] += RowCorridorPx(layout, rows[i], cell);

            // 默认排距只在「还有下一排」时计入，避免表底多出一段空白。
            if (i + 1 < rows.Length)
                rowOffsets[i + 1] += DefaultRowGapPx(settings, cell);
        }

        var gridHeight = rowOffsets[^1];

        return new Offsets(columnOffsets, rowOffsets, groupOffsets, cell, groupWidth, gridWidth, gridHeight);
    }

    /// <summary>组内列距：默认百分比 + 该列之后的过道，取较大值与最小像素。</summary>
    public static float ColumnGapPx(SeatChartSettings settings, SeatChartLayout layout, int column, float cell) =>
        Math.Max(MinGapPx, (settings.ColumnGapPercent / 100f + layout.Surface.ColumnCorridor(column)) * cell);

    /// <summary>组间距：默认百分比 + 该组之后的过道，下限 16px（沿用旧渲染器）。</summary>
    public static float GroupGapPx(SeatChartSettings settings, SeatChartLayout layout, int group, float cell) =>
        Math.Max(GroupGapMinPx, (settings.GroupGapPercent / 100f + layout.Surface.GroupCorridor(group)) * cell);

    /// <summary>只有过道、不含默认间距的那部分（挂在最后一段也要算）。</summary>
    private static float CorridorPx(float cells, float cell, float minimum = MinGapPx) =>
        cells <= 0 ? 0f : Math.Max(minimum, cells * cell);

    private static float DefaultColumnGapPx(SeatChartSettings settings, float cell) =>
        Math.Max(MinGapPx, settings.ColumnGapPercent / 100f * cell);

    private static float DefaultGroupGapPx(SeatChartSettings settings, float cell) =>
        Math.Max(GroupGapMinPx, settings.GroupGapPercent / 100f * cell);

    /// <summary>排距：默认百分比 + 该排之后的过道。</summary>
    public static float RowGapPx(SeatChartSettings settings, SeatChartLayout layout, int row, float cell) =>
        Math.Max(MinGapPx, (settings.RowGapPercent / 100f + layout.Surface.RowCorridor(row)) * cell);

    /// <summary>只有过道、不含默认排距的那部分（挂在最后一段也要算）。</summary>
    private static float RowCorridorPx(SeatChartLayout layout, int row, float cell)
    {
        var cells = layout.Surface.RowCorridor(row);
        return cells <= 0 ? 0f : Math.Max(MinGapPx, cells * cell);
    }

    /// <summary>默认排距（不含逐边界过道）。</summary>
    private static float DefaultRowGapPx(SeatChartSettings settings, float cell) =>
        Math.Max(MinGapPx, settings.RowGapPercent / 100f * cell);
}
