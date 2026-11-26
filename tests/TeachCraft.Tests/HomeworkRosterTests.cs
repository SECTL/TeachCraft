using System.Text;
using ClosedXML.Excel;
using Homework.Core;
using Homework.Core.Roster;
using NPOI.HSSF.UserModel;

namespace TeachCraft.Tests;

/// <summary>
/// 辑册名单导入。全部使用合成姓名（苏晚晴 / 陈屿 / 许砚）与合成班级
/// （八年级1班 / 九年级11班），临时文件在测试结束即删除。
/// </summary>
public sealed class HomeworkRosterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "TeachCraft.Tests", "HomeworkRoster", Guid.NewGuid().ToString("N"));

    public HomeworkRosterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string P(string name) => Path.Combine(_root, name);

    private string MakeXlsx(string fileName, string sheetName, int headerRow, string[] headers, object?[][] dataRows)
    {
        var path = P(fileName);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);
        for (var row = 1; row < headerRow; row++)
            sheet.Cell(row, 1).Value = $"说明{row}";
        for (var column = 0; column < headers.Length; column++)
            SetCell(sheet.Cell(headerRow, column + 1), headers[column]);
        for (var row = 0; row < dataRows.Length; row++)
            for (var column = 0; column < dataRows[row].Length; column++)
                if (dataRows[row][column] is { } value)
                    SetCell(sheet.Cell(headerRow + 1 + row, column + 1), value);
        workbook.SaveAs(path);
        return path;
    }

    private static void SetCell(IXLCell cell, object value)
    {
        switch (value)
        {
            case int i: cell.Value = i; break;
            case double d: cell.Value = d; break;
            case decimal m: cell.Value = m; break;
            case string s: cell.Value = s; break;
            default: cell.Value = value.ToString() ?? string.Empty; break;
        }
    }

    // ----------------------------- 预览 -----------------------------

    [Fact]
    public void Display_ShowsReadableTextInsteadOfRecordDump()
    {
        var sheet = new RosterWorksheetInfo("名单", 50, 3);
        Assert.Equal("名单（50 行）", sheet.ToString());

        var emptySheet = new RosterWorksheetInfo("说明", 0, 0);
        Assert.Equal("说明", emptySheet.ToString());

        var column = new RosterColumnChoice(2, "C", "姓名");
        Assert.Equal("C · 姓名", column.ToString());
        Assert.DoesNotContain("RosterColumnChoice", column.ToString());
    }

    [Fact]
    public void LoadWorkbook_Xlsx_ListsWorksheets()
    {
        var path = MakeXlsx("two.xlsx", "名单", 1, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["八年级1班", "02", "陈屿"],
        ]);
        using (var workbook = new XLWorkbook(path))
        {
            workbook.AddWorksheet("说明");
            workbook.Save();
        }

        var preview = HomeworkRosterImporter.LoadWorkbook(path);

        Assert.Equal(2, preview.Worksheets.Count);
        Assert.Contains(preview.Worksheets, sheet =>
            sheet.Name == "名单" && sheet.RowCount == 3 && sheet.ColumnCount == 3);
        Assert.Contains(preview.Worksheets, sheet => sheet.Name == "说明");
    }

    [Fact]
    public void InspectWorksheet_HeaderRow3_ReturnsLettersAndHeaders()
    {
        var path = MakeXlsx("header3.xlsx", "名单", 3, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["八年级1班", "02", "陈屿"],
        ]);

        var preview = HomeworkRosterImporter.InspectWorksheet(path, "名单", 3);

        Assert.Equal(3, preview.HeaderRow);
        Assert.Equal(2, preview.RowCount);
        Assert.Equal(["A", "B", "C"], preview.Columns.Select(column => column.Letter).ToArray());
        Assert.Equal(["班级", "学号", "姓名"], preview.Columns.Select(column => column.Header).ToArray());
        Assert.Equal("A · 班级", preview.Columns[0].DisplayName);
    }

    [Fact]
    public void GuessColumns_MatchesSynonyms_AndRespectsHasClassColumn()
    {
        var path = MakeXlsx("guess.xlsx", "名单", 1, ["班级", "学号", "姓名"], []);

        Assert.Equal(new RosterColumnMap(1, 2, 1, 0),
            HomeworkRosterImporter.GuessColumns(path, "名单", 1, true));
        Assert.Equal(new RosterColumnMap(1, 2, 1, -1),
            HomeworkRosterImporter.GuessColumns(path, "名单", 1, false));
    }

    [Fact]
    public void GuessColumns_EnglishAndAlternateSynonyms()
    {
        var path = MakeXlsx("guess-en.xlsx", "Sheet1", 1, ["class", "name", "考号"], []);

        var map = HomeworkRosterImporter.GuessColumns(path, "Sheet1", 1, true);

        Assert.Equal(new RosterColumnMap(1, 1, 2, 0), map);
    }

    [Fact]
    public void GuessColumns_MissingName_ReturnsMinusOne()
    {
        var path = MakeXlsx("guess-none.xlsx", "名单", 1, ["备注", "序号"], []);

        var map = HomeworkRosterImporter.GuessColumns(path, "名单", 1, true);

        Assert.Equal(-1, map.NameColumn);
        Assert.Equal(1, map.NumberColumn);
    }

    // ----------------------------- 读取 -----------------------------

    [Fact]
    public void Read_XlsxWithClassColumn_GroupsByFirstSeenClass()
    {
        var path = MakeXlsx("read.xlsx", "名单", 1, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["九年级11班", "02", "陈屿"],
            ["八年级1班", "03", "许砚"],
        ]);
        var map = HomeworkRosterImporter.GuessColumns(path, "名单", 1, true);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "默认班");

        Assert.Equal(["八年级1班", "九年级11班"], result.Classes.Select(item => item.Name).ToArray());
        Assert.Equal(3, result.Students.Count);
        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal(result.Classes[0].Id, result.Students[0].ClassId);
        Assert.Equal(result.Classes[1].Id, result.Students[1].ClassId);
        Assert.Equal(result.Classes[0].Id, result.Students[2].ClassId);
        Assert.All(result.Students, student => Assert.NotEqual(Guid.Empty, student.Id));
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.SkippedRows);
    }

    [Fact]
    public void Read_HeaderRow3_StartsAfterHeader()
    {
        var path = MakeXlsx("read-h3.xlsx", "名单", 3, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["八年级1班", "02", "陈屿"],
        ]);
        var map = HomeworkRosterImporter.GuessColumns(path, "名单", 3, true);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal("陈屿", result.Students[1].Name);
    }

    [Fact]
    public void Read_NoHeaderExplicitMap_StartsAtRowZero()
    {
        var path = MakeXlsx("no-header.xlsx", "名单", 0, [], [
            ["八年级1班", "01", "苏晚晴"],
            ["八年级1班", "02", "陈屿"],
        ]);
        var map = new RosterColumnMap(0, 2, 1, 0);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal("陈屿", result.Students[1].Name);
    }

    [Fact]
    public void Read_ClassColumnAbsent_UsesDefaultClass()
    {
        var path = MakeXlsx("no-class.xlsx", "名单", 1, ["学号", "姓名"], [
            ["01", "苏晚晴"],
            ["02", "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 1, 0, -1);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "八年级1班");

        Assert.Single(result.Classes);
        Assert.Equal("八年级1班", result.Classes[0].Name);
        Assert.All(result.Students, student => Assert.Equal(result.Classes[0].Id, student.ClassId));
    }

    [Fact]
    public void Read_BlankClass_FallsBackToDefaultClass()
    {
        var path = MakeXlsx("blank-class.xlsx", "名单", 1, ["班级", "学号", "姓名"], [
            ["", "01", "苏晚晴"],
            ["九年级11班", "02", "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 2, 1, 0);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "八年级1班");

        Assert.Equal(["八年级1班", "九年级11班"], result.Classes.Select(item => item.Name).ToArray());
    }

    [Fact]
    public void Read_BlankNumber_Allowed()
    {
        var path = MakeXlsx("blank-number.xlsx", "名单", 1, ["学号", "姓名"], [
            ["", "苏晚晴"],
            ["02", "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 1, 0, -1);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "八年级1班");

        Assert.Equal(2, result.Students.Count);
        Assert.Equal(string.Empty, result.Students[0].Number);
        Assert.Equal("02", result.Students[1].Number);
    }

    [Fact]
    public void Read_DuplicateNumber_ThrowsWithNumber()
    {
        var path = MakeXlsx("dup-number.xlsx", "名单", 1, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["八年级1班", "01", "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 2, 1, 0);

        var error = Assert.Throws<HomeworkException>(
            () => HomeworkRosterImporter.Read(path, "名单", map, "默认班"));

        Assert.Contains("01", error.Message);
    }

    [Fact]
    public void Read_SameNumberInDifferentClasses_IsAllowed()
    {
        var path = MakeXlsx("cross-number.xlsx", "名单", 1, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["九年级11班", "01", "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 2, 1, 0);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Read_DuplicateName_Warns()
    {
        var path = MakeXlsx("dup-name.xlsx", "名单", 1, ["班级", "学号", "姓名"], [
            ["八年级1班", "01", "苏晚晴"],
            ["八年级1班", "02", "苏晚晴"],
        ]);
        var map = new RosterColumnMap(1, 2, 1, 0);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Single(result.Warnings);
        Assert.Contains("苏晚晴", result.Warnings[0]);
    }

    [Fact]
    public void Read_BlankNames_SkippedAndCounted()
    {
        var path = MakeXlsx("blank-name.xlsx", "名单", 1, ["学号", "姓名"], [
            ["01", "苏晚晴"],
            ["02", ""],
            ["03", "   "],
            ["04", "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 1, 0, -1);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "八年级1班");

        Assert.Equal(2, result.Students.Count);
        Assert.Equal(2, result.SkippedRows);
    }

    [Fact]
    public void Read_NumericNumber_StaysText()
    {
        var path = MakeXlsx("numeric.xlsx", "名单", 1, ["学号", "姓名"], [
            [1, "苏晚晴"],
            [2, "陈屿"],
        ]);
        var map = new RosterColumnMap(1, 1, 0, -1);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "八年级1班");

        Assert.Equal("1", result.Students[0].Number);
        Assert.Equal("2", result.Students[1].Number);
        Assert.DoesNotContain(".", result.Students[0].Number);
    }

    [Fact]
    public void Read_TrimsAndCollapsesWhitespace()
    {
        var path = MakeXlsx("space.xlsx", "名单", 1, ["学号", "姓名"], [
            [" 01 ", " 苏  晚晴 "],
        ]);
        var map = new RosterColumnMap(1, 1, 0, -1);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "八年级1班");

        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal("苏 晚晴", result.Students[0].Name);
    }

    // ----------------------------- 真实 xls / GBK csv -----------------------------

    [Fact]
    public void Read_RealXls_Npoi()
    {
        var path = P("legacy.xls");
        using (var workbook = new HSSFWorkbook())
        {
            var sheet = workbook.CreateSheet("名单");
            var header = sheet.CreateRow(0);
            header.CreateCell(0).SetCellValue("班级");
            header.CreateCell(1).SetCellValue("学号");
            header.CreateCell(2).SetCellValue("姓名");
            var first = sheet.CreateRow(1);
            first.CreateCell(0).SetCellValue("八年级1班");
            first.CreateCell(1).SetCellValue("01");
            first.CreateCell(2).SetCellValue("苏晚晴");
            var second = sheet.CreateRow(2);
            second.CreateCell(0).SetCellValue("九年级11班");
            second.CreateCell(1).SetCellValue("02");
            second.CreateCell(2).SetCellValue("陈屿");
            using var stream = File.Create(path);
            workbook.Write(stream);
        }

        var preview = HomeworkRosterImporter.LoadWorkbook(path);
        Assert.Contains(preview.Worksheets, sheet => sheet.Name == "名单");
        var map = HomeworkRosterImporter.GuessColumns(path, "名单", 1, true);
        Assert.Equal(new RosterColumnMap(1, 2, 1, 0), map);

        var result = HomeworkRosterImporter.Read(path, "名单", map, "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal("陈屿", result.Students[1].Name);
    }

    [Fact]
    public void Read_GbkCsv()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = P("gbk.csv");
        File.WriteAllText(path,
            "班级,学号,姓名\n八年级1班,01,苏晚晴\n八年级1班,02,陈屿\n",
            Encoding.GetEncoding(936));

        var preview = HomeworkRosterImporter.LoadWorkbook(path);
        var sheet = preview.Worksheets[0].Name;
        var map = HomeworkRosterImporter.GuessColumns(path, sheet, 1, true);
        Assert.Equal(new RosterColumnMap(1, 2, 1, 0), map);

        var result = HomeworkRosterImporter.Read(path, sheet, map, "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal("陈屿", result.Students[1].Name);
    }

    // ----------------------------- 失败路径 -----------------------------

    [Fact]
    public void LoadWorkbook_Missing_Throws()
    {
        var error = Assert.Throws<HomeworkException>(
            () => HomeworkRosterImporter.LoadWorkbook(P("missing.xlsx")));
        Assert.Contains("找不到", error.Message);
    }

    [Fact]
    public void LoadWorkbook_Unsupported_Throws()
    {
        var path = P("bad.txt");
        File.WriteAllText(path, "不是名单");
        var error = Assert.Throws<HomeworkException>(() => HomeworkRosterImporter.LoadWorkbook(path));
        Assert.Contains("只支持", error.Message);
    }

    [Fact]
    public void LoadWorkbook_Empty_Throws()
    {
        var path = P("empty.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("空表");
            workbook.SaveAs(path);
        }
        var error = Assert.Throws<HomeworkException>(() => HomeworkRosterImporter.LoadWorkbook(path));
        Assert.Contains("没有可读取", error.Message);
    }

    [Fact]
    public void Read_InvalidNameColumn_Throws()
    {
        var path = MakeXlsx("invalid.xlsx", "名单", 1, ["学号", "姓名"], [["01", "苏晚晴"]]);
        var map = new RosterColumnMap(1, -1, 0, -1);
        Assert.Throws<HomeworkException>(() => HomeworkRosterImporter.Read(path, "名单", map, "八年级1班"));
    }
}
