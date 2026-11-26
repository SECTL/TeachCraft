using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ExcelDataReader;
using TeachCraft.Shared;

namespace TeachCraft.Core.Tools;

public sealed record ExcelClassSplitPreview(
    string SheetName,
    int HeaderRow,
    int ClassColumn,
    string ClassColumnName,
    IReadOnlyList<string> ClassNames,
    int DataRowCount);

public sealed record ExcelClassSplitCandidate(string SheetName, int HeaderRow, int ClassColumn, string Header,
    IReadOnlyList<string> ClassNames, int NonEmptyCount, int ClassLikeCount, DataTable Table);

public static class ExcelClassSplitter
{
    private static readonly string[] ClassHeaders = { "班级", "班别", "行政班", "class" };

    public static string Split(string input, string outputDirectory)
    {
        var preview = Inspect(input);
        return SplitSelected(input, outputDirectory, preview);
    }

    public static ExcelClassSplitPreview Inspect(string input)
    {
        if (!File.Exists(input)) throw new ToolboxException("找不到 Excel 文件，请重新选择。");
        var tables = ReadTables(input);
        var candidates = FindCandidates(tables);
        if (candidates.Count == 0)
            throw new ToolboxException("没有找到可用的班级列。请确认表头包含「班级」「班别」「行政班」或 Class。");
        var bestScore = candidates.Max(x => x.Score);
        var best = candidates.Where(x => x.Score == bestScore).ToArray();
        if (best.Length != 1)
            throw new ToolboxException("检测到多个同样可信的班级列，请先整理工作簿后再拆分，避免分错班。");

        return ToPreview(best[0].Table, best[0].HeaderRow, best[0].Column);
    }

    public static IReadOnlyList<ExcelClassSplitPreview> InspectCandidates(string input)
    {
        return GetCandidates(input).Select(candidate => ToPreview(candidate.Table, candidate.HeaderRow, candidate.ClassColumn)).ToArray();
    }

    public static IReadOnlyList<ExcelClassSplitCandidate> GetCandidates(string input)
    {
        if (!File.Exists(input)) throw new ToolboxException("找不到 Excel 文件，请重新选择。");
        return FindCandidates(ReadTables(input)).Select(candidate => ToCandidate(candidate)).ToArray();
    }

    public static string Split(string input, string outputDirectory, ExcelClassSplitPreview preview)
    {
        var selected = GetCandidates(input).FirstOrDefault(candidate =>
            candidate.SheetName == preview.SheetName && candidate.HeaderRow == preview.HeaderRow
            && candidate.ClassColumn == preview.ClassColumn);
        if (selected is null) throw new ToolboxException("所选班级列已失效，请重新选择工作簿。");
        return SplitSelected(input, outputDirectory, ToPreview(selected.Table, selected.HeaderRow, selected.ClassColumn));
    }

    private static string SplitSelected(string input, string outputDirectory, ExcelClassSplitPreview preview)
    {
        var table = ReadTable(input, preview.SheetName);
        Directory.CreateDirectory(outputDirectory);
        var targets = preview.ClassNames
            .Select(className => Path.Combine(outputDirectory, SanitizeFileName(className) + ".xlsx"))
            .ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new ToolboxException("不同班级会生成相同的文件名，请先整理班级名称后再拆分。");
        var existing = targets.FirstOrDefault(File.Exists);
        if (existing is not null)
            throw new ToolboxException($"输出文件夹中已有「{Path.GetFileName(existing)}」，为避免覆盖请换一个文件夹。");

        var written = 0;
        for (var index = 0; index < preview.ClassNames.Count; index++)
        {
            var className = preview.ClassNames[index];
            using var classBook = new XLWorkbook();
            var sheet = classBook.AddWorksheet(preview.SheetName);
            CopyRows(table, sheet, preview, className);
            var target = targets[index];
            var temp = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.tmp.xlsx");
            classBook.SaveAs(temp);
            File.Move(temp, target);
            written++;
        }

        return $"已按班级拆出 {written} 份 Excel 文件。";
    }

    private static List<(DataTable Table, int HeaderRow, int Column, int Score)> FindCandidates(IReadOnlyList<DataTable> tables)
    {
        var candidates = new List<(DataTable Table, int HeaderRow, int Column, int Score)>();
        foreach (var table in tables)
        {
            for (var row = 0; row < Math.Min(table.Rows.Count, 20); row++)
            for (var column = 0; column < table.Columns.Count; column++)
            {
                var header = NormalizeHeader(Cell(table, row, column));
                if (!ClassHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)) continue;
                var values = Values(table, column, row + 1);
                var classLike = values.Count(value => NormalizeClassName(value) is not null);
                if (classLike == 0) continue;
                candidates.Add((table, row, column, classLike));
            }
        }

        return candidates;
    }

    private static ExcelClassSplitPreview ToPreview(DataTable table, int headerRow, int column)
    {
        var names = Values(table, column, headerRow + 1)
            .Select(NormalizeClassName)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        return new ExcelClassSplitPreview(table.TableName, headerRow, column,
            Cell(table, headerRow, column), names, names.Length);
    }

    private static ExcelClassSplitCandidate ToCandidate((DataTable Table, int HeaderRow, int Column, int Score) candidate)
    {
        var preview = ToPreview(candidate.Table, candidate.HeaderRow, candidate.Column);
        var values = Values(candidate.Table, candidate.Column, candidate.HeaderRow + 1)
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return new ExcelClassSplitCandidate(candidate.Table.TableName, candidate.HeaderRow, candidate.Column,
            preview.ClassColumnName, preview.ClassNames, values.Length, candidate.Score, candidate.Table);
    }

    private static void CopyRows(DataTable table, IXLWorksheet target, ExcelClassSplitPreview preview, string? className)
    {
        var outputRow = 1;
        for (var row = 0; row < table.Rows.Count; row++)
        {
            var value = NormalizeClassName(Cell(table, row, preview.ClassColumn));
            if (row > preview.HeaderRow && className is not null && !string.Equals(value, className, StringComparison.Ordinal)) continue;
            for (var column = 0; column < table.Columns.Count; column++)
                target.Cell(outputRow, column + 1).Value = Cell(table, row, column);
            outputRow++;
        }
        target.Columns().AdjustToContents();
    }

    private static IReadOnlyList<DataTable> ReadTables(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { FallbackEncoding = Encoding.GetEncoding(936) });
            return reader.AsDataSet(new ExcelDataSetConfiguration { ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false } }).Tables.Cast<DataTable>().ToArray();
        }
        catch (Exception ex)
        {
            throw new ToolboxException(ErrorText.Describe(ex, "读取这份 Excel"));
        }
    }

    private static DataTable ReadTable(string path, string sheetName) =>
        ReadTables(path).First(t => t.TableName == sheetName);

    private static IEnumerable<string> Values(DataTable table, int column, int start) =>
        Enumerable.Range(start, Math.Max(0, table.Rows.Count - start)).Select(row => Cell(table, row, column));

    private static string Cell(DataTable table, int row, int column) =>
        table.Rows[row][column] is DBNull ? string.Empty : Convert.ToString(table.Rows[row][column], CultureInfo.InvariantCulture) ?? string.Empty;

    private static string NormalizeHeader(string value) => Regex.Replace(value.Trim(), @"[\s\u3000（）()【】\[\]：:·._\-/\\]", "").ToLowerInvariant();

    private static string? NormalizeClassName(string value)
    {
        var text = Regex.Replace(value.Replace('（', '(').Replace('）', ')').Trim(), @"\s+", "");
        if (text.Length == 0 || text.Contains("合计", StringComparison.Ordinal) || text.Contains("总计", StringComparison.Ordinal)) return null;
        return text;
    }

    private static string SanitizeFileName(string value)
    {
        var result = value;
        foreach (var ch in Path.GetInvalidFileNameChars()) result = result.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(result) ? "未命名班级" : result;
    }
}
