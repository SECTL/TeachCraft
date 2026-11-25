using System.Text;
using ClosedXML.Excel;
using NPOI.HSSF.UserModel;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class RosterCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));

    public RosterCompareTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Compare_NormalizesWhitespaceAndIgnoresBlankLines()
    {
        var expected = new[] { "  合成甲\t 同学  ", "", "\u3000", "合成乙" };
        var submitted = new[] { "合成甲  \u3000同学", " 合成乙 ", "\t" };

        var result = RosterComparer.Compare(expected, submitted);

        Assert.Equal(2, result.ExpectedCount);
        Assert.Equal(2, result.SubmittedCount);
        Assert.Empty(result.MissingNames);
        Assert.Empty(result.UnexpectedNames);
    }

    [Fact]
    public void Compare_ReportsMissingUnexpectedAndDuplicateSubmissions()
    {
        var expected = new[] { "合成甲", "合成乙", "合成丙" };
        var submitted = new[] { "合成乙", " 合成乙 ", "合成丁", "合成丁", "合成丁" };

        var result = RosterComparer.Compare(expected, submitted);

        Assert.Equal(3, result.ExpectedCount);
        Assert.Equal(5, result.SubmittedCount);
        Assert.Equal(new[] { "合成甲", "合成丙" }, result.MissingNames);
        Assert.Equal(new[] { "合成丁" }, result.UnexpectedNames);
        Assert.Equal(new[] { new RosterNameCount("合成乙", 2), new RosterNameCount("合成丁", 3) },
            result.DuplicateSubmittedNames);
    }

    [Fact]
    public void Compare_PreservesExpectedDuplicateErrorsAfterNormalization()
    {
        var expected = new[] { " 合成甲 ", "合成甲", "合成甲", "合成乙" };

        var result = RosterComparer.Compare(expected, new[] { "合成乙" });

        Assert.Equal(4, result.ExpectedCount);
        Assert.Equal(new[] { new RosterNameCount("合成甲", 3) }, result.DuplicateExpectedNames);
        Assert.Equal(new[] { "合成甲" }, result.MissingNames);
    }

    [Fact]
    public void Compare_UsesExactOrdinalNames()
    {
        var result = RosterComparer.Compare(new[] { "Sample A", "合成甲" }, new[] { "sample A", "合 成甲" });

        Assert.Equal(new[] { "Sample A", "合成甲" }, result.MissingNames);
        Assert.Equal(new[] { "sample A", "合 成甲" }, result.UnexpectedNames);
    }

    [Fact]
    public void Compare_EmptyRostersProduceEmptyResult()
    {
        var result = RosterComparer.Compare(new[] { "  " }, Array.Empty<string>());

        Assert.Equal(0, result.ExpectedCount);
        Assert.Equal(0, result.SubmittedCount);
        Assert.Empty(result.MissingNames);
        Assert.Empty(result.UnexpectedNames);
        Assert.Empty(result.DuplicateExpectedNames);
        Assert.Empty(result.DuplicateSubmittedNames);
    }

    [Fact]
    public void Dispatcher_WritesUtf8ReportWithAllSections()
    {
        var expected = Write("expected.txt", "合成甲\n合成甲\n合成乙\n");
        var submitted = Write("submitted.txt", "合成乙\n合成乙\n合成丙\n");
        var output = Path.Combine(_root, "report.txt");

        ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = new[] { expected, submitted }, Output = output,
        });

        var report = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(output));
        Assert.Contains("应交人数：3", report);
        Assert.Contains("已交人数：3", report);
        Assert.Contains("应交名单重复姓名（错误）：1 项" + Environment.NewLine + "合成甲 × 2", report);
        Assert.Contains("未交姓名：1 项" + Environment.NewLine + "合成甲", report);
        Assert.Contains("已交名单重复姓名：1 项" + Environment.NewLine + "合成乙 × 2", report);
        Assert.Contains("不在应交名单中的姓名：1 项" + Environment.NewLine + "合成丙", report);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Dispatcher_RejectsWrongInputCount(int count)
    {
        var inputs = Enumerable.Range(0, count).Select(i => Write($"{i}.txt", "合成甲")).ToArray();
        var request = new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = inputs, Output = Path.Combine(_root, "report.txt"),
        };

        Assert.Throws<ToolboxException>(() => ToolDispatcher.Run(request));
        Assert.NotNull(ToolParameterCheck.Validate(request.Kind, inputs, null));
    }

    [Fact]
    public void Dispatcher_RejectsUnsupportedInput()
    {
        var expected = Write("expected.csv", "合成甲");
        var submitted = Write("submitted.txt", "合成甲");

        Assert.Throws<ToolboxException>(() => ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = new[] { expected, submitted }, Output = Path.Combine(_root, "report.txt"),
        }));
    }

    [Fact]
    public void Dispatcher_ReadsXlsxNameColumnAndTxtTogether()
    {
        var expected = WriteWorkbook("expected.xlsx", "姓名", "合成甲", "合成乙");
        var submitted = Write("submitted.txt", "合成乙\n合成丙\n");
        var output = Path.Combine(_root, "report.txt");

        ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = new[] { expected, submitted }, Output = output,
        });

        var report = File.ReadAllText(output);
        Assert.Contains("应交人数：2", report);
        Assert.Contains("未交姓名：1 项", report);
        Assert.Contains("合成甲", report);
        Assert.Contains("不在应交名单中的姓名：1 项", report);
    }

    [Fact]
    public void Dispatcher_ReadsLegacyXlsNameColumn()
    {
        var expected = WriteLegacyWorkbook("expected.xls", "姓名", "合成甲", "合成乙");
        var submitted = Write("submitted.txt", "合成甲\n合成乙\n");
        var output = Path.Combine(_root, "report.txt");

        ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = new[] { expected, submitted }, Output = output,
        });

        Assert.Contains("应交人数：2", File.ReadAllText(output));
        Assert.Contains("未交姓名：0 项", File.ReadAllText(output));
    }

    [Fact]
    public void Dispatcher_PrefersNameHeaderAcrossWorkbookSheets()
    {
        var expected = Path.Combine(_root, "expected.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var cover = workbook.AddWorksheet("说明");
            cover.Cell(1, 1).Value = "名单说明";
            cover.Cell(2, 1).Value = "这不是姓名";
            var roster = workbook.AddWorksheet("名单");
            roster.Cell(1, 2).Value = "学生姓名";
            roster.Cell(2, 2).Value = "合成甲";
            roster.Cell(3, 2).Value = "合成乙";
            workbook.SaveAs(expected);
        }
        var submitted = Write("submitted.txt", "合成甲\n合成乙\n");
        var output = Path.Combine(_root, "report.txt");

        ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = new[] { expected, submitted }, Output = output,
        });

        Assert.Contains("应交人数：2", File.ReadAllText(output));
        Assert.Contains("未交姓名：0 项", File.ReadAllText(output));
    }

    [Fact]
    public void Dispatcher_UsesMostPopulatedColumnWhenWorkbookHasNoNameHeader()
    {
        var expected = Path.Combine(_root, "expected.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("名单");
            sheet.Cell(1, 1).Value = "合成甲";
            sheet.Cell(2, 1).Value = "合成乙";
            sheet.Cell(3, 1).Value = "合成丙";
            sheet.Cell(1, 2).Value = "说明";
            workbook.SaveAs(expected);
        }
        var submitted = Write("submitted.txt", "合成甲\n合成乙\n合成丙\n");
        var output = Path.Combine(_root, "report.txt");

        ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = new[] { expected, submitted }, Output = output,
        });

        Assert.Contains("应交人数：3", File.ReadAllText(output));
        Assert.Contains("未交姓名：0 项", File.ReadAllText(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Dispatcher_ProtectsBothInputFiles(int outputIndex)
    {
        var inputs = new[] { Write("expected.txt", "合成甲"), Write("submitted.txt", "合成乙") };

        Assert.Throws<ToolboxException>(() => ToolDispatcher.Run(new ToolRequest
        {
            Kind = ToolKind.RosterCompare, Inputs = inputs, Output = inputs[outputIndex],
        }));

        Assert.Equal("合成甲", File.ReadAllText(inputs[0]));
        Assert.Equal("合成乙", File.ReadAllText(inputs[1]));
    }

    [Fact]
    public void Toolbox_RegistersRosterCompareAsToolElevenAndSupportsOrderedTwoFileRequest()
    {
        var vm = new MainViewModel(showOobe: false);
        var tool = vm.FindTool("roster-compare")!;
        Assert.Equal("11", tool.No);
        Assert.True(tool.IsTwoFile);
        Assert.True(tool.IsMultiFile);
        Assert.True(tool.ShowPlainNote);
        tool.ReplaceFiles(new[] { "expected.txt" });
        Assert.False(tool.CanRun);

        tool.AddFiles(new[] { "submitted.txt" });
        tool.MoveFile(tool.FileEntries[1], -1);

        Assert.True(tool.CanRun);
        Assert.Equal(new[] { "submitted.txt", "expected.txt" }, tool.ToRequest("report.txt").Inputs);
        Assert.Equal("submitted-核对报告.txt", tool.SuggestedFileName());
        tool.AddFiles(new[] { "extra.txt" });
        Assert.False(tool.CanRun);
    }

    private string Write(string fileName, string text)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    private string WriteWorkbook(string fileName, string header, params string[] names)
    {
        var path = Path.Combine(_root, fileName);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("名单");
        sheet.Cell(1, 1).Value = header;
        for (var i = 0; i < names.Length; i++) sheet.Cell(i + 2, 1).Value = names[i];
        workbook.SaveAs(path);
        return path;
    }

    private string WriteLegacyWorkbook(string fileName, string header, params string[] names)
    {
        var path = Path.Combine(_root, fileName);
        var workbook = new HSSFWorkbook();
        var sheet = workbook.CreateSheet("名单");
        sheet.CreateRow(0).CreateCell(0).SetCellValue(header);
        for (var i = 0; i < names.Length; i++) sheet.CreateRow(i + 1).CreateCell(0).SetCellValue(names[i]);
        using var stream = File.Create(path);
        workbook.Write(stream);
        workbook.Close();
        return path;
    }

    [Fact]
    public void ExcelClassSplitter_InspectsAndWritesOneWorkbookPerClass()
    {
        var input = WriteClassWorkbook("classes.xlsx");
        var output = Path.Combine(_root, "split");

        var preview = ExcelClassSplitter.Inspect(input);
        var summary = ExcelClassSplitter.Split(input, output);

        Assert.Equal("班级", preview.ClassColumnName);
        Assert.Equal(new[] { "七年级1班", "七年级2班" }, preview.ClassNames);
        Assert.Contains("2 份", summary, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "七年级1班.xlsx")));
        Assert.True(File.Exists(Path.Combine(output, "七年级2班.xlsx")));
        using var workbook = new XLWorkbook(Path.Combine(output, "七年级1班.xlsx"));
        Assert.Equal("七年级1班", workbook.Worksheet(1).Cell(2, 2).GetString());
        Assert.Equal(2, workbook.Worksheet(1).LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void ExcelClassSplitter_RejectsAmbiguousClassColumns()
    {
        var input = Path.Combine(_root, "ambiguous.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("总表");
            sheet.Cell(1, 1).Value = "班级";
            sheet.Cell(1, 2).Value = "行政班";
            sheet.Cell(2, 1).Value = "七年级1班";
            sheet.Cell(2, 2).Value = "七年级1班";
            workbook.SaveAs(input);
        }

        var error = Assert.Throws<ToolboxException>(() => ExcelClassSplitter.Inspect(input));
        Assert.Contains("多个", error.Message, StringComparison.Ordinal);
    }

    private string WriteClassWorkbook(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("总表");
        sheet.Cell(1, 1).Value = "姓名";
        sheet.Cell(1, 2).Value = "班级";
        sheet.Cell(2, 1).Value = "合成甲";
        sheet.Cell(2, 2).Value = "七年级1班";
        sheet.Cell(3, 1).Value = "合成乙";
        sheet.Cell(3, 2).Value = "七年级2班";
        workbook.SaveAs(path);
        return path;
    }
}
