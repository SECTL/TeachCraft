using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Rendering;

public sealed class SeatChartDocument
{
    public required SeatChartSettings Settings { get; init; }
    public required SeatChartLayout Layout { get; init; }
    public required string Title { get; init; }
    public required string InfoLine { get; init; }
    public required bool PodiumOnTop { get; init; }
    public required IReadOnlyList<DisplayGroup> Groups { get; init; }
    public required int StudentCount { get; init; }

    /// <summary>
    /// 座号（座位 → 1 基座号）。只有考场模式会带，日常座位表是空的。
    /// 空表时渲染、Excel、PPTX 一律不画座号——日常导出的字节与版面都不变。
    /// </summary>
    public IReadOnlyDictionary<SeatPosition, int> SeatNumbers { get; init; } =
        SeatChartComposer.EmptySeatNumbers;
}

public sealed class DisplayGroup
{
    public required int Number { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<DisplayRow> Rows { get; init; }
}

public sealed class DisplayRow
{
    public required int Number { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<DisplaySeat> Seats { get; init; }
}

public sealed class DisplaySeat
{
    public required SeatPosition Position { get; init; }
    public required string Name { get; init; }
    public required Student? Student { get; init; }
    public required bool IsEmpty { get; init; }
    public required bool IsFemale { get; init; }

    /// <summary>座位还是过道。过道格不坐人，渲染与 Excel 都不画边框。</summary>
    public SeatCellKind Kind { get; init; } = SeatCellKind.Seat;

    /// <summary>
    /// 1 基座号。只有考场模式有值；日常座位表恒为 null，渲染与导出都不画。
    /// 注意它属于座位而非学生——空座也有座号。
    /// </summary>
    public int? SeatNumber { get; init; }
}

/// <summary>
/// 把「物理坐标 + 视角」翻译成可绘制的展示顺序：
/// 教师视角讲台在下方、第一排靠下；学生视角整体旋转 180°，讲台在上方。
/// </summary>
public static class SeatChartComposer
{
    /// <summary>没有座号时的只读空表。考场之外全部走它，渲染与导出一个字都不多画。</summary>
    internal static readonly FrozenDictionary<SeatPosition, int> EmptySeatNumbers =
        FrozenDictionary<SeatPosition, int>.Empty;

    public static SeatChartDocument Compose(
        SeatChartSettings settings,
        IReadOnlyDictionary<SeatPosition, Student> assignments,
        int studentCount,
        IReadOnlyDictionary<SeatPosition, int>? seatNumbers = null)
    {
        settings.Normalize();
        var layout = settings.GetLayout();
        var teacher = settings.View == ViewMode.Teacher;
        var columnNumbers = ColumnOrder(layout, settings.View);
        var rowNumbers = RowOrder(layout, settings.View);

        var groups = GroupOrder(layout, settings.View).Select(group =>
        {
            var rows = rowNumbers.Select(row =>
            {
                var seats = columnNumbers.Select(column =>
                {
                    var position = new SeatPosition(group, row, column);
                    assignments.TryGetValue(position, out var student);
                    var aisle = layout.Surface.IsDisabled(position);
                    return new DisplaySeat
                    {
                        Position = position,
                        Student = aisle ? null : student,
                        IsEmpty = aisle || student is null,
                        IsFemale = !aisle && student?.Gender == Gender.Female,
                        Name = aisle || student is null
                            ? string.Empty
                            : DisplayName(student.Name, settings.SpaceTwoCharNames),
                        Kind = aisle ? SeatCellKind.Aisle : SeatCellKind.Seat,
                        // 座号属于座位而不是坐着的人：空座位也编了号，老师要对号布置考桌。
                        SeatNumber = aisle || seatNumbers is null
                            ? null
                            : seatNumbers.TryGetValue(position, out var number) ? number : null,
                    };
                }).ToList();

                return new DisplayRow
                {
                    Number = row + 1,
                    Label = $"第{row + 1}排",
                    Seats = seats,
                };
            }).ToList();

            return new DisplayGroup
            {
                Number = group + 1,
                Label = $"第{group + 1}组",
                Rows = rows,
            };
        }).ToList();

        var infoParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.HeadTeacher))
            infoParts.Add($"班主任：{settings.HeadTeacher.Trim()}");
        infoParts.Add($"人数：{studentCount}");
        if (!string.IsNullOrWhiteSpace(settings.Monitor))
            infoParts.Add($"班长：{settings.Monitor.Trim()}");

        return new SeatChartDocument
        {
            Settings = settings,
            Layout = layout,
            Title = string.IsNullOrWhiteSpace(settings.Title) ? "座位表" : settings.Title.Trim(),
            InfoLine = string.Join("　　", infoParts),
            PodiumOnTop = !teacher,
            Groups = groups,
            StudentCount = studentCount,
            SeatNumbers = seatNumbers ?? EmptySeatNumbers,
        };
    }

    public static string DisplayName(string name, bool spaceTwoCharNames)
    {
        if (!spaceTwoCharNames) return name;

        var trimmed = name.Trim();
        return trimmed.Length == 2 && trimmed.All(IsCjk)
            ? $"{trimmed[0]} {trimmed[1]}"
            : trimmed;
    }

    private static bool IsCjk(char c) => c is >= '\u4e00' and <= '\u9fff';

    // ----------------------------- \u5c4f\u5e55\u6446\u653e\u987a\u5e8f -----------------------------
    // \u754c\u9762\u3001PNG / PDF\u3001Excel \u4e0e\u6574\u73ed\u5e73\u79fb\u5171\u7528\u8fd9\u4e09\u4efd\u987a\u5e8f\uff0c\u4e0d\u5f97\u5404\u5199\u4e00\u5957\u3002
    // \u6559\u5e08\u89c6\u89d2\uff1a\u7b2c 1 \u7ec4\u3001\u7ec4\u5185\u7b2c 1 \u5217\u5728\u6700\u5de6\uff0c\u7b2c\u4e00\u6392\u5728\u6700\u4e0b\uff1b
    // \u5b66\u751f\u89c6\u89d2\u662f\u6559\u5e08\u89c6\u89d2\u6574\u4f53\u65cb\u8f6c 180\u00b0\uff1a\u7ec4\u5e8f\u3001\u7ec4\u5185\u5217\u5e8f\u3001\u6392\u5e8f\u5168\u90e8\u53cd\u8f6c\uff08\u540c\u684c\u5de6\u53f3\u4e5f\u968f\u4e4b\u5bf9\u8c03\uff09\u3002

    /// <summary>\u4ece\u5de6\u5230\u53f3\u4f9d\u6b21\u6446\u653e\u7684\u7269\u7406\u7ec4\u53f7\uff080 \u57fa\uff09\u3002</summary>
    public static int[] GroupOrder(SeatChartLayout layout, ViewMode view) =>
        Ordered(layout.Groups, reverse: view == ViewMode.Student);

    /// <summary>\u7ec4\u5185\u4ece\u5de6\u5230\u53f3\u4f9d\u6b21\u6446\u653e\u7684\u7269\u7406\u5217\u53f7\uff080 \u57fa\uff09\u3002</summary>
    public static int[] ColumnOrder(SeatChartLayout layout, ViewMode view) =>
        Ordered(layout.Columns, reverse: view == ViewMode.Student);

    /// <summary>\u4ece\u4e0a\u5230\u4e0b\u4f9d\u6b21\u6446\u653e\u7684\u7269\u7406\u6392\u53f7\uff080 \u57fa\uff0c0 = \u9760\u8fd1\u8bb2\u53f0\uff09\u3002</summary>
    public static int[] RowOrder(SeatChartLayout layout, ViewMode view) =>
        Ordered(layout.Rows, reverse: view == ViewMode.Teacher);

    /// <summary>
    /// \u6574\u73ed\u6cbf\u5c4f\u5e55\u65b9\u5411\u5e73\u79fb\u4e00\u683c\uff1adx &gt; 0 \u5411\u53f3\u3001dy &gt; 0 \u5411\u4e0b\uff0c\u8d85\u51fa\u8fb9\u7f18\u81ea\u52a8\u7ed5\u56de\u3002
    /// \u65b9\u5411\u6309\u5f53\u524d\u89c6\u89d2\u7684\u5c4f\u5e55\u6446\u653e\u6362\u7b97\uff0c\u4e0e\u754c\u9762\u770b\u5230\u7684\u4e00\u81f4\u3002
    /// </summary>
    public static Dictionary<SeatPosition, T> ShiftOnScreen<T>(
        IReadOnlyDictionary<SeatPosition, T> assignments, SeatChartLayout layout, ViewMode view, int dx, int dy)
    {
        var shifted = ShiftOnScreen(assignments, layout, view, dx, dy, out _);
        return shifted;
    }

    /// <summary>
    /// \u540c\u4e0a\uff0c\u5e76\u62a5\u544a\u300c\u56e0\u76ee\u6807\u683c\u662f\u8fc7\u9053\u800c\u7559\u5728\u539f\u5730\u300d\u7684\u4eba\u6570\u3002
    /// \u6574\u73ed\u5e73\u79fb\u7684\u9884\u671f\u662f\u300c\u6240\u6709\u4eba\u4e00\u8d77\u632a\u4e00\u683c\u300d\uff1b\u82e5\u76ee\u6807\u683c\u662f\u8fc7\u9053\u5c31\u7ee7\u7eed\u5f80\u540e\u627e\u7a7a\u4f4d\uff0c
    /// \u4f1a\u9020\u6210\u4e0d\u540c\u5b66\u751f\u632a\u7684\u683c\u6570\u4e0d\u540c\u3001\u76f8\u5bf9\u987a\u5e8f\u88ab\u6253\u4e71\uff0c\u6bd4\u4e0d\u52a8\u66f4\u7cdf\u3002
    /// </summary>
    public static Dictionary<SeatPosition, T> ShiftOnScreen<T>(
        IReadOnlyDictionary<SeatPosition, T> assignments, SeatChartLayout layout, ViewMode view,
        int dx, int dy, out int blocked)
    {
        var groups = GroupOrder(layout, view);
        var columns = ColumnOrder(layout, view);
        var rows = RowOrder(layout, view);

        // \u6a2a\u5411\u628a\u5404\u7ec4\u62fc\u6210\u4e00\u6761\uff1a\u5c4f\u5e55\u7b2c i \u683c = (groups[i / \u5217\u6570], columns[i % \u5217\u6570])\u3002
        var width = groups.Length * columns.Length;
        var shifted = new Dictionary<SeatPosition, T>(assignments.Count);
        blocked = 0;

        foreach (var (position, value) in assignments)
        {
            var x = Array.IndexOf(groups, position.Group) * columns.Length + Array.IndexOf(columns, position.Column);
            var y = Array.IndexOf(rows, position.Row);
            if (x < 0 || y < 0)
            {
                shifted[position] = value; // \u4e0d\u5728\u5f53\u524d\u5e03\u5c40\u5185\u7684\u5ea7\u4f4d\u539f\u6837\u4fdd\u7559\uff0c\u4e0d\u4e22\u4eba
                continue;
            }

            x = ((x + dx) % width + width) % width;
            y = ((y + dy) % rows.Length + rows.Length) % rows.Length;
            var target = new SeatPosition(groups[x / columns.Length], rows[y], columns[x % columns.Length]);

            if (!layout.IsUsable(target))
            {
                shifted[position] = value;   // \u76ee\u6807\u683c\u662f\u8fc7\u9053\uff1a\u539f\u5730\u4e0d\u52a8\u5e76\u62a5\u6570
                blocked++;
                continue;
            }

            shifted[target] = value;
        }

        return shifted;
    }

    /// <summary>\u5de6\u53f3\u955c\u50cf\uff1a\u6bcf\u7ec4\u5185\u7b2c c \u5217\u4e0e\u7b2c Columns-1-c \u5217\u4e92\u6362\u5b66\u751f\u3002\u7269\u7406\u53d8\u6362\uff0c\u4e0d\u662f\u89c6\u89d2\u3002</summary>
    public static Dictionary<SeatPosition, T> MirrorColumns<T>(
        IReadOnlyDictionary<SeatPosition, T> assignments, SeatChartLayout layout, out int blocked)
    {
        var mirrored = new Dictionary<SeatPosition, T>(assignments.Count);
        blocked = 0;

        foreach (var (position, value) in assignments)
        {
            if (!layout.Contains(position))
            {
                mirrored[position] = value;
                continue;
            }

            var target = position with { Column = layout.Columns - 1 - position.Column };
            if (!layout.IsUsable(target))
            {
                mirrored[position] = value;
                blocked++;
                continue;
            }

            mirrored[target] = value;
        }

        return mirrored;
    }

    /// <summary>\u7ec4\u5e8f\u955c\u50cf\uff1a\u7b2c g \u7ec4\u4e0e\u7b2c Groups-1-g \u7ec4\u4e92\u6362\u5b66\u751f\u3002</summary>
    public static Dictionary<SeatPosition, T> MirrorGroups<T>(
        IReadOnlyDictionary<SeatPosition, T> assignments, SeatChartLayout layout, out int blocked)
    {
        var mirrored = new Dictionary<SeatPosition, T>(assignments.Count);
        blocked = 0;

        foreach (var (position, value) in assignments)
        {
            if (!layout.Contains(position))
            {
                mirrored[position] = value;
                continue;
            }

            var target = position with { Group = layout.Groups - 1 - position.Group };
            if (!layout.IsUsable(target))
            {
                mirrored[position] = value;
                blocked++;
                continue;
            }

            mirrored[target] = value;
        }

        return mirrored;
    }

    /// <summary>
    /// 周轮转：每组整体移到下一组（dx = +1）或上一组（dx = −1），最外侧绕回。
    /// 与 <see cref="ShiftOnScreen"/> 的区别：这里只换 Group，Row / Column 不动，
    /// 方向按物理组号而不是视角——物理上确实整体挪了一组，学生视角看到反向是对的。
    /// </summary>
    public static Dictionary<SeatPosition, T> RotateGroups<T>(
        IReadOnlyDictionary<SeatPosition, T> assignments, SeatChartLayout layout, int dx)
    {
        var shifted = new Dictionary<SeatPosition, T>(assignments.Count);
        var groups = layout.Groups;
        if (groups <= 1 || dx % groups == 0)
        {
            foreach (var (position, value) in assignments) shifted[position] = value;
            return shifted;
        }

        foreach (var (position, value) in assignments)
        {
            if (!layout.Contains(position))
            {
                shifted[position] = value;   // 不在布局内的座位原样保留，不丢人
                continue;
            }

            var group = ((position.Group + dx) % groups + groups) % groups;
            var target = position with { Group = group };
            if (layout.IsUsable(target)) shifted[target] = value;
            else shifted[position] = value;   // 目标格是过道：原地不动，由调用方报数
        }

        return shifted;
    }

    private static int[] Ordered(int count, bool reverse)
    {
        var order = Enumerable.Range(0, count).ToArray();
        if (reverse) Array.Reverse(order);
        return order;
    }
}
