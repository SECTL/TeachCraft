using ClosedXML.Excel;
using Homework.Core.Models;

namespace Homework.Core.Export;

/// <summary>
/// 作业索引工作簿：汇总 / 明细 / 未匹配 三张表。只读批次与计划，不改动任何导入来源。
/// </summary>
public static class HomeworkIndexWorkbook
{
    private static readonly string[] SummaryHeaders = ["班级", "学号", "姓名", "页数", "输出目录"];
    private static readonly string[] DetailHeaders =
        ["序号", "班级", "学号", "姓名", "页码", "原文件名", "原页码", "输出文件"];
    private static readonly string[] UnmatchedHeaders = ["序号", "未知标签", "原文件名", "原页码"];

    public static void Write(string path, HomeworkBatch batch, HomeworkExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(path))
            throw new HomeworkException("索引文件路径为空，无法写出索引工作簿。");

        var sources = batch.Pages.Select(p => p.SourcePath).ToList();
        if (HomeworkPathGuard.IsBlockedTarget(path, sources))
            throw new HomeworkException($"索引文件「{path}」是导入来源，禁止覆盖。");

        var classes = batch.Classes.ToDictionary(c => c.Id, c => c.Name);
        var students = batch.Students.ToDictionary(s => s.Id, s => s);
        var pagesById = batch.Pages.ToDictionary(p => p.Id, p => p);

        using var workbook = new XLWorkbook();

        // ---------------- 汇总：一名有页的学生一行 + 合计 ----------------
        var summary = CreateSheet(workbook, "汇总", SummaryHeaders, [16, 12, 14, 10, 46]);
        var studentIds = plan.Entries
            .Where(e => e.StudentId.HasValue)
            .Select(e => e.StudentId!.Value)
            .Distinct()
            .ToList();

        var row = 2;
        var totalPages = 0;
        foreach (var sid in studentIds)
        {
            if (!students.TryGetValue(sid, out var student)) continue;
            var pageList = batch.PagesOf(sid).OrderBy(p => p.Index).ToList();
            var firstEntry = plan.Entries.First(e => e.StudentId == sid);
            var dir = Path.GetDirectoryName(firstEntry.TargetPath) ?? string.Empty;

            summary.Cell(row, 1).Value = classes.GetValueOrDefault(student.ClassId, string.Empty);
            summary.Cell(row, 2).Value = student.Number;
            summary.Cell(row, 3).Value = student.Name;
            summary.Cell(row, 4).Value = pageList.Count;
            summary.Cell(row, 5).Value = dir;

            totalPages += pageList.Count;
            row++;
        }

        summary.Cell(row, 1).Value = "合计";
        summary.Cell(row, 4).Value = totalPages;

        // ---------------- 明细：一条单页条目一行 ----------------
        var detail = CreateSheet(workbook, "明细", DetailHeaders, [8, 16, 12, 14, 8, 26, 10, 48]);
        row = 2;
        var seq = 1;
        foreach (var entry in plan.Entries.Where(e => e.PageId.HasValue && e.StudentId.HasValue))
        {
            if (!pagesById.TryGetValue(entry.PageId!.Value, out var page)) continue;
            if (!students.TryGetValue(entry.StudentId!.Value, out var student)) continue;

            var ordered = batch.PagesOf(student.Id).OrderBy(p => p.Index).ToList();
            var pageNo = ordered.FindIndex(p => p.Id == page.Id) + 1;

            detail.Cell(row, 1).Value = seq++;
            detail.Cell(row, 2).Value = classes.GetValueOrDefault(student.ClassId, string.Empty);
            detail.Cell(row, 3).Value = student.Number;
            detail.Cell(row, 4).Value = student.Name;
            detail.Cell(row, 5).Value = pageNo;
            detail.Cell(row, 6).Value = Path.GetFileName(page.SourcePath);
            detail.Cell(row, 7).Value = page.SourcePageNumber;
            detail.Cell(row, 8).Value = entry.TargetPath;
            row++;
        }

        // ---------------- 未匹配：提交前应为空，仍如实列出 ----------------
        var unmatched = CreateSheet(workbook, "未匹配", UnmatchedHeaders, [8, 16, 26, 10]);
        row = 2;
        seq = 1;
        foreach (var page in batch.UnmatchedPages)
        {
            unmatched.Cell(row, 1).Value = seq++;
            unmatched.Cell(row, 2).Value = page.UnknownLabel;
            unmatched.Cell(row, 3).Value = Path.GetFileName(page.SourcePath);
            unmatched.Cell(row, 4).Value = page.SourcePageNumber;
            row++;
        }

        var dirPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dirPath)) Directory.CreateDirectory(dirPath);
        workbook.SaveAs(path);
    }

    private static IXLWorksheet CreateSheet(
        XLWorkbook workbook, string name, IReadOnlyList<string> headers, IReadOnlyList<double> widths)
    {
        var ws = workbook.Worksheets.Add(name);
        for (var i = 0; i < headers.Count; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(230, 230, 230);
            ws.Column(i + 1).Width = widths[i];
        }
        ws.SheetView.FreezeRows(1);
        return ws;
    }
}
