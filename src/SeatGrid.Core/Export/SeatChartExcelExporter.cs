using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;

namespace SeatGrid.Core.Export;

/// <summary>把当前座位表导出成一份带颜色与边框的 Excel，并附一张完整名单。</summary>
public static class SeatChartExcelExporter
{
    public static void ExportSeatChart(
        SeatChartDocument document,
        IReadOnlyDictionary<SeatPosition, Student> assignments,
        IReadOnlyList<Student> students,
        string path)
    {
        var settings = document.Settings;
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SeatGridConfigIO.ChartSheetName);

        var hints = settings.ShowGroupRowHints;
        var firstSeatColumn = hints ? 2 : 1;
        var seatColumns = document.Layout.Groups * document.Layout.Columns + (document.Layout.Groups - 1);
        var lastColumn = firstSeatColumn + seatColumns - 1;

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

        for (var group = 0; group < document.Layout.Groups; group++)
        {
            var groupStart = firstSeatColumn + group * (document.Layout.Columns + 1);
            for (var column = 0; column < document.Layout.Columns; column++)
                sheet.Column(groupStart + column).Width = Math.Max(6, settings.CellSizePx / 7.0);

            if (group < document.Layout.Groups - 1)
                sheet.Column(groupStart + document.Layout.Columns).Width = 2;
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
                var start = firstSeatColumn + groupIndex * (document.Layout.Columns + 1);
                var end = start + document.Layout.Columns - 1;
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
                var groupStart = firstSeatColumn + groupIndex * (document.Layout.Columns + 1);

                for (var columnIndex = 0; columnIndex < row.Seats.Count; columnIndex++)
                {
                    var seat = row.Seats[columnIndex];
                    var cell = sheet.Cell(cursor, groupStart + columnIndex);
                    cell.Value = seat.Name;

                    cell.Style.Font.FontName = "Microsoft YaHei";
                    cell.Style.Font.FontSize = Math.Max(7, settings.NameFontSize * 0.75);
                    cell.Style.Font.Bold = settings.BoldNames;
                    cell.Style.Font.FontColor = XLColor.FromHtml(
                        !seat.IsEmpty && settings.MarkFemales && seat.IsFemale
                            ? settings.FemaleMark
                            : settings.SeatText);

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
                    cell.Style.Alignment.WrapText = false;
                }
            }

            sheet.Row(cursor).Height = Math.Max(22, settings.CellSizePx * 0.75);
            cursor++;
        }

        if (!document.PodiumOnTop)
        {
            DrawPodium(sheet, cursor, firstSeatColumn, lastColumn, settings);
            cursor++;
        }

        // 名单页与配置共用写法；再附一张隐藏的「座位设置」，导回时布局、配色与座位都能还原。
        // 隐藏表放在最后，打开文件时停在座位表上。
        SeatGridConfigIO.WriteRosterSheet(workbook, students, assignments, withIndex: true);
        SeatGridConfigIO.WriteSettingsSheet(workbook, settings).Hide();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        workbook.SaveAs(path);
    }

    private static void DrawPodium(IXLWorksheet sheet, int row, int firstColumn, int lastColumn, SeatChartSettings settings)
    {
        var range = sheet.Range(row, firstColumn, row, lastColumn);
        range.Merge();
        var cell = sheet.Cell(row, firstColumn);
        cell.Value = "讲　台";
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
