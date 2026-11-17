using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;
using SeatGrid.Core.Seating;

namespace SeatGrid.Core.Export;

/// <summary>把当前座位表导出成一份带颜色与边框的 Excel，并附一张完整名单。</summary>
public static class SeatChartExcelExporter
{
    public static void ExportSeatChart(
        SeatChartDocument document,
        IReadOnlyDictionary<SeatPosition, Student> assignments,
        IReadOnlyList<Student> students,
        string path,
        ArrangeConstraints? constraints = null,
        bool includeSettingsSheet = true)
    {
        var settings = document.Settings;
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SeatGridConfigIO.ChartSheetName);

        var hints = settings.ShowGroupRowHints;
        var firstSeatColumn = hints ? 2 : 1;

        // 过道 / 走廊占位：只在真的设了过道时才插入额外的列与行，
        // 保证「不设过道」时输出与旧版逐格一致（既有测试与导回都依赖这一点）。
        var layout = document.Layout;
        var extraColumnAfter = BuildExtraColumns(document);
        var extraRowAfter = BuildExtraRows(document);

        var groupStarts = new int[layout.Groups];
        var columnOf = new int[layout.Columns];
        var running = firstSeatColumn;
        for (var group = 0; group < layout.Groups; group++)
        {
            groupStarts[group] = running;
            for (var column = 0; column < layout.Columns; column++)
            {
                if (group == 0) columnOf[column] = running + column;
                running++;
                var extra = extraColumnAfter(group, column);
                if (extra > 0) running += extra;
            }

            if (group < layout.Groups - 1) running += 1;   // 组与组之间固定的 1 列空档
        }

        var lastColumn = Math.Max(firstSeatColumn, running - 1);

        var cursor = 1;

        // 标题
        var titleCell = sheet.Cell(cursor, 1);
        sheet.Range(cursor, 1, cursor, lastColumn).Merge();
        titleCell.Value = document.Title;
        titleCell.Style.Font.FontName = "Microsoft YaHei";
        titleCell.Style.Font.FontSize = 18;
        titleCell.Style.Font.Bold = settings.BoldTitle;
        titleCell.Style.Font.FontColor = XLColor.FromHtml("#141413");
        titleCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(cursor).Height = 32;
        cursor++;

        // 信息行
        var infoCell = sheet.Cell(cursor, 1);
        sheet.Range(cursor, 1, cursor, lastColumn).Merge();
        infoCell.Value = document.InfoLine;
        infoCell.Style.Font.FontName = "Microsoft YaHei";
        infoCell.Style.Font.FontSize = 9.5;
        infoCell.Style.Font.FontColor = XLColor.FromHtml("#85806F");
        infoCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        infoCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(cursor).Height = 18;
        cursor++;

        if (settings.ShowGroupRowHints)
            sheet.Column(1).Width = 6;

        for (var group = 0; group < layout.Groups; group++)
        {
            var groupStart = groupStarts[group];
            for (var column = 0; column < layout.Columns; column++)
            {
                var excelColumn = groupStart + column + OffsetWithinGroup(extraColumnAfter, group, column);
                sheet.Column(excelColumn).Width = Math.Max(6, settings.CellSizePx / 7.0);
            }

            if (group < layout.Groups - 1)
            {
                var spacer = groupStart + layout.Columns + OffsetWithinGroup(extraColumnAfter, group, layout.Columns - 1);
                sheet.Column(spacer).Width = 2;
            }
        }

        if (document.PodiumOnTop)
        {
            DrawPodium(sheet, cursor, firstSeatColumn, lastColumn, settings);
            cursor++;
        }

        // 组标题
        if (settings.ShowGroupRowHints)
        {
            for (var groupIndex = 0; groupIndex < document.Groups.Count; groupIndex++)
            {
                var group = document.Groups[groupIndex];
                var start = groupStarts[groupIndex];
                var end = start + layout.Columns - 1 + OffsetWithinGroup(extraColumnAfter, groupIndex, layout.Columns - 1);
                sheet.Range(cursor, start, cursor, end).Merge();
                var cell = sheet.Cell(cursor, start);
                cell.Value = group.Label;
                cell.Style.Font.FontName = "Microsoft YaHei";
                cell.Style.Font.FontSize = 9;
                cell.Style.Font.FontColor = XLColor.FromHtml(settings.GroupLabel);
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            sheet.Row(cursor).Height = 18;
            cursor++;
        }

        // 座位
        var rowCount = document.Groups.Count == 0 ? 0 : document.Groups[0].Rows.Count;

        for (var displayRow = 0; displayRow < rowCount; displayRow++)
        {
            if (settings.ShowGroupRowHints && document.Groups.Count > 0)
            {
                var labelCell = sheet.Cell(cursor, 1);
                labelCell.Value = document.Groups[0].Rows[displayRow].Label;
                labelCell.Style.Font.FontName = "Microsoft YaHei";
                labelCell.Style.Font.FontSize = 8.5;
                labelCell.Style.Font.FontColor = XLColor.FromHtml(settings.RowLabel);
                labelCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                labelCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }

            for (var groupIndex = 0; groupIndex < document.Groups.Count; groupIndex++)
            {
                var group = document.Groups[groupIndex];
                var row = group.Rows[displayRow];
                var groupStart = groupStarts[groupIndex];

                for (var columnIndex = 0; columnIndex < row.Seats.Count; columnIndex++)
                {
                    var seat = row.Seats[columnIndex];
                    var cell = sheet.Cell(cursor, groupStart + columnIndex +
                                                  OffsetWithinGroup(extraColumnAfter, groupIndex, columnIndex));

                    // 过道格：不写文字、不填底色、不画边框——与 PNG 里的留白一致。
                    if (seat.Kind == SeatCellKind.Aisle)
                    {
                        cell.Value = string.Empty;
                        continue;
                    }

                    cell.Style.Font.FontName = "Microsoft YaHei";
                    cell.Style.Font.FontSize = Math.Max(7, settings.NameFontSize * 0.75);
                    cell.Style.Font.Bold = settings.BoldNames;
                    cell.Style.Font.FontColor = XLColor.FromHtml(
                        !seat.IsEmpty && settings.MarkFemales && seat.IsFemale
                            ? settings.FemaleMark
                            : settings.SeatText);

                    // 文字放在样式之后写：考场表用富文本（姓名 + 换行小字座号），
                    // 每个 run 自带字体，样式写在前面才不会被后续赋值覆盖。
                    WriteSeatText(cell, seat, settings);

                    if (!seat.IsEmpty)
                    {
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(settings.SeatBackground);
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.OutsideBorderColor = XLColor.FromHtml(settings.SeatBorder);
                    }
                    else if (!settings.HideEmptySeats)
                    {
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(settings.EmptySeatBackground);
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Dashed;
                        cell.Style.Border.OutsideBorderColor = XLColor.FromHtml(settings.SeatBorder);
                    }

                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    cell.Style.Alignment.WrapText = seat.SeatNumber is not null;
                }
            }

            sheet.Row(cursor).Height = Math.Max(22, settings.CellSizePx * 0.75);
            cursor++;

            // 该排之后的过道：插一行窄高占位，与 PNG 里的走廊视觉对应。
            var extraRows = extraRowAfter(displayRow);
            for (var i = 0; i < extraRows; i++)
            {
                sheet.Row(cursor).Height = Math.Max(3, settings.CellSizePx * 0.35);
                cursor++;
            }
        }

        if (!document.PodiumOnTop)
        {
            DrawPodium(sheet, cursor, firstSeatColumn, lastColumn, settings);
            cursor++;
        }

        // 门窗：ClosedXML 没有形状 API，Excel 端只能落成一行文字。
        // PNG / PDF 里是画在网格外沿的门洞线段——两边的形态本来就不同，不是 bug。
        DrawDoorLabels(sheet, document, cursor, firstSeatColumn, lastColumn);

        // 名单页与配置共用写法；再附一张隐藏的「座位设置」，导回时布局、配色与座位都能还原。
        // 隐藏表放在最后，打开文件时停在座位表上。
        //
        // 考场快照（带座号）另说：它的布局是单组单人桌，列数可能超过日常 4 列的上限，
        // 导回来只会被夹成日常布局、后排学生整片报「超出布局」。所以照样写设置表，
        // 但打上 ExamSnapshot 标记——导入侧见到标记就如实拒绝，绝不产出一份「导回来变成日常」的配置。
        // 约束表同理不写：考场下锁定 / 互斥 / 结对 / 区域本来就不生效。
        var examSnapshot = document.SeatNumbers.Count > 0;
        SeatGridConfigIO.WriteRosterSheet(workbook, students, assignments, withIndex: true);
        if (includeSettingsSheet)
            SeatGridConfigIO.WriteSettingsSheet(workbook, settings, examSnapshot).Hide();
        if (!examSnapshot && constraints is { IsEmpty: false })
            SeatGridConfigIO.WriteConstraintSheet(workbook, constraints, students);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        workbook.SaveAs(path);
    }

    /// <summary>
    /// 写座位格的文字。日常座位表就是姓名一个值（与旧版逐格一致）；
    /// 考场表多一行小号灰字座号——文档带座号才走富文本，日常导出完全不碰这条分支。
    /// </summary>
    private static void WriteSeatText(IXLCell cell, DisplaySeat seat, SeatChartSettings settings)
    {
        if (seat.SeatNumber is not { } seatNumber)
        {
            cell.Value = seat.Name;
            return;
        }

        var rich = cell.CreateRichText();
        if (seat.Name.Length > 0)
        {
            var nameRun = rich.AddText(seat.Name);
            nameRun.SetFontName("Microsoft YaHei");
            nameRun.SetFontSize(Math.Max(7, settings.NameFontSize * 0.75));
            nameRun.SetFontColor(XLColor.FromHtml(
                !seat.IsEmpty && settings.MarkFemales && seat.IsFemale
                    ? settings.FemaleMark
                    : settings.SeatText));
        }

        var numberRun = rich.AddText($"{(seat.Name.Length > 0 ? "\n" : string.Empty)}{seatNumber:00} 号");
        numberRun.SetFontName("Microsoft YaHei");
        numberRun.SetFontSize(8);
        numberRun.SetFontColor(XLColor.FromHtml(settings.RowLabel));
    }

    /// <summary>
    /// 某组某列之后要插入几个占位列。返回 0 表示不插——「不设过道」时整张表与旧版逐格一致。
    /// 一条过道最多插 2 列，避免把表撑得过宽。
    /// </summary>
    private static Func<int, int, int> BuildExtraColumns(SeatChartDocument document)
    {
        var surface = document.Layout.Surface;
        if (surface.ColumnCorridors.IsDefaultOrEmpty && surface.GroupCorridors.IsDefaultOrEmpty)
            return (_, _) => 0;

        return (group, column) =>
        {
            var cells = surface.ColumnCorridor(column);
            if (column == document.Layout.Columns - 1) cells += surface.GroupCorridor(group);
            return cells <= 0 ? 0 : Math.Min(2, Math.Max(1, (int)Math.Ceiling(cells)));
        };
    }

    /// <summary>某排之后要插入几个占位行；0 表示不插。</summary>
    private static Func<int, int> BuildExtraRows(SeatChartDocument document)
    {
        var surface = document.Layout.Surface;
        if (surface.RowCorridors.IsDefaultOrEmpty) return _ => 0;

        return row =>
        {
            var cells = surface.RowCorridor(row);
            return cells <= 0 ? 0 : Math.Min(2, Math.Max(1, (int)Math.Ceiling(cells)));
        };
    }

    /// <summary>组内前 column 列累积插入的占位列数。</summary>
    private static int OffsetWithinGroup(Func<int, int, int> extraAfter, int group, int column)
    {
        var sum = 0;
        for (var i = 0; i < column; i++) sum += extraAfter(group, i);
        return sum;
    }

    /// <summary>
    /// 门窗在 Excel 里只能落成文字（ClosedXML 没有形状 API）。这一处与 PNG / PDF 的
    /// 门洞线段形态不同，是刻意的降级，不要当成 bug 去「修」。
    /// </summary>
    private static void DrawDoorLabels(
        IXLWorksheet sheet, SeatChartDocument document, int row, int firstColumn, int lastColumn)
    {
        var settings = document.Settings;
        if (!settings.ShowDoors || document.Layout.Surface.Doors.IsDefaultOrEmpty) return;

        var front = document.Layout.Surface.Doors.Where(d => d.Edge == RoomEdge.Front).Select(d => d.Label);
        var back = document.Layout.Surface.Doors.Where(d => d.Edge == RoomEdge.Back).Select(d => d.Label);

        var left = string.Join("、", front);
        var right = string.Join("、", back);
        if (left.Length == 0 && right.Length == 0) return;

        sheet.Range(row, firstColumn, row, lastColumn).Merge();
        var cell = sheet.Cell(row, firstColumn);
        cell.Value = string.Join("　　", new[] { left, right }.Where(s => s.Length > 0));
        cell.Style.Font.FontName = "Microsoft YaHei";
        cell.Style.Font.FontSize = 9;
        cell.Style.Font.FontColor = XLColor.FromHtml("#85806F");
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Row(row).Height = 16;
    }

    private static void DrawPodium(IXLWorksheet sheet, int row, int firstColumn, int lastColumn, SeatChartSettings settings)
    {
        var range = sheet.Range(row, firstColumn, row, lastColumn);
        range.Merge();
        var cell = sheet.Cell(row, firstColumn);
        cell.Value = settings.PodiumLabel;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(settings.PodiumBackground);
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = XLColor.FromHtml(settings.SeatBorder);
        cell.Style.Font.FontName = "Microsoft YaHei";
        cell.Style.Font.FontSize = 10;
        cell.Style.Font.FontColor = XLColor.FromHtml(settings.PodiumText);
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(row).Height = 24;
    }
}
