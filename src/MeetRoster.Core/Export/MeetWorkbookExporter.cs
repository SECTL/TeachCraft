using ClosedXML.Excel;
using MeetRoster.Core.Models;

namespace MeetRoster.Core.Export;

/// <summary>
/// 导出「高亮班级」的秩序册工作簿：分「班级报名 / 单项名单 / 赛程（只列该班）」三页，
/// 该班条目整行加底色。与按模板回写的报名表导出不同——那份是给 Excel 继续填的，
/// 这份是发给班主任看的参赛手册。
/// </summary>
public static class MeetWorkbookExporter
{
    private static readonly XLColor Ink = XLColor.FromHtml("#141413");
    private static readonly XLColor Muted = XLColor.FromHtml("#6F6A5E");
    private static readonly XLColor Line = XLColor.FromHtml("#D9D3C4");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#F4F2EC");
    private static readonly XLColor HighlightFill = XLColor.FromHtml("#FBE4D7");
    private static readonly XLColor Wash = XLColor.FromHtml("#FCFAF4");

    public static string Export(MeetProject project, MeetExportOptions options, string path)
    {
        if (project.Classes.Count == 0)
            throw new InvalidOperationException("还没有导入任何班级，无法导出。");

        var target = Path.GetFullPath(path);
        foreach (var source in project.Classes
                     .Select(c => c.SourcePath)
                     .Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (string.Equals(target, Path.GetFullPath(source!), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导出文件不能覆盖原始报名表，请换一个文件名。");
        }

        var summaries = MeetExportBuilder.Build(project, options);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        if (options.IncludeRoster) WriteRosterSheet(workbook, project, summaries, options);
        if (options.IncludeEvents) WriteEventSheet(workbook, summaries, options);
        if (options.IncludeSchedule) WriteScheduleSheet(workbook, project, summaries, options);
        if (options.IncludeClassSummary) WriteSummarySheet(workbook, project, summaries, options);

        if (workbook.Worksheets.Count == 0)
            workbook.AddWorksheet("说明").Cell(1, 1).Value = "没有勾选任何分页，未生成内容。";

        workbook.SaveAs(target);
        return target;
    }

    // ----------------------------- 班级报名 -----------------------------

    private static void WriteRosterSheet(
        XLWorkbook workbook, MeetProject project,
        IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var sheet = workbook.AddWorksheet("班级报名");
        var row = 1;
        sheet.Cell(row, 1).Value = project.Title;
        sheet.Range(row, 1, row, 5).Merge();
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(Ink);
        row += 2;

        foreach (var summary in summaries)
        {
            row = WriteClassHeader(sheet, row, summary, options);

            var headers = new[] { "序号", "号码", "姓名", "性别", "比赛项目" };
            WriteHeaderRow(sheet, row++, headers);

            var index = 0;
            foreach (var athlete in summary.Athletes)
            {
                sheet.Cell(row, 1).Value = ++index;
                sheet.Cell(row, 2).Value = athlete.Bib;
                sheet.Cell(row, 3).Value = athlete.Name;
                sheet.Cell(row, 4).Value = athlete.Gender;
                sheet.Cell(row, 5).Value = string.Join("、", new[]
                {
                    athlete.IndividualEvent1, athlete.IndividualEvent2,
                }.Where(s => !string.IsNullOrWhiteSpace(s)));
                StyleRow(sheet, row, 5);
                row++;
            }

            if (summary.Athletes.Count == 0)
            {
                sheet.Cell(row, 1).Value = "（没有名单）";
                sheet.Range(row, 1, row, 5).Merge();
                sheet.Cell(row, 1).Style.Font.SetFontColor(Muted).Font.SetItalic();
                row++;
            }

            row++;
        }

        Finish(sheet, 5, row);
    }

    private static int WriteClassHeader(
        IXLWorksheet sheet, int row, ClassExportSummary summary, MeetExportOptions options)
    {
        sheet.Cell(row, 1).Value = summary.Class.Name;
        sheet.Range(row, 1, row, 3).Merge();
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(12).Font.SetFontColor(Ink);

        sheet.Cell(row, 4).Value =
            $"{summary.AthleteCount} 人 · 男 {summary.MaleCount} · 女 {summary.FemaleCount} · {summary.IndividualCount} 个单项";
        sheet.Range(row, 4, row, 5).Merge();
        sheet.Cell(row, 4).Style.Font.SetFontColor(Muted).Font.SetFontSize(10)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
        row++;

        var meta = new List<string>();
        if (summary.Class.Coach.Length > 0) meta.Add("任课体育老师：" + summary.Class.Coach);
        if (summary.Class.Staff.Length > 0) meta.Add("工作人员：" + summary.Class.Staff);
        if (summary.Class.LeaderContact.Length > 0) meta.Add("领队：" + summary.Class.LeaderContact);
        if (summary.Class.TeamEvents.Count > 0)
            meta.Add("团体项目：" + string.Join("、", summary.Class.TeamEvents));

        if (meta.Count > 0)
        {
            sheet.Cell(row, 1).Value = string.Join("　|　", meta);
            sheet.Range(row, 1, row, 5).Merge();
            sheet.Cell(row, 1).Style.Font.SetFontColor(Muted).Font.SetFontSize(9.5);
            row++;
        }

        return row;
    }

    // ----------------------------- 单项名单 -----------------------------

    private static void WriteEventSheet(
        XLWorkbook workbook, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var sheet = workbook.AddWorksheet("单项名单");
        var row = 1;
        sheet.Cell(row, 1).Value = options.IsAllClasses ? "单项报名汇总" : $"{summaries[0].Title} · 单项报名";
        sheet.Range(row, 1, row, 4).Merge();
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(Ink);
        row += 2;

        WriteHeaderRow(sheet, row++, ["项目", "组别", "参赛人", "所在班级"]);
        var any = false;
        foreach (var summary in summaries)
        {
            foreach (var (eventName, gender, names) in summary.EventRoster())
            {
                any = true;
                sheet.Cell(row, 1).Value = eventName;
                sheet.Cell(row, 2).Value = gender;
                sheet.Cell(row, 3).Value = string.Join("、", names);
                sheet.Cell(row, 4).Value = summary.Title;
                StyleRow(sheet, row, 4);
                row++;
            }
        }

        if (!any)
        {
            sheet.Cell(row, 1).Value = "（没有单项报名）";
            sheet.Range(row, 1, row, 4).Merge();
            sheet.Cell(row, 1).Style.Font.SetFontColor(Muted).Font.SetItalic();
            row++;
        }

        Finish(sheet, 4, row);
    }

    // ----------------------------- 赛程 -----------------------------

    private static void WriteScheduleSheet(
        XLWorkbook workbook, MeetProject project,
        IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var sheet = workbook.AddWorksheet("赛程");
        var row = 1;
        sheet.Cell(row, 1).Value = options.IsAllClasses
            ? $"{project.Title} · 赛程"
            : $"{summaries[0].Title} · 参赛赛程";
        sheet.Range(row, 1, row, 7).Merge();
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(Ink);
        row += 2;

        var lines = summaries
            .SelectMany(s => s.Entries)
            .OrderBy(l => DayOrder(project, l.DayName))
            .ThenBy(l => l.Period == "上午" ? 0 : 1)
            .ThenBy(l => l.Time, StringComparer.Ordinal)
            .ToList();

        string? lastDay = null;
        string? lastPeriod = null;
        var headerWritten = false;
        var headers = new[] { "时间", "组别", "项目", "赛次", "参赛人", "组次 / 道次", "组数" };
        foreach (var line in lines)
        {
            if (!string.Equals(lastDay, line.DayName + line.DateText, StringComparison.Ordinal))
            {
                if (lastDay is not null) row++;
                sheet.Cell(row, 1).Value = $"{line.DayName} · {line.DateText}";
                sheet.Range(row, 1, row, 7).Merge();
                sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(12)
                    .Font.SetFontColor(Ink)
                    .Fill.SetBackgroundColor(Wash);
                row++;
                lastDay = line.DayName + line.DateText;
                lastPeriod = null;
                headerWritten = false;
            }

            if (!string.Equals(lastPeriod, line.Period, StringComparison.Ordinal))
            {
                sheet.Cell(row, 1).Value = line.Period;
                sheet.Range(row, 1, row, 7).Merge();
                sheet.Cell(row, 1).Style.Font.SetFontColor(Muted).Font.SetFontSize(9.5);
                row++;
                lastPeriod = line.Period;
                headerWritten = false;
            }

            if (!headerWritten)
            {
                WriteHeaderRow(sheet, row++, headers);
                headerWritten = true;
            }

            sheet.Cell(row, 1).Value = line.Time;
            sheet.Cell(row, 2).Value = line.Grade;
            sheet.Cell(row, 3).Value = line.EventName;
            sheet.Cell(row, 4).Value = line.Stage;
            sheet.Cell(row, 5).Value = line.AthleteName;
            sheet.Cell(row, 6).Value = line.Lane;
            sheet.Cell(row, 7).Value = line.GroupCount.Length > 0 ? $"{line.GroupCount} 组" : string.Empty;

            StyleRow(sheet, row, 7);
            if (options.HighlightClass)
                sheet.Range(row, 1, row, 7).Style.Fill.SetBackgroundColor(HighlightFill);
            row++;
        }

        if (lines.Count == 0)
        {
            sheet.Cell(row, 1).Value = options.IsAllClasses
                ? "（还没有赛程，请先导入「竞赛日程 + 项目分组表」）"
                : "（这个班在赛程里没有参赛记录）";
            sheet.Range(row, 1, row, 7).Merge();
            sheet.Cell(row, 1).Style.Font.SetFontColor(Muted).Font.SetItalic();
            row++;
        }

        Finish(sheet, 7, row);
    }

    private static int DayOrder(MeetProject project, string dayName)
    {
        for (var i = 0; i < project.Days.Count; i++)
        {
            if (string.Equals(project.Days[i].Name, dayName, StringComparison.Ordinal)) return i;
        }

        return int.MaxValue;
    }

    // ----------------------------- 班级汇总 -----------------------------

    private static void WriteSummarySheet(
        XLWorkbook workbook, MeetProject project,
        IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var sheet = workbook.AddWorksheet("班级汇总");
        var row = 1;
        sheet.Cell(row, 1).Value = $"{project.Title} · 参赛汇总";
        sheet.Range(row, 1, row, 6).Merge();
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(Ink);
        row += 2;

        WriteHeaderRow(sheet, row++, ["班级", "男子", "女子", "合计", "单项", "团体项目"]);

        foreach (var summary in summaries)
        {
            sheet.Cell(row, 1).Value = summary.Title;
            sheet.Cell(row, 2).Value = summary.MaleCount;
            sheet.Cell(row, 3).Value = summary.FemaleCount;
            sheet.Cell(row, 4).Value = summary.AthleteCount;
            sheet.Cell(row, 5).Value = summary.IndividualCount;
            sheet.Cell(row, 6).Value = summary.TeamCount;
            StyleRow(sheet, row, 6);
            if (options.HighlightClass && !options.IsAllClasses)
                sheet.Range(row, 1, row, 6).Style.Fill.SetBackgroundColor(HighlightFill);
            row++;
        }

        if (summaries.Count > 1)
        {
            sheet.Cell(row, 1).Value = "合计";
            sheet.Cell(row, 2).Value = summaries.Sum(s => s.MaleCount);
            sheet.Cell(row, 3).Value = summaries.Sum(s => s.FemaleCount);
            sheet.Cell(row, 4).Value = summaries.Sum(s => s.AthleteCount);
            sheet.Cell(row, 5).Value = summaries.Sum(s => s.IndividualCount);
            sheet.Cell(row, 6).Value = summaries.Sum(s => s.TeamCount);
            sheet.Range(row, 1, row, 6).Style.Font.SetBold();
            StyleRow(sheet, row, 6);
            row++;
        }

        Finish(sheet, 6, row);
    }

    // ----------------------------- 共用样式 -----------------------------

    private static void WriteHeaderRow(IXLWorksheet sheet, int row, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var cell = sheet.Cell(row, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.SetBold().Font.SetFontSize(10).Font.SetFontColor(Ink);
            cell.Style.Fill.SetBackgroundColor(HeaderFill);
            cell.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(Line);
            cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
        }
    }

    private static void StyleRow(IXLWorksheet sheet, int row, int columns)
    {
        for (var column = 1; column <= columns; column++)
        {
            var cell = sheet.Cell(row, column);
            cell.Style.Font.SetFontSize(10).Font.SetFontColor(Ink);
            cell.Style.Border.SetBottomBorder(XLBorderStyleValues.Hair).Border.SetBottomBorderColor(Line);
            cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
        }

        sheet.Row(row).Height = 19;
    }

    private static void Finish(IXLWorksheet sheet, int columns, int lastRow)
    {
        sheet.SheetView.FreezeRows(3);
        sheet.Column(1).Width = 16;
        for (var column = 2; column <= columns; column++)
            sheet.Column(column).Width = column == columns ? 20 : 14;
        sheet.Columns().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        var range = sheet.Range(1, 1, Math.Max(lastRow - 1, 1), columns);
        range.Style.Alignment.SetWrapText(true);
    }
}
