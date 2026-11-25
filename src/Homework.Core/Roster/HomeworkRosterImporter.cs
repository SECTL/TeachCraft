using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Homework.Core.Models;

namespace Homework.Core.Roster;

/// <summary>
/// 作业整理名单导入：xlsx / xls / csv 统一走 ExcelDataReader，
/// 先预览工作表与表头，再由用户明确指定列映射后读取。
/// </summary>
public static class HomeworkRosterImporter
{
    public static RosterWorkbookPreview LoadWorkbook(string path)
    {
        var data = ReadWorkbook(path);
        var sheets = data.Tables.Cast<DataTable>()
            .Select(table => new RosterWorksheetInfo(table.TableName, table.Rows.Count, table.Columns.Count))
            .ToArray();
        if (sheets.Length == 0 || sheets.All(sheet => sheet.RowCount == 0))
            throw new HomeworkException("名单文件里没有可读取的内容，请换一份名单。");
        return new RosterWorkbookPreview(Array.AsReadOnly(sheets));
    }

    public static RosterWorksheetPreview InspectWorksheet(string path, string worksheet, int headerRow)
    {
        var data = ReadWorkbook(path);
        var table = GetWorksheet(data, worksheet);
        if (headerRow < 1 || headerRow > table.Rows.Count)
            throw new HomeworkException("表头行号超出工作表范围，请重新选择。");

        var row = table.Rows[headerRow - 1];
        var columns = Enumerable.Range(0, table.Columns.Count)
            .Select(index => new RosterColumnChoice(index, RosterText.ColumnLetter(index), CellText(row[index])))
            .ToArray();
        return new RosterWorksheetPreview(worksheet, headerRow, table.Rows.Count - headerRow, columns);
    }

    public static RosterColumnMap GuessColumns(string path, string worksheet, int headerRow, bool hasClassColumn)
    {
        var data = ReadWorkbook(path);
        var table = GetWorksheet(data, worksheet);
        if (headerRow < 1 || headerRow > table.Rows.Count)
            throw new HomeworkException("表头行号超出工作表范围，请重新选择。");

        var row = table.Rows[headerRow - 1];
        var name = -1;
        var number = -1;
        var classColumn = -1;

        for (var index = 0; index < table.Columns.Count; index++)
        {
            var header = NormalizeHeader(CellText(row[index]));
            if (header.Length == 0) continue;

            // 学号优先于姓名：像「学生学号」这样的表头不能被「学生」误判成姓名列。
            if (number < 0 && IsNumberHeader(header)) { number = index; continue; }
            if (name < 0 && IsNameHeader(header)) { name = index; continue; }
            if (hasClassColumn && classColumn < 0 && IsClassHeader(header)) classColumn = index;
        }

        return new RosterColumnMap(headerRow, name, number, classColumn);
    }

    public static RosterImportResult Read(string path, string worksheet, RosterColumnMap map, string defaultClassName)
    {
        var data = ReadWorkbook(path);
        var table = GetWorksheet(data, worksheet);
        if (map.HeaderRow < -1 || map.HeaderRow > table.Rows.Count)
            throw new HomeworkException("表头行号超出工作表范围，请重新选择。");
        if (map.NameColumn < 0 || map.NameColumn >= table.Columns.Count)
            throw new HomeworkException("请选择有效的姓名列。");
        if (map.NumberColumn < -1 || map.NumberColumn >= table.Columns.Count)
            throw new HomeworkException("学号列设置无效，请重新选择。");
        if (map.ClassColumn < -1 || map.ClassColumn >= table.Columns.Count)
            throw new HomeworkException("班级列设置无效，请重新选择。");

        var fallbackClass = string.IsNullOrWhiteSpace(defaultClassName)
            ? "未分班"
            : RosterText.Clean(defaultClassName);

        var classes = new List<HomeworkClass>();
        var classByName = new Dictionary<string, HomeworkClass>(StringComparer.Ordinal);
        var students = new List<HomeworkStudent>();
        var warnings = new List<string>();
        var seenNames = new HashSet<(string Class, string Name)>();
        var warnedNames = new HashSet<(string Class, string Name)>();
        var seenNumbers = new HashSet<(string Class, string Number)>();
        var skipped = 0;

        var startRow = Math.Max(0, map.HeaderRow);
        for (var rowIndex = startRow; rowIndex < table.Rows.Count; rowIndex++)
        {
            var cells = table.Rows[rowIndex];
            var name = RosterText.Clean(CellText(cells[map.NameColumn]));
            if (name.Length == 0)
            {
                skipped++;
                continue;
            }

            var number = map.NumberColumn >= 0
                ? RosterText.Clean(CellText(cells[map.NumberColumn]))
                : string.Empty;
            var className = map.ClassColumn >= 0
                ? RosterText.Clean(CellText(cells[map.ClassColumn]))
                : string.Empty;
            if (className.Length == 0) className = fallbackClass;

            if (number.Length > 0 && !seenNumbers.Add((className, number)))
                throw new HomeworkException($"班级「{className}」内学号「{number}」重复，请检查名单。");

            if (!classByName.TryGetValue(className, out var homeworkClass))
            {
                homeworkClass = new HomeworkClass(Guid.NewGuid(), className);
                classByName.Add(className, homeworkClass);
                classes.Add(homeworkClass);
            }

            if (!seenNames.Add((className, name)) && warnedNames.Add((className, name)))
                warnings.Add($"班级「{className}」内存在同名学生「{name}」，请核对。");

            students.Add(new HomeworkStudent(Guid.NewGuid(), homeworkClass.Id, number, name));
        }

        return new RosterImportResult(classes, students, warnings, skipped);
    }

    private static DataSet ReadWorkbook(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new HomeworkException("找不到名单文件，请重新选择。");

        var extension = Path.GetExtension(path);
        var isCsv = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
        if (!isCsv
            && !extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
            throw new HomeworkException("名单文件只支持 .xlsx、.xls 和 .csv 格式。");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var stream = File.OpenRead(path);
            var configuration = new ExcelReaderConfiguration
            {
                FallbackEncoding = Encoding.GetEncoding(936),
            };
            using var reader = isCsv
                ? ExcelReaderFactory.CreateCsvReader(stream, configuration)
                : ExcelReaderFactory.CreateReader(stream, configuration);
            return reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false },
            });
        }
        catch (Exception ex)
        {
            throw new HomeworkException($"读取名单文件失败：{ex.Message}", ex);
        }
    }

    private static DataTable GetWorksheet(DataSet data, string worksheet)
    {
        var tables = data.Tables.Cast<DataTable>().ToList();
        return tables.FirstOrDefault(table => string.Equals(table.TableName, worksheet, StringComparison.Ordinal))
               ?? tables.FirstOrDefault(table => string.Equals(table.TableName, worksheet, StringComparison.OrdinalIgnoreCase))
               ?? throw new HomeworkException("所选工作表不存在，请重新选择。");
    }

    /// <summary>
    /// 单元格转文本。数值单元格 1 必须落成「1」而不是「1.0」——
    /// 用不变文化 + 整数格式，避免把学号变成带小数点的形式。
    /// </summary>
    private static string CellText(object? value)
    {
        if (value is null or DBNull) return string.Empty;
        if (value is double d && double.IsFinite(d) && d == Math.Floor(d))
            return d.ToString("0", CultureInfo.InvariantCulture);
        if (value is float f && float.IsFinite(f) && f == Math.Floor(f))
            return f.ToString("0", CultureInfo.InvariantCulture);
        if (value is decimal m && m == Math.Floor(m))
            return m.ToString("0", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = Regex.Replace(value.Trim(), @"[\s\u3000]+", string.Empty);
        return Regex.Replace(text, @"[（）()【】\[\]：:·.\-_/\\]", string.Empty).ToLowerInvariant();
    }

    private static bool IsNameHeader(string header) =>
        header.Contains("姓名") || header.Contains("名字") || header.Contains("学生") || header.Contains("name");

    private static bool IsNumberHeader(string header) =>
        header.Contains("学号") || header.Contains("编号") || header.Contains("考号")
        || header.Contains("座号") || header.Contains("序号");

    private static bool IsClassHeader(string header) =>
        header.Contains("班级") || header.Contains("班别") || header.Contains("行政班")
        || header.Contains("教学班") || header.Contains("class");
}
