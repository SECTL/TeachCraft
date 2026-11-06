using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SeatGrid.App;
using SeatGrid.Core.Export;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;
using SeatGrid.Core.Roster;
using SeatGrid.Core.Seating;

namespace TeachCraft.Tests;

public sealed class SeatGridTests : IDisposable
{
    private readonly string _root;

    public SeatGridTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "SeatGrid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void RosterImporter_ReadsChineseHeadersAndFields()
    {
        var path = Path.Combine(_root, "roster.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("六班");
            string[] headers = { "姓名", "性别", "学号", "身高", "视力", "总分", "备注" };
            for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
            sheet.Cell(2, 1).Value = "陈思远"; sheet.Cell(2, 2).Value = "男"; sheet.Cell(2, 3).Value = "2024001";
            sheet.Cell(2, 4).Value = 175; sheet.Cell(2, 5).Value = 4.8; sheet.Cell(2, 6).Value = 560; sheet.Cell(2, 7).Value = "近视";
            sheet.Cell(3, 1).Value = "李雨桐"; sheet.Cell(3, 2).Value = "女生"; sheet.Cell(3, 3).Value = "2024002";
            sheet.Cell(3, 4).Value = "162cm"; sheet.Cell(3, 5).Value = "5.0/4.9"; sheet.Cell(3, 6).Value = 548;
            workbook.SaveAs(path);
        }

        var result = RosterImporter.Import(path);

        Assert.Equal(2, result.Students.Count);
        Assert.Equal("陈思远", result.Students[0].Name);
        Assert.Equal(Gender.Male, result.Students[0].Gender);
        Assert.Equal("2024001", result.Students[0].Number);
        Assert.Equal(175, result.Students[0].Height);
        Assert.Equal(4.8, result.Students[0].Vision);
        Assert.Equal(Gender.Female, result.Students[1].Gender);
        Assert.Equal(162, result.Students[1].Height);
        Assert.Equal(4.9, result.Students[1].Vision);
    }

    [Fact]
    public void RosterImporter_DetectsHeaderlessTwoColumnRoster()
    {
        var path = Path.Combine(_root, "no-header.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Sheet1");
            sheet.Cell(1, 1).Value = "张三"; sheet.Cell(1, 2).Value = "男"; sheet.Cell(1, 3).Value = "2024001";
            sheet.Cell(2, 1).Value = "李四"; sheet.Cell(2, 2).Value = "女"; sheet.Cell(2, 3).Value = "2024002";
            sheet.Cell(3, 1).Value = "王五"; sheet.Cell(3, 2).Value = "男"; sheet.Cell(3, 3).Value = "2024003";
            workbook.SaveAs(path);
        }

        var result = RosterImporter.Import(path);

        Assert.Equal(3, result.Students.Count);
        Assert.Equal(new[] { "张三", "李四", "王五" }, result.Students.Select(s => s.Name));
        Assert.Equal(Gender.Male, result.Students[0].Gender);
        Assert.Equal(Gender.Female, result.Students[1].Gender);
    }

    [Fact]
    public void SeatingEngine_HeightPutsShortestStudentsInFrontRow()
    {
        var students = new List<Student>
        {
            new() { Name = "高个", Height = 190 },
            new() { Name = "最矮", Height = 150 },
            new() { Name = "中等", Height = 170 },
            new() { Name = "较矮", Height = 160 },
        };

        var layout = new SeatChartLayout(1, 2, 2); // 前排在 Row=0
        var seats = SeatingEngine.Arrange(students, layout, ArrangeMode.Height);

        Assert.Equal("最矮", seats[new SeatPosition(0, 0, 0)].Name);
        Assert.Equal("较矮", seats[new SeatPosition(0, 0, 1)].Name);
        Assert.Equal("中等", seats[new SeatPosition(0, 1, 0)].Name);
        Assert.Equal("高个", seats[new SeatPosition(0, 1, 1)].Name);
    }

    [Fact]
    public void SeatingEngine_GenderAlternateProducesAlternatingGenders()
    {
        var students = new List<Student>
        {
            new() { Name = "男1", Gender = Gender.Male },
            new() { Name = "女1", Gender = Gender.Female },
            new() { Name = "男2", Gender = Gender.Male },
            new() { Name = "女2", Gender = Gender.Female },
        };

        var layout = new SeatChartLayout(1, 2, 2);
        var seats = SeatingEngine.Arrange(students, layout, ArrangeMode.GenderAlternate);
        var ordered = layout.FillOrder().Select(p => seats[p].Gender).ToList();

        Assert.Equal(new[] { Gender.Male, Gender.Female, Gender.Male, Gender.Female }, ordered);
    }

    [Fact]
    public void SeatingEngine_GenderPairGroupsSameGenderDeskmates()
    {
        var students = new List<Student>
        {
            new() { Name = "男1", Gender = Gender.Male },
            new() { Name = "女1", Gender = Gender.Female },
            new() { Name = "男2", Gender = Gender.Male },
            new() { Name = "女2", Gender = Gender.Female },
            new() { Name = "未知1" },
        };

        var layout = new SeatChartLayout(1, 3, 2);
        var seats = SeatingEngine.Arrange(students, layout, ArrangeMode.GenderPair);
        var ordered = layout.FillOrder().Take(students.Count).Select(p => seats[p].Gender).ToList();

        // 男男成对 → 女女成对 → 性别未知排在最后
        Assert.Equal(
            new[] { Gender.Male, Gender.Male, Gender.Female, Gender.Female, Gender.Unknown },
            ordered);
    }

    [Fact]
    public void SeatingEngine_GenderPairKeepsRunningWhenOneSideRunsOut()
    {
        var students = new List<Student>
        {
            new() { Name = "男1", Gender = Gender.Male },
            new() { Name = "男2", Gender = Gender.Male },
            new() { Name = "男3", Gender = Gender.Male },
            new() { Name = "女1", Gender = Gender.Female },
            new() { Name = "女2", Gender = Gender.Female },
        };

        var layout = new SeatChartLayout(1, 3, 2);
        var seats = SeatingEngine.Arrange(students, layout, ArrangeMode.GenderPair);
        var ordered = layout.FillOrder().Take(students.Count).Select(p => seats[p].Gender).ToList();

        // 男 3 人 / 女 2 人：男男 → 女女 → 剩下 1 名男生连排
        Assert.Equal(
            new[] { Gender.Male, Gender.Male, Gender.Female, Gender.Female, Gender.Male },
            ordered);
    }

    [Fact]
    public void Config_RoundTripsSettingsStudentsAndSeats()
    {
        var settings = new SeatChartSettings
        {
            Title = "九年级六班座位表",
            HeadTeacher = "王老师",
            Monitor = "陈思远",
            Groups = 2,
            Rows = 2,
            Columns = 2,
            CellScalePercent = 120,
            SeatBackground = "#FFF2CC",
            MarkFemales = false,
        };

        var students = new List<Student>
        {
            new() { Name = "陈思远", Gender = Gender.Male, Number = "1", Height = 175 },
            new() { Name = "李雨桐", Gender = Gender.Female, Number = "2", Vision = 4.8 },
            new() { Name = "王浩然", Gender = Gender.Male, Number = "3" },
        };

        var assignments = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
            [new SeatPosition(0, 0, 1)] = students[1],
        };

        var path = Path.Combine(_root, "config.xlsx");
        SeatGridConfigIO.Export(path, settings, students, assignments);
        var imported = SeatGridConfigIO.Import(path);

        Assert.Equal("九年级六班座位表", imported.Settings.Title);
        Assert.Equal("王老师", imported.Settings.HeadTeacher);
        Assert.Equal(2, imported.Settings.Groups);
        Assert.Equal(120, imported.Settings.CellScalePercent);
        Assert.Equal("#FFF2CC", imported.Settings.SeatBackground);
        Assert.False(imported.Settings.MarkFemales);
        Assert.Equal(3, imported.Students.Count);
        Assert.Equal(2, imported.Assignments.Count);
        Assert.Equal("李雨桐", imported.Assignments[new SeatPosition(0, 0, 1)].Name);
    }

    [Fact]
    public void Config_KeepsUnassignedStudentsUnseated()
    {
        // 未排座的学生曾被写成「1 组 1 排 1 列」：排在名单前面时会抢走真正坐在那里的同学。
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };
        var waiting = new Student { Name = "王浩然", Gender = Gender.Male };
        var seated = new Student { Name = "陈思远", Gender = Gender.Male };
        var assignments = new Dictionary<SeatPosition, Student> { [new SeatPosition(0, 0, 0)] = seated };

        var path = Path.Combine(_root, "unassigned.xlsx");
        SeatGridConfigIO.Export(path, settings, new List<Student> { waiting, seated }, assignments);
        var imported = SeatGridConfigIO.Import(path);

        Assert.Empty(imported.Warnings);
        Assert.Equal("陈思远", Assert.Single(imported.Assignments).Value.Name);
    }

    [Fact]
    public void RosterImporter_PrefersStudentNumberOverIndexColumn()
    {
        // 软件导出的名单页与很多花名册都是「序号」在「学号」前；序号只在没有学号列时兜底。
        var both = WriteRoster("both.xlsx", new[] { "序号", "姓名", "性别", "学号" },
            new[] { "1", "陈思远", "男", "2026001" }, new[] { "2", "李雨桐", "女", "2026002" });
        Assert.Equal(new[] { "2026001", "2026002" }, RosterImporter.Import(both).Students.Select(s => s.Number));

        var indexOnly = WriteRoster("index-only.xlsx", new[] { "序号", "姓名", "性别" },
            new[] { "1", "陈思远", "男" }, new[] { "2", "李雨桐", "女" });
        Assert.Equal(new[] { "1", "2" }, RosterImporter.Import(indexOnly).Students.Select(s => s.Number));
    }

    [Fact]
    public void SeatChartExcel_ReimportsWithSettingsAndSeats()
    {
        // 「导出 Excel」带一张隐藏的设置表：导回后布局、配色、标题与座位都还原，未排座的同学仍未排座。
        var (settings, students, assignments) = ChartFixture();
        var path = Path.Combine(_root, "chart.xlsx");
        SeatChartExcelExporter.ExportSeatChart(
            SeatChartComposer.Compose(settings, assignments, students.Count), assignments, students, path);

        using (var workbook = new XLWorkbook(path))
        {
            Assert.Equal("座位表", workbook.Worksheet(1).Name);
            Assert.Equal(XLWorksheetVisibility.Hidden, workbook.Worksheet("座位设置").Visibility);
        }

        var imported = SeatGridConfigIO.Import(path);
        Assert.Empty(imported.Warnings);
        Assert.Equal(ViewMode.Student, imported.Settings.View);
        Assert.Equal((4, 2, 2), (imported.Settings.Groups, imported.Settings.Rows, imported.Settings.Columns));
        Assert.Equal("九年级六班座位表", imported.Settings.Title);
        Assert.Equal("王老师", imported.Settings.HeadTeacher);
        Assert.Equal("#FFF2CC", imported.Settings.SeatBackground);
        Assert.Equal(students.Count, imported.Students.Count);
        Assert.Equal("2026003", imported.Students[0].Number);
        AssertSameSeats(assignments, imported);
    }

    [Fact]
    public void SeatChartExcel_ReimportsOlderExportsFromRosterPage()
    {
        // 旧版导出没有隐藏设置表，未排座的同学还被写成「1 组 1 排 1 列」：按名单页的组 / 排 / 列还原座位，
        // 布局按组 / 排标题推算，第 1 组第 1 排第 1 列只认座位表上真的出现过的那位同学。
        var (settings, students, assignments) = ChartFixture();
        var path = Path.Combine(_root, "legacy-chart.xlsx");
        SeatChartExcelExporter.ExportSeatChart(
            SeatChartComposer.Compose(settings, assignments, students.Count), assignments, students, path);
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("座位设置").Delete();
            var roster = workbook.Worksheet("名单");
            var header = roster.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
            var waiting = roster.RowsUsed().First(r => r.Cell(header["姓名"]).GetString() == "王浩然");
            foreach (var key in new[] { "组", "排", "列" }) waiting.Cell(header[key]).Value = 1;
            workbook.Save();
        }

        var imported = SeatGridConfigIO.Import(path);
        Assert.Equal(ViewMode.Student, imported.Settings.View);
        Assert.Equal((4, 2, 2), (imported.Settings.Groups, imported.Settings.Rows, imported.Settings.Columns));
        Assert.Equal("九年级六班座位表", imported.Settings.Title);
        Assert.Equal("王老师", imported.Settings.HeadTeacher);
        Assert.Contains(imported.Warnings, w => w.Contains("旧版"));
        AssertSameSeats(assignments, imported);
    }

    [Fact]
    public void Composer_StudentViewReversesGroupsAndRows()
    {
        var settings = new SeatChartSettings
        {
            Groups = 2,
            Rows = 2,
            Columns = 1,
            View = ViewMode.Teacher,
        };
        var empty = new Dictionary<SeatPosition, Student>();

        var teacher = SeatChartComposer.Compose(settings, empty, 0);
        Assert.False(teacher.PodiumOnTop);
        Assert.Equal(new[] { 1, 2 }, teacher.Groups.Select(g => g.Number));
        Assert.Equal(new[] { 2, 1 }, teacher.Groups[0].Rows.Select(r => r.Number));

        settings.View = ViewMode.Student;
        var student = SeatChartComposer.Compose(settings, empty, 0);
        Assert.True(student.PodiumOnTop);
        Assert.Equal(new[] { 2, 1 }, student.Groups.Select(g => g.Number));
        Assert.Equal(new[] { 1, 2 }, student.Groups[0].Rows.Select(r => r.Number));
    }

    [Fact]
    public void Composer_StudentViewIsTeacherViewRotated180()
    {
        // 每组 2 列才能暴露同桌左右：只倒组序、不倒组内列序时，学生视角里同桌会左右对调。
        var settings = new SeatChartSettings { Groups = 3, Rows = 2, Columns = 2, View = ViewMode.Teacher };
        var empty = new Dictionary<SeatPosition, Student>();

        var teacher = ScreenGrid(SeatChartComposer.Compose(settings, empty, 0));
        settings.View = ViewMode.Student;
        var student = ScreenGrid(SeatChartComposer.Compose(settings, empty, 0));

        var rows = teacher.Count;
        var columns = teacher[0].Count;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                Assert.Equal(teacher[rows - 1 - r][columns - 1 - c], student[r][c]);
    }

    [Fact]
    public void ShiftOnScreen_FollowsScreenDirectionInBothViews()
    {
        var layout = new SeatChartLayout(2, 2, 2);
        var start = new SeatPosition(0, 0, 0);
        var seated = new Dictionary<SeatPosition, string> { [start] = "甲" };

        // 教师视角第 1 组在最左：(0,0,0) 是第一排最左的座位，向右一格是同桌 (0,0,1)。
        var teacherRight = SeatChartComposer.ShiftOnScreen(seated, layout, ViewMode.Teacher, 1, 0);
        Assert.Equal(new SeatPosition(0, 0, 1), teacherRight.Single().Key);

        // 学生视角整体旋转 180°：同一座位落在第一排最右，向左一格才是同桌 (0,0,1)。
        var studentLeft = SeatChartComposer.ShiftOnScreen(seated, layout, ViewMode.Student, -1, 0);
        Assert.Equal(new SeatPosition(0, 0, 1), studentLeft.Single().Key);

        // 学生视角第一排在最上：向上一格绕回到最后一排。
        var studentUp = SeatChartComposer.ShiftOnScreen(seated, layout, ViewMode.Student, 0, -1);
        Assert.Equal(new SeatPosition(0, 1, 0), studentUp.Single().Key);
    }

    [Fact]
    public void ApplyConfig_RefreshesViewButtonsAndLayoutLabels()
    {
        // 导入配置时座位重建被挂起，但视角按钮与滑块旁的数值必须照常收到通知，否则界面停在旧值。
        var vm = new SeatGridViewModel();
        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        var settings = new SeatChartSettings
        {
            View = ViewMode.Student, Groups = 3, Rows = 5, Columns = 3, CellScalePercent = 120,
        };
        vm.ApplyConfig(new SeatGridConfig(settings, new List<Student>(), new Dictionary<SeatPosition, Student>(), new List<string>()));

        Assert.False(vm.IsTeacherView);
        foreach (var name in new[]
                 {
                     nameof(SeatGridViewModel.IsTeacherView), nameof(SeatGridViewModel.GroupCountLabel),
                     nameof(SeatGridViewModel.RowCountLabel), nameof(SeatGridViewModel.ColumnCountLabel),
                     nameof(SeatGridViewModel.CellSizeLabel),
                 })
            Assert.Contains(name, raised);
        Assert.Equal("3", vm.GroupCountLabel);
        Assert.Equal("96 × 96px", vm.CellSizeLabel);
    }

    /// <summary>导出 / 导回用例共用：学生视角、4 组（第 4 组整组空着）、未排座的同学排在名单最前。</summary>
    private static (SeatChartSettings Settings, List<Student> Students, Dictionary<SeatPosition, Student> Assignments) ChartFixture()
    {
        var settings = new SeatChartSettings
        {
            Title = "九年级六班座位表", HeadTeacher = "王老师", View = ViewMode.Student,
            Groups = 4, Rows = 2, Columns = 2, SeatBackground = "#FFF2CC",
        };
        var students = new List<Student>
        {
            new() { Name = "王浩然", Gender = Gender.Male, Number = "2026003" },
            new() { Name = "陈思远", Gender = Gender.Male, Number = "2026001" },
            new() { Name = "李雨桐", Gender = Gender.Female, Number = "2026002" },
            new() { Name = "张欣怡", Gender = Gender.Female, Number = "2026004" },
            new() { Name = "刘子涵", Gender = Gender.Male, Number = "2026005" },
        };
        var assignments = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[1],
            [new SeatPosition(0, 0, 1)] = students[2],
            [new SeatPosition(1, 1, 0)] = students[3],
            [new SeatPosition(2, 1, 1)] = students[4],
        };
        return (settings, students, assignments);
    }

    private static void AssertSameSeats(Dictionary<SeatPosition, Student> expected, SeatGridConfig imported)
    {
        static IEnumerable<(SeatPosition, string)> Ordered(IEnumerable<KeyValuePair<SeatPosition, Student>> seats) =>
            seats.OrderBy(kv => kv.Key.Group).ThenBy(kv => kv.Key.Row).ThenBy(kv => kv.Key.Column)
                .Select(kv => (kv.Key, kv.Value.Name));

        Assert.Equal(Ordered(expected), Ordered(imported.Assignments));
    }

    private string WriteRoster(string fileName, string[] headers, params string[][] rows)
    {
        var path = Path.Combine(_root, fileName);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("六班");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];
        workbook.SaveAs(path);
        return path;
    }

    /// <summary>把文档按屏幕摆放展开成「行 × 列」的物理坐标矩阵（各组从左到右拼接）。</summary>
    private static List<List<SeatPosition>> ScreenGrid(SeatChartDocument document)
    {
        var rows = document.Groups[0].Rows.Count;
        return Enumerable.Range(0, rows)
            .Select(r => document.Groups.SelectMany(g => g.Rows[r].Seats.Select(s => s.Position)).ToList())
            .ToList();
    }

    [Fact]
    public void Exports_ProducePngPdfAndExcel()
    {
        var settings = new SeatChartSettings
        {
            Title = "九年级六班座位表",
            Groups = 4,
            Rows = 2,
            Columns = 2,
        };
        var students = Enumerable.Range(0, 8).Select(i => new Student
        {
            Name = $"同学{i + 1}",
            Gender = i % 2 == 0 ? Gender.Male : Gender.Female,
        }).ToList();
        var assignments = SeatingEngine.Arrange(students, settings.GetLayout(), ArrangeMode.RosterOrder);
        var document = SeatChartComposer.Compose(settings, assignments, students.Count);

        var pngPath = Path.Combine(_root, "seat.png");
        SeatChartRenderer.RenderPng(document, pngPath);
        using (var codec = SKCodec.Create(pngPath))
        {
            Assert.NotNull(codec);
            Assert.True(codec!.Info.Width > 300);
        }

        var pdfPath = Path.Combine(_root, "seat.pdf");
        SeatChartRenderer.RenderPdf(document, pdfPath);
        Assert.True(new FileInfo(pdfPath).Length > 1024);
        Assert.Equal("%PDF", File.ReadAllText(pdfPath, System.Text.Encoding.ASCII)[..4]);
        Assert.True(new FileInfo(pdfPath).Length < 5 * 1024 * 1024);

        var excelPath = Path.Combine(_root, "seat.xlsx");
        SeatChartExcelExporter.ExportSeatChart(document, assignments, students, excelPath);
        using var workbook = new XLWorkbook(excelPath);
        Assert.True(workbook.Worksheets.Contains("座位表"));
        Assert.True(workbook.Worksheets.Contains("名单"));
        var sheet = workbook.Worksheet("座位表");
        Assert.Contains("同学1", sheet.CellsUsed().Select(c => c.GetFormattedString()));
    }
}
