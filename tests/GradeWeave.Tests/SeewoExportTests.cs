using System.IO.Compression;
using System.Xml.Linq;
using GradeWeave.Core;
using GradeWeave.Core.Export;
using GradeWeave.Core.Models;
using GradeWeave.Core.Scheduling;
using GradeWeave.Core.Timetable;

namespace GradeWeave.Tests;

/// <summary>
/// 希沃全校课表预设回归：模板原样保留 + 数据填对 + 错误路径可读。
/// 只用 WeaveSamples 的合成数据，模板本身是希沃下发的空表。
/// </summary>
public sealed class SeewoExportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "GradeWeave.Tests", Guid.NewGuid().ToString("N"));

    public SeewoExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Export_KeepsEveryTemplatePartExceptSheetAndSharedStrings()
    {
        var path = ExportSample();
        var template = ExtractBundledTemplate();

        using var actual = ZipFile.OpenRead(path);
        using var expected = ZipFile.OpenRead(template);

        var actualNames = actual.Entries.Where(e => !e.FullName.EndsWith('/'))
            .Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expectedNames = expected.Entries.Where(e => !e.FullName.EndsWith('/'))
            .Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedNames, actualNames);

        // 只有工作表与共享字符串表允许变化；批注、VML、外部链接、样式表必须逐字节一致。
        foreach (var name in expectedNames)
        {
            if (name is "xl/worksheets/sheet1.xml" or "xl/sharedStrings.xml") continue;
            Assert.True(
                ReadBytes(expected, name).SequenceEqual(ReadBytes(actual, name)),
                $"{name} 被改动了；希沃预设只能填数据，不能重排模板。");
        }
    }

    [Fact]
    public void Export_PreservesHeaderMergesAndComments()
    {
        var path = ExportSample();

        using var archive = ZipFile.OpenRead(path);
        var sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");
        XNamespace ns = sheet.Root!.Name.Namespace;

        var merges = sheet.Descendants(ns + "mergeCell")
            .Select(m => m.Attribute("ref")!.Value)
            .ToList();
        Assert.Contains("A1:Q1", merges);
        Assert.Contains("A2:Q2", merges);
        Assert.Contains("C3:C4", merges);
        Assert.Equal(11, merges.Count);

        // 表头文字原样：第 3 行「班级 / 课节（必填）/ 星期一…」，第 4 行「年级（必填）/ 班级（必填）」。
        // 表头是富文本（一个 si 里多个 r），要按条目拼接后再比。
        var strings = LoadXml(archive, "xl/sharedStrings.xml");
        var texts = strings.Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
        Assert.Contains("课节（必填）", texts);
        Assert.Contains("年级（必填）", texts);
        Assert.Contains("班级（必填）", texts);
        Assert.Contains("老师姓名或手机号", texts);

        // 批注部件仍在，且 C3 / E4 的提示文字没被动过。
        var comments = LoadXml(archive, "xl/comments1.xml");
        var commentRefs = comments.Descendants(ns + "comment").Select(c => c.Attribute("ref")!.Value).ToList();
        Assert.Contains("C3", commentRefs);
        Assert.Contains("E4", commentRefs);
    }

    [Fact]
    public void Export_WritesOneRowPerClassAndPeriod()
    {
        var project = WeaveSamples.CreateDemo(3);
        var path = Path.Combine(_directory, "seewo.xlsx");
        WeaveSeewoExporter.Export(project, path);

        using var archive = ZipFile.OpenRead(path);
        var sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");
        XNamespace ns = sheet.Root!.Name.Namespace;

        var rows = sheet.Descendants(ns + "row").ToList();
        var periods = project.Periods.Count(p => !p.IsExtension);
        Assert.Equal(4 + project.Classes.Count * periods, rows.Count);
        Assert.Equal(5, (int)rows[4].Attribute("r")!);

        // 每行 A 列年级、B 列班级、C 列节次；数据行从第 5 行起连续。
        var dataRows = rows.Where(r => int.Parse(r.Attribute("r")!.Value) >= 5).ToList();
        Assert.All(dataRows, row =>
        {
            var cells = row.Elements(ns + "c").ToList();
            Assert.Equal($"A{row.Attribute("r")!.Value}", cells[0].Attribute("r")!.Value);
            Assert.Equal($"C{row.Attribute("r")!.Value}", cells[2].Attribute("r")!.Value);
        });

        // 示例行「高一8班」已被真实数据替换。
        var strings = LoadXml(archive, "xl/sharedStrings.xml");
        var texts = strings.Descendants(ns + "t").Select(t => t.Value).ToList();
        Assert.Contains("九年级", texts);
        Assert.Contains("九（1）班", texts);
        Assert.Contains("高一8班", texts); // 示例行字符串仍留在表里，但已不被任何单元格引用
    }

    [Fact]
    public void Export_CellValuesMatchProject()
    {
        var project = WeaveSamples.CreateDemo(2);
        TimetableService.Confirm(project, WeaveTimetables.FromPreset("lesson-only"));
        new WeaveScheduler().Generate(project);

        var path = Path.Combine(_directory, "seewo.xlsx");
        WeaveSeewoExporter.Export(project, path);

        using var archive = ZipFile.OpenRead(path);
        var sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");
        XNamespace ns = sheet.Root!.Name.Namespace;
        var strings = LoadXml(archive, "xl/sharedStrings.xml");
        var table = strings.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();

        string CellText(XElement row, string column)
        {
            var reference = $"{column}{row.Attribute("r")!.Value}";
            var cell = row.Elements(ns + "c").FirstOrDefault(c => c.Attribute("r")?.Value == reference);
            if (cell is null) return string.Empty;
            var value = cell.Element(ns + "v")?.Value;
            if (value is null) return string.Empty;
            return cell.Attribute("t")?.Value == "s" ? table[int.Parse(value)] : value;
        }

        var cls = project.Classes[0];
        var firstRow = sheet.Descendants(ns + "row").First(r => (int)r.Attribute("r")! == 5);
        Assert.Equal("九年级", CellText(firstRow, "A"));
        Assert.Equal(cls.Name, CellText(firstRow, "B"));
        Assert.Equal("1", CellText(firstRow, "C"));

        var lesson = project.LessonAt(cls.Id, 1, 1);
        Assert.NotNull(lesson);
        Assert.Equal(project.FindSubject(lesson!.SubjectId)!.Name, CellText(firstRow, "D"));
        Assert.Equal(project.FindTeacher(lesson.TeacherId)!.Name, CellText(firstRow, "E"));

        // 周末没排课：N–Q 留空。
        Assert.Equal(string.Empty, CellText(firstRow, "N"));
        Assert.Equal(string.Empty, CellText(firstRow, "Q"));
    }

    [Fact]
    public void Export_MergesLayeredLayersIntoOneCell()
    {
        var project = WeaveSamples.CreateDemo(2);
        var group = project.LayeredGroups[0];
        group.IsFlexible = false;
        group.DayIndex = 1;
        group.StartPeriod = 1;
        group.EndPeriod = 1;
        TimetableService.Confirm(project, WeaveTimetables.FromPreset("lesson-only"));
        new WeaveScheduler().Generate(project);

        var path = Path.Combine(_directory, "seewo-layered.xlsx");
        WeaveSeewoExporter.Export(project, path);

        var rows = WeaveSeewoExporter.BuildRows(project, null);
        var row = rows.First(r => r.ClassName == project.Classes[0].Name && r.Period == 1);
        Assert.Contains("/", row.Subjects[0]);
        Assert.Contains("A层", row.Subjects[0]);
        Assert.Contains("B层", row.Subjects[0]);
        Assert.Contains("/", row.Teachers[0]);
    }

    [Fact]
    public void Export_RejectsOutputOverwritingSource()
    {
        var project = WeaveSamples.CreateDemo(2);
        project.SourceFile = Path.Combine(_directory, "source.xlsx");
        File.WriteAllBytes(project.SourceFile, [1, 2, 3]);

        var error = Assert.Throws<GradeWeaveException>(() =>
            WeaveSeewoExporter.Export(project, project.SourceFile));
        Assert.Contains("避免覆盖源文件", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_RejectsEmptyProject()
    {
        var project = new WeaveProject();
        var error = Assert.Throws<GradeWeaveException>(() =>
            WeaveSeewoExporter.Export(project, Path.Combine(_directory, "empty.xlsx")));
        Assert.Contains("还没有班级", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_RejectsNonSeewoTemplate()
    {
        var project = WeaveSamples.CreateDemo(2);
        var template = Path.Combine(_directory, "not-seewo.xlsx");
        using (var workbook = new ClosedXML.Excel.XLWorkbook())
        {
            workbook.Worksheets.Add("随便一张表").Cell(1, 1).Value = "x";
            workbook.SaveAs(template);
        }

        var error = Assert.Throws<GradeWeaveException>(() =>
            WeaveSeewoExporter.Export(project, Path.Combine(_directory, "out.xlsx"), templatePath: template));
        Assert.Contains("不是希沃", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_WarnsWhenHoursUnplaced()
    {
        var project = WeaveSamples.CreateDemo(2);
        var warnings = new List<string>();
        WeaveSeewoExporter.Export(project, Path.Combine(_directory, "seewo.xlsx"), warnings);

        Assert.Contains(warnings, w => w.Contains("没排", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitGrade_HandlesCommonClassNames()
    {
        Assert.Equal("九年级", WeaveSeewoExporter.SplitGrade("九（1）班"));
        Assert.Equal("九年级", WeaveSeewoExporter.SplitGrade("九年级1班"));
        Assert.Equal("高一年级", WeaveSeewoExporter.SplitGrade("高一8班"));
        Assert.Equal("七年级", WeaveSeewoExporter.SplitGrade("初一3班"));
        Assert.Equal("2026级", WeaveSeewoExporter.SplitGrade("2026级1班"));
        Assert.Equal(string.Empty, WeaveSeewoExporter.SplitGrade("实验班"));
    }

    [Fact]
    public void Export_UpdatesDimensionToLastDataRow()
    {
        var project = WeaveSamples.CreateDemo(2);
        var path = Path.Combine(_directory, "seewo.xlsx");
        WeaveSeewoExporter.Export(project, path);

        using var archive = ZipFile.OpenRead(path);
        var sheet = LoadXml(archive, "xl/worksheets/sheet1.xml");
        XNamespace ns = sheet.Root!.Name.Namespace;
        var dimension = sheet.Descendants(ns + "dimension").Single().Attribute("ref")!.Value;
        var lastRow = sheet.Descendants(ns + "row").Max(r => int.Parse(r.Attribute("r")!.Value));
        Assert.Equal($"A1:S{lastRow}", dimension);
    }

    // ----------------------------- 工具 -----------------------------

    private string ExportSample()
    {
        var project = WeaveSamples.CreateDemo(2);
        TimetableService.Confirm(project, WeaveTimetables.FromPreset("lesson-only"));
        new WeaveScheduler().Generate(project);

        var path = Path.Combine(_directory, "seewo.xlsx");
        WeaveSeewoExporter.Export(project, path);
        return path;
    }

    /// <summary>从程序集里解出内置模板，供逐字节比对。</summary>
    private string ExtractBundledTemplate()
    {
        var path = Path.Combine(_directory, "template.xlsx");
        using var resource = typeof(WeaveSeewoExporter).Assembly
            .GetManifestResourceStream("GradeWeave.SeewoTemplate.xlsx");
        Assert.NotNull(resource);
        using var output = File.Create(path);
        resource!.CopyTo(output);
        return path;
    }

    private static byte[] ReadBytes(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static XDocument LoadXml(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }
}
