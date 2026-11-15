using System;
using System.Collections.Generic;
using System.Linq;

namespace SeatGrid.Core.Models;

/// <summary>物理座位坐标：第几组、第几排（0 表示靠近讲台）、第几列。</summary>
public readonly record struct SeatPosition(int Group, int Row, int Column)
{
    public override string ToString() => $"{Group + 1} 组 · {Row + 1} 排 · {Column + 1} 列";
}

public sealed record SeatChartLayout(int Groups, int Rows, int Columns, SeatChartCanvas? Canvas = null)
{
    /// <summary>画布（过道 / 走廊 / 门窗）。未设置时是空画布，行为与旧版完全一致。</summary>
    public SeatChartCanvas Surface => Canvas ?? SeatChartCanvas.Empty;

    /// <summary>矩形格数（画布要画满整个矩形）。</summary>
    public int CellCount => Groups * Rows * Columns;

    /// <summary>可坐座位数 = 矩形格数减去落在矩形内的过道格。状态栏的「N 座」用它。</summary>
    public int UsableSeatCount
    {
        get
        {
            if (Surface.IsEmpty) return CellCount;

            var disabled = 0;
            foreach (var seat in Surface.DisabledSeats)
                if (Contains(seat)) disabled++;
            return CellCount - disabled;
        }
    }

    /// <summary>兼容旧名：语义等同 <see cref="UsableSeatCount"/>。</summary>
    public int SeatCount => UsableSeatCount;

    /// <summary>每排的双人桌数：2 列 = 1 桌，3 列 = 1 桌 + 1 个落单，4 列 = 2 桌。</summary>
    public int DeskPairCount => Columns / 2;

    public bool Contains(SeatPosition position) =>
        position.Group >= 0 && position.Group < Groups &&
        position.Row >= 0 && position.Row < Rows &&
        position.Column >= 0 && position.Column < Columns;

    /// <summary>矩形内、且不是过道。</summary>
    public bool IsUsable(SeatPosition position) => Contains(position) && !Surface.IsDisabled(position);

    /// <summary>
    /// 同桌：同组同排、列号落在同一对双人桌（col / 2 相同）。不跨组、不跨排。
    /// Columns = 1 时没有同桌；Columns = 3 时第 3 列落单——两者都返回 null。
    /// </summary>
    public SeatPosition? DeskmateOf(SeatPosition position)
    {
        var pairStart = position.Column - position.Column % 2;
        if (pairStart + 1 >= Columns) return null;
        return position with { Column = position.Column == pairStart ? pairStart + 1 : pairStart };
    }

    public bool AreDeskmates(SeatPosition a, SeatPosition b) =>
        a.Group == b.Group && a.Row == b.Row && a.Column != b.Column && a.Column / 2 == b.Column / 2;

    /// <summary>
    /// 前后左右四个物理邻座。组与组之间是过道，物理上不相邻，因此不跨组；也不含对角。
    /// Columns = 1 时左右邻自然消失，只剩前后邻。
    /// </summary>
    public IEnumerable<SeatPosition> NeighborsOf(SeatPosition position)
    {
        if (position.Row > 0) yield return position with { Row = position.Row - 1 };
        if (position.Row + 1 < Rows) yield return position with { Row = position.Row + 1 };
        if (position.Column > 0) yield return position with { Column = position.Column - 1 };
        if (position.Column + 1 < Columns) yield return position with { Column = position.Column + 1 };
    }

    public bool AreAdjacent(SeatPosition a, SeatPosition b) =>
        a.Group == b.Group &&
        ((a.Row == b.Row && Math.Abs(a.Column - b.Column) == 1) ||
         (a.Column == b.Column && Math.Abs(a.Row - b.Row) == 1));

    /// <summary>
    /// 排座时的填充顺序：先填靠近讲台的一排，再从左到右跨组，最后才往后退。
    /// 这样按身高 / 视力排序时，前排优先的语义才成立。过道格被跳过。
    /// </summary>
    public IEnumerable<SeatPosition> FillOrder()
    {
        for (var row = 0; row < Rows; row++)
        {
            for (var group = 0; group < Groups; group++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    var seat = new SeatPosition(group, row, column);
                    if (IsUsable(seat)) yield return seat;
                }
            }
        }
    }

    /// <summary>矩形内全部格子（含过道）——画布要画满，所以不过滤。</summary>
    public IEnumerable<SeatPosition> AllSeats()
    {
        for (var group = 0; group < Groups; group++)
        {
            for (var row = 0; row < Rows; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    yield return new SeatPosition(group, row, column);
                }
            }
        }
    }

    /// <summary>只列可坐的格子（组 → 排 → 列）。</summary>
    public IEnumerable<SeatPosition> AllUsableSeats() => AllSeats().Where(IsUsable);

    public static SeatChartLayout FromSettings(SeatChartSettings settings) =>
        new(settings.Groups, settings.Rows, settings.Columns, settings.Canvas);
}
