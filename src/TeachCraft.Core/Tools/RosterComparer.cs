using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace TeachCraft.Core.Tools;

public sealed record RosterNameCount(string Name, int Count);

public sealed record RosterComparison(
    int ExpectedCount,
    int SubmittedCount,
    IReadOnlyList<string> MissingNames,
    IReadOnlyList<RosterNameCount> DuplicateSubmittedNames,
    IReadOnlyList<string> UnexpectedNames,
    IReadOnlyList<RosterNameCount> DuplicateExpectedNames);

/// <summary>逐行规范化后精确核对名单；计数保留重复行，差异按首次出现顺序列出。</summary>
public static class RosterComparer
{
    public static RosterComparison Compare(IEnumerable<string> expectedLines, IEnumerable<string> submittedLines)
    {
        var expected = Normalize(expectedLines);
        var submitted = Normalize(submittedLines);
        var expectedCounts = expected.CountBy(name => name, StringComparer.Ordinal).ToArray();
        var submittedCounts = submitted.CountBy(name => name, StringComparer.Ordinal).ToArray();
        var expectedNames = expected.ToHashSet(StringComparer.Ordinal);
        var submittedNames = submitted.ToHashSet(StringComparer.Ordinal);

        return new RosterComparison(
            expected.Length,
            submitted.Length,
            expectedCounts.Where(pair => !submittedNames.Contains(pair.Key)).Select(pair => pair.Key).ToArray(),
            submittedCounts.Where(pair => pair.Value > 1)
                .Select(pair => new RosterNameCount(pair.Key, pair.Value)).ToArray(),
            submittedCounts.Where(pair => !expectedNames.Contains(pair.Key)).Select(pair => pair.Key).ToArray(),
            expectedCounts.Where(pair => pair.Value > 1)
                .Select(pair => new RosterNameCount(pair.Key, pair.Value)).ToArray());
    }

    public static string WriteReport(IReadOnlyList<string> inputs, string output)
    {
        if (inputs.Count != 2)
            throw new ToolboxException("名单核对需要恰好两份名单文件：第 1 份为应交名单，第 2 份为已交名单。");
        if (inputs.Any(path => !IsSupportedExtension(Path.GetExtension(path))))
            throw new ToolboxException("名单核对支持 .txt、.xlsx 和 .xls 文件，请检查所选名单格式。");

        var result = Compare(ReadNames(inputs[0]), ReadNames(inputs[1]));
        var report = new StringBuilder();
        report.AppendLine("名单核对报告");
        report.AppendLine($"应交人数：{result.ExpectedCount}");
        report.AppendLine($"已交人数：{result.SubmittedCount}");
        report.AppendLine("人数按非空行统计，包含重复姓名。");
        report.AppendLine();
        report.AppendLine($"应交名单重复姓名（错误）：{result.DuplicateExpectedNames.Count} 项");
        foreach (var duplicate in result.DuplicateExpectedNames)
            report.AppendLine($"{duplicate.Name} × {duplicate.Count}");
        if (result.DuplicateExpectedNames.Count == 0) report.AppendLine("无");
        report.AppendLine();
        report.AppendLine($"未交姓名：{result.MissingNames.Count} 项");
        foreach (var name in result.MissingNames) report.AppendLine(name);
        if (result.MissingNames.Count == 0) report.AppendLine("无");
        report.AppendLine();
        report.AppendLine($"已交名单重复姓名：{result.DuplicateSubmittedNames.Count} 项");
        foreach (var duplicate in result.DuplicateSubmittedNames)
            report.AppendLine($"{duplicate.Name} × {duplicate.Count}");
        if (result.DuplicateSubmittedNames.Count == 0) report.AppendLine("无");
        report.AppendLine();
        report.AppendLine($"不在应交名单中的姓名：{result.UnexpectedNames.Count} 项");
        foreach (var name in result.UnexpectedNames) report.AppendLine(name);
        if (result.UnexpectedNames.Count == 0) report.AppendLine("无");

        File.WriteAllText(output, report.ToString(), new UTF8Encoding(false));
        return $"已生成名单核对报告：{Path.GetFileName(output)}";
    }

    private static string[] Normalize(IEnumerable<string> lines) => lines
        .Select(line => Regex.Replace(line.Trim(), @"\s+", " "))
        .Where(line => line.Length > 0)
        .ToArray();

    private static bool IsSupportedExtension(string extension) =>
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadNames(string path)
    {
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return File.ReadLines(path);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = Encoding.GetEncoding(936),
            });
            var data = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false },
            });

            var names = ReadWorkbookNames(data.Tables.Cast<DataTable>());
            return names.Count > 0
                ? names
                : throw new ToolboxException($"工作簿「{Path.GetFileName(path)}」没有可读取的名单内容。");
        }
        catch (ToolboxException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ToolboxException($"无法读取名单文件「{Path.GetFileName(path)}」：{ex.Message}");
        }
    }

    private static IReadOnlyList<string> ReadWorkbookNames(IEnumerable<DataTable> tables)
    {
        var candidates = tables.Where(table => table.Rows.Count > 0).ToArray();
        foreach (var table in candidates)
        {
            var header = FindNameColumn(table);
            if (header is { } nameColumn)
            {
                return ReadColumn(table, nameColumn.Column, nameColumn.HeaderRow + 1);
            }
        }

        var populated = candidates
            .SelectMany(table => Enumerable.Range(0, table.Columns.Count)
                .Select(column => (Table: table, Column: column, Count: Enumerable.Range(0, table.Rows.Count)
                    .Count(row => CellText(table.Rows[row][column]).Trim().Length > 0))))
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        return populated.Count == 0 ? Array.Empty<string>() : ReadColumn(populated.Table, populated.Column, 0);
    }

    private static (int Column, int HeaderRow)? FindNameColumn(DataTable table)
    {
        var scanRows = Math.Min(table.Rows.Count, 20);
        for (var row = 0; row < scanRows; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                var header = Regex.Replace(CellText(table.Rows[row][column]).Trim(), @"[\s\u3000（）()【】\[\]：:·.\-_/\\]", "").ToLowerInvariant();
                if (header is "姓名" or "学生姓名" or "名字" or "学生" or "名称" or "学生名单" or "name")
                    return (column, row);
            }
        }

        return null;
    }

    private static string[] ReadColumn(DataTable table, int column, int startRow) =>
        Enumerable.Range(startRow, table.Rows.Count - startRow)
            .Select(row => CellText(table.Rows[row][column]))
            .ToArray();

    private static string CellText(object? value) =>
        value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
