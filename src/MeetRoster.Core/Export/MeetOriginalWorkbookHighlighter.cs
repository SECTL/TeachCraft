using ClosedXML.Excel;
using MeetRoster.Core.Models;

namespace MeetRoster.Core.Export;

/// <summary>
/// 「原表高亮」导出：把导入的原工作簿原样复制一份，只在
/// 「代表队名单」的该班块与「项目分组表」的该班条目上涂底色，
/// 其余单元格、列宽、合并、隐藏表、下拉校验一律不动。
///
/// 与 <see cref="MeetWorkbookExporter"/> 的区别：那份是软件重排的新工作簿，
/// 这份是老师手里那张表本身——他们要在熟悉的版面里一眼看到自己班。
/// </summary>
public static class MeetOriginalWorkbookHighlighter
{
    private static readonly XLColor Fill = XLColor.FromHtml("#FBE4D7");
    private static readonly XLColor Stroke = XLColor.FromHtml("#D9A58E");

    /// <summary>导出一份带高亮的副本；返回目标路径。</summary>
    public static string Export(MeetProject project, MeetExportOptions options, string path)
    {
        if (project.Classes.Count == 0)
            throw new InvalidOperationException("还没有导入任何班级，无法导出。");

        var source = project.SourcePath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new InvalidOperationException(
                "「原表高亮」需要导入时的原工作簿，但找不到它了（可能被移动或删除）。"
                + "请重新导入秩序册，或改用「软件整理」样式导出。");

        var target = Path.GetFullPath(path);
        if (string.Equals(target, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原表高亮导出不能覆盖导入的原文件，请换一个文件名。");

        // 工程里逐班报名表（如果有）同样不许被覆盖；秩序册工程这一列全空，守卫直接放过。
        MeetExportSourceGuard.EnsureNotOverwritingSources(
            target, project, "导入的原文件", "原始报名表");

        var className = options.IsAllClasses ? null : NormalizeClassName(options.ClassName);

        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);

        var painted = 0;
        var rosterSheet = FindSheet(workbook, "代表队名单");
        if (rosterSheet is not null)
            painted += HighlightRoster(rosterSheet, className);

        var groupingSheet = FindSheet(workbook, "项目分组表");
        if (groupingSheet is not null)
            painted += HighlightGrouping(groupingSheet, className);

        if (painted == 0 && className is not null)
            throw new InvalidOperationException(
                $"原表里没有找到「{options.ClassName}」的条目，无法高亮。请确认班级名与秩序册一致。");

        // 插一张「高亮说明」放在最前，让收到文件的人知道底色是什么意思。
        var note = workbook.Worksheets.Add("高亮说明", 1);
        note.Cell(1, 1).Value = string.IsNullOrEmpty(className)
            ? $"{project.Title} · 原表（未指定班级，未着色）"
            : $"{project.Title} · 已高亮 {className}";
        note.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(13);
        note.Cell(3, 1).Value = "底色 #FBE4D7 的单元格 = 该班条目。";
        note.Cell(4, 1).Value = "其余内容、列宽、合并单元格、下拉校验与原表一致，未做改动。";
        note.Cell(5, 1).Value = "本文件由「竞名」在本机生成，原文件未被修改。";
        note.Cell(5, 1).Style.Font.SetFontColor(XLColor.FromHtml("#6F6A5E"));
        note.Column(1).Width = 68;
        note.Column(1).Style.Alignment.SetWrapText(true);

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        workbook.SaveAs(target);
        return target;
    }

    // ----------------------------- 代表队名单 -----------------------------

    /// <summary>
    /// 名单按班分块：块头是「七年级 1班 (20)人」，下面依次是领队 / 教练 / 工作人员、
    /// 「男子」若干行、空行、「女子」若干行。只涂目标班这一块的整行，不改内容。
    /// </summary>
    private static int HighlightRoster(IXLWorksheet sheet, string? className)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var blocks = new List<(int Row, string Name)>();
        for (var row = 1; row <= lastRow; row++)
        {
            var text = Clean(sheet.Cell(row, 1).GetString());
            var match = System.Text.RegularExpressions.Regex.Match(
                text, @"^(?<grade>七|八|九|高一|高二|高三)(?:年级)?\s*(?<number>\d+)\s*班");
            if (!match.Success) continue;
            blocks.Add((row, $"{Grade(match.Groups["grade"].Value)}{match.Groups["number"].Value}班"));
        }

        var painted = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var (startRow, name) = blocks[i];
            if (className is not null && !string.Equals(name, className, StringComparison.Ordinal)) continue;

            var endRow = i + 1 < blocks.Count ? blocks[i + 1].Row - 1 : lastRow;
            // 块尾常跟着几行空行，别把空白也刷成色块。
            // 逐列查而不是用 sheet.Row(endRow).CellsUsed()：后者每退一行都要重建一次
            // 整行的已用单元格集合，块尾空行多时是平方级开销。
            while (endRow > startRow && IsRowBlank(sheet, endRow, PaintedColumns))
                endRow--;

            for (var row = startRow; row <= endRow; row++)
                PaintRow(sheet, row, 9);
            painted += endRow - startRow + 1;
        }

        return painted;
    }

    // ----------------------------- 项目分组表 -----------------------------

    /// <summary>
    /// 分组表里班级名在「班级」那一行（号码 / 姓名 / 班级三行一组，或团体项目的一行班级）。
    /// 只给命中该班的单元格涂色，不动整行——同一行的其他班不能被误标。
    ///
    /// 关键：分组表的班级列只写「6班」，必须用所在块头的组别（A 列「七年级」）拼成全名再比。
    /// 早先只做 `className.EndsWith("6班")`，会把**其他年级的 6 班**一并刷成色块。
    /// </summary>
    private static int HighlightGrouping(IXLWorksheet sheet, string? className)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var painted = 0;
        var currentGrade = string.Empty;

        for (var row = 1; row <= lastRow; row++)
        {
            if (TryReadBlockGrade(sheet, row, out var grade)) currentGrade = grade;

            for (var column = 2; column <= 9; column++)
            {
                var text = Clean(sheet.Cell(row, column).GetString());
                if (text.Length == 0 || !text.EndsWith("班", StringComparison.Ordinal)) continue;

                var full = FullClassName(text, currentGrade);
                if (className is not null && !string.Equals(full, className, StringComparison.Ordinal)) continue;

                var cell = sheet.Cell(row, column);
                cell.Style.Fill.SetBackgroundColor(Fill);
                cell.Style.Border.SetTopBorder(XLBorderStyleValues.Thin).Border.SetTopBorderColor(Stroke);
                cell.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(Stroke);
                painted++;
            }
        }

        return painted;
    }

    /// <summary>
    /// 块头行：A 列组别、C 列项目、F 列赛次同时非空。A 列给的是「七年级」这种组别，
    /// 分组表里所有「N班」都要靠它补全年级。
    /// </summary>
    private static bool TryReadBlockGrade(IXLWorksheet sheet, int row, out string grade)
    {
        grade = string.Empty;

        var a = Clean(sheet.Cell(row, 1).GetString());
        var c = Clean(sheet.Cell(row, 3).GetString());
        var f = Clean(sheet.Cell(row, 6).GetString());
        if (a.Length == 0 || c.Length == 0 || f.Length == 0) return false;

        grade = NormalizeGrade(a);
        return grade.Length > 0;
    }

    /// <summary>「6班」+「九年级」→「九年级6班」；「高一6班」归一化为「高一年级6班」。</summary>
    private static string FullClassName(string text, string grade)
    {
        var normalized = NormalizeClassName(text);
        if (normalized.Contains("年级", StringComparison.Ordinal)) return normalized;
        return grade.Length == 0 ? normalized : grade + normalized;
    }

    private static string NormalizeClassName(string? value)
    {
        var text = Clean(value ?? string.Empty);
        if (text.Length == 0) return string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"^(?<grade>七|八|九|高一|高二|高三)(?:年级)?\s*(?<number>\d+)\s*班$");
        return match.Success
            ? $"{Grade(match.Groups["grade"].Value)}{match.Groups["number"].Value}班"
            : text;
    }

    private static string NormalizeGrade(string value)
    {
        var text = Clean(value);
        if (text.StartsWith("七年级", StringComparison.Ordinal) || text == "七") return "七年级";
        if (text.StartsWith("八年级", StringComparison.Ordinal) || text == "八") return "八年级";
        if (text.StartsWith("九年级", StringComparison.Ordinal) || text == "九") return "九年级";
        if (text.StartsWith("高一", StringComparison.Ordinal)) return "高一年级";
        if (text.StartsWith("高二", StringComparison.Ordinal)) return "高二年级";
        if (text.StartsWith("高三", StringComparison.Ordinal)) return "高三年级";
        return string.Empty;
    }

    // ----------------------------- 工具 -----------------------------

    /// <summary>高亮时涂色的列数：与 <see cref="PaintRow"/> 保持一致。</summary>
    private const int PaintedColumns = 9;

    /// <summary>
    /// 该行在涂色列范围内是否全空。用逐格 GetString 而不是 `Row(...).CellsUsed()`：
    /// 后者要为整行建集合，而这里只需要看涂色会覆盖的那几列。
    /// </summary>
    private static bool IsRowBlank(IXLWorksheet sheet, int row, int columns)
    {
        for (var column = 1; column <= columns; column++)
        {
            if (!string.IsNullOrWhiteSpace(sheet.Cell(row, column).GetString())) return false;
        }

        return true;
    }

    private static void PaintRow(IXLWorksheet sheet, int row, int columns)
    {
        var range = sheet.Range(row, 1, row, columns);
        range.Style.Fill.SetBackgroundColor(Fill);
    }

    private static IXLWorksheet? FindSheet(XLWorkbook workbook, string keyword) =>
        workbook.Worksheets.FirstOrDefault(w =>
            w.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string Grade(string value) => value switch
    {
        "七" => "七年级",
        "八" => "八年级",
        "九" => "九年级",
        "高一" => "高一年级",
        "高二" => "高二年级",
        "高三" => "高三年级",
        _ => value,
    };

    private static string Clean(string value) =>
        (value ?? string.Empty).Replace("　", " ", StringComparison.Ordinal).Trim();
}
