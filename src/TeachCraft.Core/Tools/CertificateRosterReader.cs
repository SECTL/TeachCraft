using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace TeachCraft.Core.Tools;

public static class CertificateRosterReader
{
    public static CertificateWorkbookSnapshot LoadWorkbook(string path) => new(ReadWorkbook(path));

    public static CertificateWorkbookPreview InspectWorkbook(string path) => LoadWorkbook(path).InspectWorkbook();

    public static CertificateWorksheetPreview InspectWorksheet(string path, string worksheet, int headerRow) =>
        LoadWorkbook(path).InspectWorksheet(worksheet, headerRow);

    public static IReadOnlyList<CertificateRecord> Read(
        string path, string worksheet, int headerRow, int nameColumn, int awardColumn) =>
        LoadWorkbook(path).Read(worksheet, headerRow, nameColumn, awardColumn);

    public static IReadOnlyList<CertificateRecord> Read(CertificateBatchRequest request) =>
        Read(request.RosterPath, request.Worksheet, request.HeaderRow, request.NameColumn, request.AwardColumn);

    private static DataSet ReadWorkbook(string path)
    {
        if (!File.Exists(path)) throw new ToolboxException("找不到名单工作簿，请重新选择。");
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
            throw new ToolboxException("名单工作簿只支持 .xlsx 和 .xls 文件。");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = Encoding.GetEncoding(936),
            });
            return reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false },
            });
        }
        catch (Exception ex)
        {
            throw new ToolboxException($"读取名单工作簿失败：{ex.Message}");
        }
    }
}

public sealed class CertificateWorkbookSnapshot
{
    private readonly DataSet _data;
    private readonly CertificateWorkbookPreview _preview;

    internal CertificateWorkbookSnapshot(DataSet data)
    {
        _data = data;
        var sheets = data.Tables.Cast<DataTable>()
            .Select(table => new CertificateWorksheetInfo(table.TableName, table.Rows.Count, table.Columns.Count))
            .ToArray();
        if (sheets.Length == 0) throw new ToolboxException("工作簿中没有工作表。");
        _preview = new CertificateWorkbookPreview(Array.AsReadOnly(sheets));
    }

    public CertificateWorkbookPreview InspectWorkbook() => _preview;

    public CertificateWorksheetPreview InspectWorksheet(string worksheet, int headerRow)
    {
        var table = GetWorksheet(worksheet, headerRow);
        var row = headerRow - 1;
        var columns = Enumerable.Range(0, table.Columns.Count)
            .Select(index => new CertificateColumnChoice(index, ColumnLetter(index), Cell(table, row, index).Trim()))
            .ToArray();
        return new CertificateWorksheetPreview(worksheet, headerRow, table.Rows.Count - headerRow, columns);
    }

    public IReadOnlyList<CertificateRecord> Read(string worksheet, int headerRow, int nameColumn, int awardColumn)
    {
        var table = GetWorksheet(worksheet, headerRow);
        if (nameColumn < 0 || nameColumn >= table.Columns.Count)
            throw new ToolboxException("请选择有效的姓名列。");
        if (awardColumn < -1 || awardColumn >= table.Columns.Count)
            throw new ToolboxException("请选择有效的奖项列，或明确选择不打印奖项。");
        if (awardColumn >= 0 && awardColumn == nameColumn)
            throw new ToolboxException("姓名列与奖项列不能是同一列。");

        var records = Enumerable.Range(headerRow, table.Rows.Count - headerRow)
            .Select(row => new CertificateRecord(
                Normalize(Cell(table, row, nameColumn)),
                awardColumn < 0 ? string.Empty : Normalize(Cell(table, row, awardColumn))))
            .Where(record => record.Name.Length > 0)
            .ToArray();
        return records.Length > 0
            ? records
            : throw new ToolboxException("所选姓名列没有可生成的名单记录。");
    }

    private DataTable GetWorksheet(string worksheet, int headerRow)
    {
        var table = _data.Tables.Cast<DataTable>()
            .FirstOrDefault(table => string.Equals(table.TableName, worksheet, StringComparison.Ordinal))
            ?? throw new ToolboxException("所选工作表不存在，请重新选择。");
        if (headerRow < 1 || headerRow > table.Rows.Count)
            throw new ToolboxException("表头行号超出工作表范围，请重新选择。");
        return table;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    private static string Cell(DataTable table, int row, int column) =>
        table.Rows[row][column] is null or DBNull
            ? string.Empty
            : Convert.ToString(table.Rows[row][column], CultureInfo.InvariantCulture) ?? string.Empty;

    private static string ColumnLetter(int column)
    {
        var current = column + 1;
        var letters = string.Empty;
        while (current > 0)
        {
            current--;
            letters = (char)('A' + current % 26) + letters;
            current /= 26;
        }
        return letters;
    }
}
