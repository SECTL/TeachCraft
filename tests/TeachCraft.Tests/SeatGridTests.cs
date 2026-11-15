using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SeatGrid.App;
using SeatGrid.Core;
using SeatGrid.Core.Exam;
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

    // ----------------------------- 1.25.0 约束引擎 -----------------------------

    [Fact]
    public void Layout_AreAdjacentIsFourNeighborhoodAndNeverCrossesGroups()
    {
        var layout = new SeatChartLayout(2, 3, 2);

        Assert.True(layout.AreAdjacent(new SeatPosition(0, 0, 0), new SeatPosition(0, 0, 1)));
        Assert.True(layout.AreAdjacent(new SeatPosition(0, 0, 0), new SeatPosition(0, 1, 0)));

        // 组与组之间是过道，物理上不相邻。
        Assert.False(layout.AreAdjacent(new SeatPosition(0, 0, 0), new SeatPosition(1, 0, 0)));

        // 对角不算相邻。
        Assert.False(layout.AreAdjacent(new SeatPosition(0, 0, 0), new SeatPosition(0, 1, 1)));
    }

    [Fact]
    public void Layout_NeighborsOfHasAtMostFourAndClampsAtEdges()
    {
        var layout = new SeatChartLayout(1, 3, 3);

        Assert.Equal(4, layout.NeighborsOf(new SeatPosition(0, 1, 1)).Count());
        Assert.Equal(2, layout.NeighborsOf(new SeatPosition(0, 0, 0)).Count());
        Assert.Equal(3, layout.NeighborsOf(new SeatPosition(0, 0, 1)).Count());
    }

    [Fact]
    public void Layout_DeskmateOfPairsEvenColumns()
    {
        var two = new SeatChartLayout(1, 1, 2);
        Assert.Equal(new SeatPosition(0, 0, 1), two.DeskmateOf(new SeatPosition(0, 0, 0)));
        Assert.Equal(new SeatPosition(0, 0, 0), two.DeskmateOf(new SeatPosition(0, 0, 1)));

        var four = new SeatChartLayout(1, 1, 4);
        Assert.Equal(new SeatPosition(0, 0, 1), four.DeskmateOf(new SeatPosition(0, 0, 0)));
        Assert.Equal(new SeatPosition(0, 0, 3), four.DeskmateOf(new SeatPosition(0, 0, 2)));
    }

    [Fact]
    public void Layout_DeskmateOfReturnsNullForSingleColumnAndOddTail()
    {
        Assert.Null(new SeatChartLayout(1, 1, 1).DeskmateOf(new SeatPosition(0, 0, 0)));
        Assert.Null(new SeatChartLayout(1, 1, 3).DeskmateOf(new SeatPosition(0, 0, 2)));
        Assert.NotNull(new SeatChartLayout(1, 1, 3).DeskmateOf(new SeatPosition(0, 0, 0)));
    }

    [Fact]
    public void Layout_AreDeskmatesNeverCrossesGroupOrRow()
    {
        var layout = new SeatChartLayout(2, 2, 2);

        Assert.True(layout.AreDeskmates(new SeatPosition(0, 0, 0), new SeatPosition(0, 0, 1)));
        Assert.False(layout.AreDeskmates(new SeatPosition(0, 0, 1), new SeatPosition(1, 0, 0)));
        Assert.False(layout.AreDeskmates(new SeatPosition(0, 0, 0), new SeatPosition(0, 1, 1)));
    }

    [Fact]
    public void SeatingEngine_PinnedStudentKeepsSeat()
    {
        var students = MakeStudents(6);
        var layout = new SeatChartLayout(1, 3, 2);
        var pin = new SeatPosition(0, 2, 1);

        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[0].Id] = pin },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal("同学1", result.Assignments[pin].Name);
    }

    [Fact]
    public void SeatingEngine_PinnedSeatIsNotGivenToOthers()
    {
        var students = MakeStudents(6);
        var layout = new SeatChartLayout(1, 3, 2);
        var pin = new SeatPosition(0, 1, 0);

        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[3].Id] = pin },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal("同学4", result.Assignments[pin].Name);
    }

    [Fact]
    public void SeatingEngine_PinOutsideLayoutIsReportedAndDropped()
    {
        var students = MakeStudents(3);
        var layout = new SeatChartLayout(1, 2, 2);

        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[0].Id] = new SeatPosition(9, 0, 0) },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Contains(result.Issues, i => i.Kind == SeatIssueKind.PinOutOfLayout);
        Assert.Equal(3, result.Assignments.Count);
        Assert.DoesNotContain(new SeatPosition(9, 0, 0), result.Assignments.Keys);
    }

    [Fact]
    public void SeatingEngine_TwoPinsToOneSeatKeepsFirstAndReports()
    {
        var students = MakeStudents(3);
        var layout = new SeatChartLayout(1, 2, 2);
        var seat = new SeatPosition(0, 0, 0);

        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition>
            {
                [students[0].Id] = seat,
                [students[1].Id] = seat,
            },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal("同学1", result.Assignments[seat].Name);
        var issue = Assert.Single(result.Issues, i => i.Kind == SeatIssueKind.PinConflict);
        Assert.Contains("同学1", issue.Message);
        Assert.Contains("同学2", issue.Message);
    }

    [Fact]
    public void SeatingEngine_MutexPairIsNeverAdjacent()
    {
        var students = MakeStudents(16);
        var layout = new SeatChartLayout(2, 4, 2);

        var constraints = new ArrangeConstraints
        {
            Mutex = new[] { StudentPair.Create(students[0].Id, students[1].Id) },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        var seatA = SeatOf(result.Assignments, students[0].Id);
        var seatB = SeatOf(result.Assignments, students[1].Id);
        Assert.NotNull(seatA);
        Assert.NotNull(seatB);
        Assert.False(layout.AreAdjacent(seatA!.Value, seatB!.Value));
    }

    [Fact]
    public void SeatingEngine_MutexUnsatisfiableIsReportedNotThrown()
    {
        var students = MakeStudents(2);
        var layout = new SeatChartLayout(1, 1, 2);   // 只有两个座位，必然相邻

        var constraints = new ArrangeConstraints
        {
            Mutex = new[] { StudentPair.Create(students[0].Id, students[1].Id) },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal(2, result.Assignments.Count);
        var issue = Assert.Single(result.Issues, i => i.Kind == SeatIssueKind.MutexUnsatisfied);
        Assert.Contains("同学1", issue.Message);
        Assert.Contains("同学2", issue.Message);
    }

    [Fact]
    public void SeatingEngine_DeskmatePairSitsTogether()
    {
        var students = MakeStudents(4);
        var layout = new SeatChartLayout(1, 2, 2);

        var constraints = new ArrangeConstraints
        {
            Deskmates = new[] { StudentPair.Create(students[0].Id, students[2].Id) },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        var seatA = SeatOf(result.Assignments, students[0].Id);
        var seatB = SeatOf(result.Assignments, students[2].Id);
        Assert.True(layout.AreDeskmates(seatA!.Value, seatB!.Value));
    }

    [Fact]
    public void SeatingEngine_DeskmateWithSingleColumnIsReported()
    {
        var students = MakeStudents(3);
        var layout = new SeatChartLayout(1, 3, 1);   // 每组 1 列，没有同桌

        var constraints = new ArrangeConstraints
        {
            Deskmates = new[] { StudentPair.Create(students[0].Id, students[1].Id) },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal(3, result.Assignments.Count);
        Assert.Contains(result.Issues, i => i.Kind == SeatIssueKind.DeskmateUnsatisfied);
    }

    [Fact]
    public void SeatingEngine_GenderPairUsesDeskmatePairsWhenThreeColumns()
    {
        // 回归：旧实现靠 FillOrder 的列循环碰巧成对，Columns=3 时第 3 列会与下一排第 1 列配成同桌。
        var students = new List<Student>
        {
            new() { Name = "男1", Gender = Gender.Male },
            new() { Name = "男2", Gender = Gender.Male },
            new() { Name = "男3", Gender = Gender.Male },
        };
        var layout = new SeatChartLayout(1, 2, 3);

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.GenderPair);

        // 第 3 列落单，不能与下一排第 1 列成为同桌。
        Assert.False(layout.AreDeskmates(new SeatPosition(0, 0, 2), new SeatPosition(0, 1, 0)));
    }

    [Fact]
    public void SeatingEngine_ScoreSnakeBalancesAcrossGroups()
    {
        var students = Enumerable.Range(0, 40).Select(i => new Student
        {
            Name = $"同学{i + 1}",
            Score = i + 1,
        }).ToList();
        var layout = new SeatChartLayout(4, 5, 2);

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.ScoreSnake, ArrangeConstraints.Empty);
        var averages = ScorePlanner.GroupAverages(result.Assignments, 4);

        var spread = averages.Values.Max() - averages.Values.Min();
        Assert.True(spread <= 2.0, $"各组均分极差 {spread:F1} 过大：{string.Join(" / ", averages.Values)}");
    }

    [Fact]
    public void SeatingEngine_ScoreModeWithAllNullFallsBackToRosterOrder()
    {
        var students = MakeStudents(8);
        var layout = new SeatChartLayout(2, 2, 2);

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.ScoreSnake, ArrangeConstraints.Empty);

        Assert.Contains(result.Issues, i => i.Kind == SeatIssueKind.ScoreMissing);
        Assert.Equal("同学1", result.Assignments[new SeatPosition(0, 0, 0)].Name);
    }

    [Fact]
    public void SeatingEngine_UnknownArrangeModeThrowsInsteadOfSilentlyRosterOrder()
    {
        var students = MakeStudents(2);
        var layout = new SeatChartLayout(1, 1, 2);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SeatingEngine.Arrange(students, layout, (ArrangeMode)99));
    }

    [Fact]
    public void ScorePlanner_SnakeIsDeterministic()
    {
        var students = Enumerable.Range(0, 20).Select(i => new Student
        {
            Name = $"同学{i + 1}",
            Score = (i * 7) % 20,
        }).ToList();
        var layout = new SeatChartLayout(4, 5, 1);

        var first = ScorePlanner.TargetGroups(students, layout, ArrangeMode.ScoreSnake);
        var second = ScorePlanner.TargetGroups(students, layout, ArrangeMode.ScoreSnake);

        Assert.Equal(first.OrderBy(kv => kv.Key), second.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void ScorePlanner_NullScoresAreExcludedAndTrailing()
    {
        var students = MakeStudents(6);
        students[0].Score = 90;
        students[1].Score = 80;
        var layout = new SeatChartLayout(2, 3, 1);

        var targets = ScorePlanner.TargetGroups(students, layout, ArrangeMode.ScoreSnake);

        Assert.Equal(2, targets.Count);
        Assert.DoesNotContain(students[2].Id, targets.Keys);
    }

    [Fact]
    public void ScorePlanner_FoldAlternatesHighAndLow()
    {
        var group = new List<Student>
        {
            new() { Name = "高分", Score = 100 },
            new() { Name = "中分", Score = 60 },
            new() { Name = "低分", Score = 20 },
        };

        var ordered = ScorePlanner.OrderWithin(group, WithinGroupOrder.Fold);

        Assert.Equal("高分", ordered[0].Name);
        Assert.Equal("低分", ordered[1].Name);
        Assert.Equal("中分", ordered[2].Name);
    }

    [Fact]
    public void SeatingEngine_MutexAndDeskmateConflictIsReported()
    {
        var students = MakeStudents(4);
        var layout = new SeatChartLayout(1, 2, 2);
        var pair = StudentPair.Create(students[0].Id, students[1].Id);

        var constraints = new ArrangeConstraints
        {
            Mutex = new[] { pair },
            Deskmates = new[] { pair },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        // 结对优先成桌，互斥因此无法满足——两条里至少有一条如实报告。
        Assert.Contains(result.Issues,
            i => i.Kind is SeatIssueKind.MutexUnsatisfied or SeatIssueKind.DeskmateUnsatisfied);
    }

    [Fact]
    public void SeatZone_AllowsHonoursAllowedAndDenied()
    {
        var layout = new SeatChartLayout(2, 4, 2);
        var zone = new StudentZone(
            new[] { SeatRect.RowBand(layout, 0, 1) },
            new[] { SeatRect.GroupBand(layout, 0) });

        Assert.True(zone.Allows(new SeatPosition(1, 0, 0)));
        Assert.False(zone.Allows(new SeatPosition(1, 2, 0)));   // 第 3 排，不在允许区
        Assert.False(zone.Allows(new SeatPosition(0, 0, 0)));   // 第 1 组，被禁止
    }

    [Fact]
    public void SeatZone_RectNormalizesSwappedAndOutOfRange()
    {
        var layout = new SeatChartLayout(2, 3, 2);
        var rect = new SeatRect(5, 0, 2, 0, 0, 9).Normalize(layout);

        Assert.Equal(0, rect.GroupFrom);
        Assert.Equal(1, rect.GroupTo);
        Assert.Equal(0, rect.RowFrom);
        Assert.Equal(2, rect.RowTo);
        Assert.Equal(1, rect.ColumnTo);
    }

    [Fact]
    public void SeatingEngine_ZoneRestrictionKeepsStudentInFrontTwoRows()
    {
        var students = MakeStudents(4);
        var layout = new SeatChartLayout(1, 4, 2);

        var constraints = new ArrangeConstraints
        {
            Zones = new Dictionary<Guid, StudentZone>
            {
                [students[3].Id] = new StudentZone(
                    new[] { SeatRect.RowBand(layout, 0, 1) }, Array.Empty<SeatRect>()),
            },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);
        var seat = SeatOf(result.Assignments, students[3].Id);

        Assert.NotNull(seat);
        Assert.True(seat!.Value.Row <= 1, $"实际落在第 {seat.Value.Row + 1} 排");
    }

    [Fact]
    public void SeatingEngine_ZoneAndHeightComposeFrontFirst()
    {
        // 区域是候选集裁剪，不参与排序：裁剪后仍按前排优先，矮个子落在允许区的最前排。
        var students = new List<Student>
        {
            new() { Name = "高个", Height = 190 },
            new() { Name = "最矮", Height = 150 },
        };
        var layout = new SeatChartLayout(1, 4, 1);
        var zone = new StudentZone(new[] { SeatRect.RowBand(layout, 1, 3) }, Array.Empty<SeatRect>());

        var constraints = new ArrangeConstraints
        {
            Zones = students.ToDictionary(s => s.Id, _ => zone),
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.Height, constraints);

        Assert.Equal("最矮", result.Assignments[new SeatPosition(0, 1, 0)].Name);
    }

    [Fact]
    public void SeatingEngine_ZoneUnsatisfiableRelaxesAndReports()
    {
        var students = MakeStudents(3);
        var layout = new SeatChartLayout(1, 3, 1);
        var tiny = new StudentZone(new[] { SeatRect.RowBand(layout, 0, 0) }, Array.Empty<SeatRect>());

        var constraints = new ArrangeConstraints
        {
            Zones = students.ToDictionary(s => s.Id, _ => tiny),
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal(3, result.Assignments.Count);
        Assert.Contains(result.Issues, i => i.Kind == SeatIssueKind.ZoneUnsatisfied);
    }

    [Fact]
    public void SeatingEngine_AvoidsPreviousDeskmatesWhenPossible()
    {
        var students = MakeStudents(4);
        var layout = new SeatChartLayout(1, 2, 2);

        var constraints = new ArrangeConstraints
        {
            AvoidDeskmates = new[] { StudentPair.Create(students[0].Id, students[1].Id) },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        var seatA = SeatOf(result.Assignments, students[0].Id);
        var seatB = SeatOf(result.Assignments, students[1].Id);
        Assert.False(layout.AreDeskmates(seatA!.Value, seatB!.Value));
    }

    [Fact]
    public void SeatingEngine_RepairNeverMovesPinnedStudent()
    {
        var students = MakeStudents(8);
        var layout = new SeatChartLayout(2, 2, 2);
        var pin = new SeatPosition(1, 1, 1);

        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[7].Id] = pin },
            Mutex = new[]
            {
                StudentPair.Create(students[0].Id, students[1].Id),
                StudentPair.Create(students[2].Id, students[3].Id),
                StudentPair.Create(students[4].Id, students[5].Id),
            },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);

        Assert.Equal("同学8", result.Assignments[pin].Name);
    }

    [Fact]
    public void Config_RoundTripsLocks()
    {
        var students = MakeStudents(4);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };
        var assignments = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
        };
        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[0].Id] = new SeatPosition(0, 0, 0) },
        };

        var path = Path.Combine(_root, "locks.xlsx");
        SeatGridConfigIO.Export(path, settings, students, assignments, constraints);
        var imported = SeatGridConfigIO.Import(path);

        Assert.NotNull(imported.Constraints);
        var pin = Assert.Single(imported.Constraints!.Pins);
        Assert.Equal(students[0].Name, imported.Students.Single(s => s.Id == pin.Key).Name);
        Assert.Equal(new SeatPosition(0, 0, 0), pin.Value);
    }

    [Fact]
    public void Config_RoundTripsMutexAndDeskmateRules()
    {
        var students = MakeStudents(4);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };
        var constraints = new ArrangeConstraints
        {
            Mutex = new[] { StudentPair.Create(students[0].Id, students[1].Id) },
            Deskmates = new[] { StudentPair.Create(students[2].Id, students[3].Id) },
            AvoidDeskmates = new[] { StudentPair.Create(students[0].Id, students[2].Id) },
        };

        var path = Path.Combine(_root, "pairs.xlsx");
        SeatGridConfigIO.Export(path, settings, students,
            new Dictionary<SeatPosition, Student>(), constraints);
        var imported = SeatGridConfigIO.Import(path);

        Assert.NotNull(imported.Constraints);
        Assert.Single(imported.Constraints!.Mutex);
        Assert.Single(imported.Constraints.Deskmates);
        Assert.Single(imported.Constraints.AvoidDeskmates);

        // 规则按「序号 + 姓名」解析回来，必须仍指向同一对学生（StudentPair 会规范化顺序，不比先后）。
        var importedNames = imported.Students.Select(s => s.Name).ToList();
        var pair = imported.Constraints.Mutex[0];
        var nameA = importedNames[imported.Students.FindIndex(s => s.Id == pair.A)];
        var nameB = importedNames[imported.Students.FindIndex(s => s.Id == pair.B)];
        Assert.Equal(
            new[] { students[0].Name, students[1].Name }.OrderBy(n => n, StringComparer.Ordinal),
            new[] { nameA, nameB }.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Config_DropsRuleWhenStudentIsMissingWithWarning()
    {
        var students = MakeStudents(3);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };
        var constraints = new ArrangeConstraints
        {
            Mutex = new[] { StudentPair.Create(students[0].Id, students[1].Id) },
        };

        var path = Path.Combine(_root, "missing.xlsx");
        SeatGridConfigIO.Export(path, settings, students,
            new Dictionary<SeatPosition, Student>(), constraints);

        // 把名单里的第二个人删掉，模拟「学生已转学」。
        using (var workbook = new XLWorkbook(path))
        {
            var roster = workbook.Worksheet("名单");
            roster.Row(3).Delete();
            workbook.Save();
        }

        var imported = SeatGridConfigIO.Import(path);

        Assert.Null(imported.Constraints);
        Assert.Contains(imported.Warnings, w => w.Contains("已不在名单里"));
    }

    [Fact]
    public void Config_RoundTripsZones()
    {
        var students = MakeStudents(2);
        var settings = new SeatChartSettings { Groups = 2, Rows = 3, Columns = 2 };
        var layout = settings.GetLayout();
        var constraints = new ArrangeConstraints
        {
            Zones = new Dictionary<Guid, StudentZone>
            {
                [students[0].Id] = new StudentZone(
                    new[] { SeatRect.RowBand(layout, 0, 1) },
                    new[] { SeatRect.GroupBand(layout, 1) }),
            },
        };

        var path = Path.Combine(_root, "zones.xlsx");
        SeatGridConfigIO.Export(path, settings, students,
            new Dictionary<SeatPosition, Student>(), constraints);
        var imported = SeatGridConfigIO.Import(path);

        Assert.NotNull(imported.Constraints);
        var zone = Assert.Single(imported.Constraints!.Zones).Value;
        Assert.Single(zone.Allowed);
        Assert.Single(zone.Denied);
        Assert.True(zone.Allows(new SeatPosition(0, 0, 0)));
        Assert.False(zone.Allows(new SeatPosition(0, 2, 0)));
    }

    [Fact]
    public void Config_MissingConstraintSheetReadsNullWithoutWarning()
    {
        var students = MakeStudents(2);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };

        var path = Path.Combine(_root, "plain.xlsx");
        SeatGridConfigIO.Export(path, settings, students, new Dictionary<SeatPosition, Student>());
        var imported = SeatGridConfigIO.Import(path);

        Assert.Null(imported.Constraints);
        Assert.Empty(imported.Warnings);
    }

    [Fact]
    public void SeatChartExcel_RoundTripsLocksAndHidesConstraintSheet()
    {
        var (settings, students, assignments) = ChartFixture();
        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[0].Id] = new SeatPosition(0, 0, 0) },
        };

        var path = Path.Combine(_root, "chart-locks.xlsx");
        SeatChartExcelExporter.ExportSeatChart(
            SeatChartComposer.Compose(settings, assignments, students.Count),
            assignments, students, path, constraints);

        using (var workbook = new XLWorkbook(path))
        {
            Assert.True(workbook.Worksheets.Contains("座位约束"));
            Assert.Equal(XLWorksheetVisibility.Hidden, workbook.Worksheet("座位约束").Visibility);
        }

        var imported = SeatGridConfigIO.Import(path);
        Assert.NotNull(imported.Constraints);
        Assert.Single(imported.Constraints!.Pins);
    }

    // ----------------------------- 1.26.0 教室画布 -----------------------------

    [Fact]
    public void Layout_DisabledSeatsAreSkippedByFillOrder()
    {
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 0), true)
            .WithDisabled(new SeatPosition(1, 1, 1), true);
        var layout = new SeatChartLayout(2, 2, 2, canvas);

        var seats = layout.FillOrder().ToList();

        Assert.Equal(6, seats.Count);
        Assert.DoesNotContain(new SeatPosition(0, 0, 0), seats);
        Assert.DoesNotContain(new SeatPosition(1, 1, 1), seats);
        // 顺序仍是 row → group → column。
        Assert.Equal(new SeatPosition(0, 0, 1), seats[0]);
    }

    [Fact]
    public void Layout_UsableSeatCountExcludesAisles()
    {
        var canvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 0, 0), true)
            .WithDisabled(new SeatPosition(0, 0, 1), true)
            .WithDisabled(new SeatPosition(1, 1, 0), true);
        var layout = new SeatChartLayout(2, 2, 2, canvas);

        Assert.Equal(8, layout.CellCount);
        Assert.Equal(5, layout.UsableSeatCount);
        Assert.Equal(5, layout.SeatCount);
    }

    [Fact]
    public void Layout_AllSeatsStillReturnsFullRectangle()
    {
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 0), true);
        var layout = new SeatChartLayout(2, 2, 2, canvas);

        Assert.Equal(8, layout.AllSeats().Count());
        Assert.Equal(7, layout.AllUsableSeats().Count());
    }

    [Fact]
    public void SeatingEngine_ArrangeSkipsDisabledSeats()
    {
        var students = MakeStudents(10);
        var canvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 0, 0), true)
            .WithDisabled(new SeatPosition(1, 0, 0), true);
        var layout = new SeatChartLayout(2, 2, 2, canvas);   // 8 格 - 2 过道 = 6 座

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder);

        Assert.Equal(6, result.Count);
        Assert.DoesNotContain(new SeatPosition(0, 0, 0), result.Keys);
        Assert.DoesNotContain(new SeatPosition(1, 0, 0), result.Keys);
    }

    [Fact]
    public void Composer_MarksDisabledSeatsAsAisle()
    {
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 0), true);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2, Canvas = canvas };
        var document = SeatChartComposer.Compose(settings, new Dictionary<SeatPosition, Student>(), 0);

        var aisle = document.Groups[0].Rows.SelectMany(r => r.Seats)
            .Single(s => s.Position == new SeatPosition(0, 0, 0));
        Assert.Equal(SeatCellKind.Aisle, aisle.Kind);
        Assert.True(aisle.IsEmpty);

        // 矩形形状不变：每组仍是 Columns 个座位。
        Assert.All(document.Groups, g => Assert.All(g.Rows, r => Assert.Equal(2, r.Seats.Count)));
    }

    [Fact]
    public void Canvas_NormalizeDropsOutOfBoundsAisles()
    {
        var canvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 0, 0), true)
            .WithDisabled(new SeatPosition(7, 0, 0), true);

        var normalized = canvas.Normalize(new SeatChartLayout(3, 2, 2));

        Assert.Single(normalized.DisabledSeats);
        Assert.Contains(normalized.DisabledSeats, s => s == new SeatPosition(0, 0, 0));
    }

    [Fact]
    public void Canvas_StructuralEquality()
    {
        var a = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 0, 0), true)
            .WithDisabled(new SeatPosition(1, 1, 1), true);
        var b = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(1, 1, 1), true)
            .WithDisabled(new SeatPosition(0, 0, 0), true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Settings_CloneDoesNotAliasCanvas()
    {
        var settings = new SeatChartSettings();
        var clone = settings.Clone();

        // 浅拷贝共享同一个不可变画布引用（安全）；给原对象换新画布后，克隆体仍指向旧画布。
        Assert.Same(settings.Canvas, clone.Canvas);

        settings.Canvas = settings.Canvas.Toggle(new SeatPosition(0, 0, 0));

        Assert.True(clone.Canvas.IsEmpty);
        Assert.False(settings.Canvas.IsEmpty);
    }

    [Fact]
    public void Config_RoundTripsCanvas()
    {
        var students = MakeStudents(2);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };
        settings.Canvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 0, 1), true)
            .WithGroupCorridor(0, 0.5f);

        var path = Path.Combine(_root, "canvas.xlsx");
        SeatGridConfigIO.Export(path, settings, students, new Dictionary<SeatPosition, Student>());
        var imported = SeatGridConfigIO.Import(path);

        Assert.Contains(imported.Settings.Canvas.DisabledSeats, s => s == new SeatPosition(0, 0, 1));
        Assert.Equal(0.5f, imported.Settings.Canvas.GroupCorridor(0));
    }

    [Fact]
    public void Config_MissingCanvasKeyReadsEmpty()
    {
        var students = MakeStudents(2);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };

        var path = Path.Combine(_root, "no-canvas.xlsx");
        SeatGridConfigIO.Export(path, settings, students, new Dictionary<SeatPosition, Student>());
        var imported = SeatGridConfigIO.Import(path);

        Assert.True(imported.Settings.Canvas.IsEmpty);
        Assert.Empty(imported.Warnings);
    }

    [Fact]
    public void Geometry_DefaultOffsetsMatchLegacySpacing()
    {
        var settings = new SeatChartSettings { Groups = 1, Rows = 2, Columns = 2, CellScalePercent = 100 };
        var layout = settings.GetLayout();

        var offsets = SeatChartGeometry.Measure(layout, settings, ViewMode.Teacher);

        // Cell = 80，列距 = max(3, 0.07*80) = 5.6 → 第一列到第二列 85.6。
        Assert.Equal(85.6f, offsets.Column[1] - offsets.Column[0], 1);
        // 组宽 = 80 + 5.6 + 80 = 165.6。
        Assert.Equal(165.6f, offsets.GroupWidth, 1);
        // 排距同为 5.6。
        Assert.Equal(85.6f, offsets.Row[1] - offsets.Row[0], 1);
    }

    [Fact]
    public void Geometry_GroupCorridorWidensOnlyThatBoundary()
    {
        var settings = new SeatChartSettings { Groups = 3, Rows = 1, Columns = 1, CellScalePercent = 100 };
        settings.Canvas = SeatChartCanvas.Empty.WithGroupCorridor(1, 0.5f);
        var layout = settings.GetLayout();

        var offsets = SeatChartGeometry.Measure(layout, settings, ViewMode.Teacher);

        var first = offsets.Group[1] - offsets.Group[0];
        var second = offsets.Group[2] - offsets.Group[1];
        Assert.True(second > first, $"第 2 条组距 {second} 应大于第 1 条 {first}");
    }

    [Fact]
    public void Geometry_RowOffsetsFollowDisplayOrderInStudentView()
    {
        var settings = new SeatChartSettings { Groups = 1, Rows = 3, Columns = 1, CellScalePercent = 100 };
        settings.Canvas = SeatChartCanvas.Empty.WithRowCorridor(0, 0.5f);
        var layout = settings.GetLayout();

        var teacher = SeatChartGeometry.Measure(layout, settings, ViewMode.Teacher);
        var student = SeatChartGeometry.Measure(layout, settings, ViewMode.Student);

        // 物理第 1 排（Row=0）之后有 0.5 格过道。
        // 教师视角讲台在下：显示序是倒的（物理第 3 排 → 第 1 排），所以过道落在最后一段。
        Assert.True(teacher.Row[3] - teacher.Row[2] > teacher.Row[1] - teacher.Row[0]);
        // 学生视角讲台在上：显示序 = 物理序，过道落在第一段。
        Assert.True(student.Row[1] - student.Row[0] > student.Row[3] - student.Row[2]);
    }

    [Fact]
    public void Geometry_ZeroCorridorKeepsGridWidthUnchanged()
    {
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2, CellScalePercent = 100 };
        var layout = settings.GetLayout();

        var offsets = SeatChartGeometry.Measure(layout, settings, ViewMode.Teacher);

        // 两组各 165.6 宽 + 组距 max(16, 0.28*80=22.4) = 22.4 → 353.6。
        Assert.Equal(353.6f, offsets.GridWidth, 1);
    }

    [Fact]
    public void Renderer_PngWidthGrowsWithGroupCorridor()
    {
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2, CellScalePercent = 100 };
        var document = SeatChartComposer.Compose(settings, new Dictionary<SeatPosition, Student>(), 0);

        var plain = Path.Combine(_root, "plain.png");
        SeatChartRenderer.RenderPng(document, plain, 1.0);
        using var plainCodec = SKCodec.Create(plain);

        settings.Canvas = SeatChartCanvas.Empty.WithGroupCorridor(0, 0.8f);
        var wide = SeatChartComposer.Compose(settings, new Dictionary<SeatPosition, Student>(), 0);
        var widePath = Path.Combine(_root, "wide.png");
        SeatChartRenderer.RenderPng(wide, widePath, 1.0);
        using var wideCodec = SKCodec.Create(widePath);

        Assert.True(wideCodec!.Info.Width > plainCodec!.Info.Width);
    }

    [Fact]
    public void Renderer_AislePixelsStayWhite()
    {
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 0), true);
        var settings = new SeatChartSettings
        {
            Groups = 1, Rows = 1, Columns = 2, CellScalePercent = 100, Canvas = canvas,
            ShowGroupRowHints = false, Title = "t",
        };
        var students = MakeStudents(1);
        var assignments = new Dictionary<SeatPosition, Student> { [new SeatPosition(0, 0, 1)] = students[0] };
        var document = SeatChartComposer.Compose(settings, assignments, 1);

        var path = Path.Combine(_root, "aisle.png");
        SeatChartRenderer.RenderPng(document, path, 1.0);

        using var bitmap = SKBitmap.Decode(path);
        Assert.NotNull(bitmap);

        // 左侧过道格的中心应当接近纯白（不画底色、不画边框）。
        var offsets = SeatChartGeometry.Measure(document.Layout, settings, ViewMode.Teacher);
        var x = (int)(28 + 44 + offsets.Column[0] + offsets.Cell / 2);
        var y = (int)(28 + 40 + 24 + offsets.Row[0] + offsets.Cell / 2);
        var pixel = bitmap!.GetPixel(x, y);
        Assert.True(pixel.Red > 250 && pixel.Green > 250 && pixel.Blue > 250,
            $"过道格中心像素为 {pixel}，应为白色");
    }

    [Fact]
    public void Excel_NoCorridorKeepsLegacyColumnLayout()
    {
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2, ShowGroupRowHints = true };
        var students = MakeStudents(2);
        var assignments = new Dictionary<SeatPosition, Student> { [new SeatPosition(0, 0, 0)] = students[0] };
        var document = SeatChartComposer.Compose(settings, assignments, students.Count);

        var path = Path.Combine(_root, "no-corridor.xlsx");
        SeatChartExcelExporter.ExportSeatChart(document, assignments, students, path);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("座位表");

        // 不设过道时，列布局必须与旧版一致：座位紧邻排号列，组间只有 1 列空档。
        Assert.Contains("同学1", sheet.CellsUsed().Select(c => c.GetString()));
        Assert.Contains("第1组", sheet.CellsUsed().Select(c => c.GetString()));

        var seatCell = sheet.CellsUsed().First(c => c.GetString() == "同学1");
        Assert.Equal(2, seatCell.Address.ColumnNumber);   // 排号列(1) → 第 1 组第 1 列 = 2
    }

    [Fact]
    public void Excel_AisleCellHasNoFillOrBorder()
    {
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 0), true);
        var settings = new SeatChartSettings
        {
            Groups = 1, Rows = 1, Columns = 2, ShowGroupRowHints = true, Canvas = canvas,
        };
        var students = MakeStudents(1);
        var assignments = new Dictionary<SeatPosition, Student> { [new SeatPosition(0, 0, 1)] = students[0] };
        var document = SeatChartComposer.Compose(settings, assignments, 1);

        var path = Path.Combine(_root, "aisle.xlsx");
        SeatChartExcelExporter.ExportSeatChart(document, assignments, students, path);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("座位表");
        var aisleCell = sheet.Cell(5, 3);

        Assert.Equal(string.Empty, aisleCell.GetString());

        // ClosedXML 的 OutsideBorder 只有 setter，读不回来；用「无底色」证明过道格没被画成座位。
        Assert.NotEqual(XLColor.FromHtml("#FFFFFF"), aisleCell.Style.Fill.BackgroundColor);
    }

    [Fact]
    public void Config_RoundTripsDoors()
    {
        var students = MakeStudents(2);
        var settings = new SeatChartSettings { Groups = 2, Rows = 2, Columns = 2 };
        settings.Canvas = SeatChartCanvas.Empty.WithDoors(System.Collections.Immutable.ImmutableArray.Create(
            new RoomDoor(RoomEdge.Front, 0.15, "前门"),
            new RoomDoor(RoomEdge.Back, 0.85, "后门")));

        var path = Path.Combine(_root, "doors.xlsx");
        SeatGridConfigIO.Export(path, settings, students, new Dictionary<SeatPosition, Student>());
        var imported = SeatGridConfigIO.Import(path);

        Assert.Equal(2, imported.Settings.Canvas.Doors.Length);
        Assert.Contains(imported.Settings.Canvas.Doors, d => d.Label == "前门" && d.Edge == RoomEdge.Front);
        Assert.Contains(imported.Settings.Canvas.Doors, d => d.Label == "后门" && d.Edge == RoomEdge.Back);
    }

    [Fact]
    public void Canvas_NormalizeClampsDoorRatio()
    {
        var canvas = SeatChartCanvas.Empty.WithDoors(System.Collections.Immutable.ImmutableArray.Create(
            new RoomDoor(RoomEdge.Front, 1.4, "前门")));

        var normalized = canvas.Normalize(new SeatChartLayout(2, 2, 2));

        Assert.Equal(1.0, normalized.Doors[0].OffsetRatio);
    }

    [Fact]
    public void Renderer_PngHeightGrowsWithPodiumHeight()
    {
        var settings = new SeatChartSettings { Groups = 1, Rows = 1, Columns = 1, PodiumHeight = 40 };
        var document = SeatChartComposer.Compose(settings, new Dictionary<SeatPosition, Student>(), 0);
        var tall = SeatChartComposer.Compose(
            new SeatChartSettings { Groups = 1, Rows = 1, Columns = 1, PodiumHeight = 80 },
            new Dictionary<SeatPosition, Student>(), 0);

        var a = Path.Combine(_root, "p40.png");
        var b = Path.Combine(_root, "p80.png");
        SeatChartRenderer.RenderPng(document, a, 1.0);
        SeatChartRenderer.RenderPng(tall, b, 1.0);

        using var codecA = SKCodec.Create(a);
        using var codecB = SKCodec.Create(b);
        Assert.True(codecB!.Info.Height > codecA!.Info.Height);
    }

    [Fact]
    public void Excel_PodiumLabelIsConfigurable()
    {
        var settings = new SeatChartSettings
        {
            Groups = 1, Rows = 1, Columns = 1, PodiumLabel = "讲台 / 黑板", ShowGroupRowHints = false,
        };
        var students = MakeStudents(1);
        var assignments = new Dictionary<SeatPosition, Student> { [new SeatPosition(0, 0, 0)] = students[0] };
        var document = SeatChartComposer.Compose(settings, assignments, 1);

        var path = Path.Combine(_root, "podium-label.xlsx");
        SeatChartExcelExporter.ExportSeatChart(document, assignments, students, path);

        using var workbook = new XLWorkbook(path);
        Assert.Contains("讲台 / 黑板",
            workbook.Worksheet("座位表").CellsUsed().Select(c => c.GetString()));
    }

    [Fact]
    public void Settings_PodiumLabelFallsBackWhenBlank()
    {
        var settings = new SeatChartSettings { PodiumLabel = "   " };
        settings.Normalize();

        Assert.Equal("讲　台", settings.PodiumLabel);
    }

    // ----------------------------- 1.27.0 考务套件 -----------------------------

    [Fact]
    public void ExamPlanner_ColumnsFollowSquareRootRule()
    {
        var thirty = ExamPlanner.Build(MakeStudents(30));
        Assert.Equal(6, thirty.Layout.Columns);
        Assert.Equal(5, thirty.Layout.Rows);

        var sixtyFour = ExamPlanner.Build(MakeStudents(64));
        Assert.Equal(8, sixtyFour.Layout.Columns);
        Assert.Equal(8, sixtyFour.Layout.Rows);
    }

    [Fact]
    public void ExamPlanner_SerpentineNumbersSnakePerRow()
    {
        var layout = new SeatChartLayout(1, 2, 3);
        var numbers = ExamPlanner.NumberSeats(layout, serpentine: true);

        Assert.Equal(1, numbers[new SeatPosition(0, 0, 0)]);
        Assert.Equal(2, numbers[new SeatPosition(0, 0, 1)]);
        Assert.Equal(3, numbers[new SeatPosition(0, 0, 2)]);
        Assert.Equal(6, numbers[new SeatPosition(0, 1, 0)]);
        Assert.Equal(5, numbers[new SeatPosition(0, 1, 1)]);
        Assert.Equal(4, numbers[new SeatPosition(0, 1, 2)]);
    }

    [Fact]
    public void ExamPlanner_ColumnOrderNumbersStraightDown()
    {
        var layout = new SeatChartLayout(1, 2, 3);
        var numbers = ExamPlanner.NumberSeats(layout, serpentine: false);

        // 按列从上到下：第 1 列 1、2；第 2 列 3、4；第 3 列 5、6。
        Assert.Equal(1, numbers[new SeatPosition(0, 0, 0)]);
        Assert.Equal(2, numbers[new SeatPosition(0, 1, 0)]);
        Assert.Equal(3, numbers[new SeatPosition(0, 0, 1)]);
        Assert.Equal(4, numbers[new SeatPosition(0, 1, 1)]);
        Assert.Equal(5, numbers[new SeatPosition(0, 0, 2)]);
        Assert.Equal(6, numbers[new SeatPosition(0, 1, 2)]);
    }

    [Fact]
    public void ExamPlanner_SeatNumbersAreContiguousAndUnique()
    {
        var plan = ExamPlanner.Build(MakeStudents(37));

        // 37 人 → 7 列 × 6 排 = 42 座，座号连续覆盖全部座位。
        var values = plan.SeatNumbers.Values.OrderBy(v => v).ToList();
        Assert.Equal(Enumerable.Range(1, plan.Layout.SeatCount), values);
        Assert.Equal(37, plan.Assignments.Count);
    }

    [Fact]
    public void ExamPlanner_DailySeatOrderPreservedInExam()
    {
        var students = MakeStudents(6);
        var layout = new SeatChartLayout(2, 3, 1);
        var daily = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[4],   // 日常第一排最左
            [new SeatPosition(0, 0, 1)] = students[2],
        };

        var plan = ExamPlanner.FromAssignments(students, daily);
        var firstSeat = plan.SeatNumbers.First(kv => kv.Value == 1).Key;

        Assert.Equal(students[4].Name, plan.Assignments[firstSeat].Name);
    }

    [Fact]
    public void ExamPlanner_EmptyRosterProducesEmptyPlan()
    {
        var plan = ExamPlanner.Build(Array.Empty<Student>());

        Assert.Empty(plan.Assignments);
        Assert.Contains(plan.Notes, n => n.Contains("名单为空"));
    }

    [Fact]
    public void RotateGroups_ShiftsEveryGroupByOneAndWraps()
    {
        var students = MakeStudents(4);
        var layout = new SeatChartLayout(4, 1, 1);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
            [new SeatPosition(3, 0, 0)] = students[3],
        };

        var rotated = SeatChartComposer.RotateGroups(seats, layout, 1);

        Assert.Equal(students[0].Name, rotated[new SeatPosition(1, 0, 0)].Name);
        Assert.Equal(students[3].Name, rotated[new SeatPosition(0, 0, 0)].Name);
    }

    [Fact]
    public void RotateGroups_KeepsRowAndColumn()
    {
        var students = MakeStudents(1);
        var layout = new SeatChartLayout(3, 2, 2);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 1, 1)] = students[0],
        };

        var rotated = SeatChartComposer.RotateGroups(seats, layout, 1);

        var position = rotated.Single().Key;
        Assert.Equal(1, position.Row);
        Assert.Equal(1, position.Column);
        Assert.Equal(1, position.Group);
    }

    [Fact]
    public void RotateGroups_SingleGroupIsNoOp()
    {
        var students = MakeStudents(1);
        var layout = new SeatChartLayout(1, 2, 2);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
        };

        var rotated = SeatChartComposer.RotateGroups(seats, layout, 1);

        Assert.Equal(new SeatPosition(0, 0, 0), rotated.Single().Key);
    }

    [Fact]
    public void RotateGroups_KeepsStudentInPlaceWhenTargetIsAisle()
    {
        var students = MakeStudents(1);
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(1, 0, 0), true);
        var layout = new SeatChartLayout(2, 1, 1, canvas);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
        };

        var rotated = SeatChartComposer.RotateGroups(seats, layout, 1);

        Assert.Equal(new SeatPosition(0, 0, 0), rotated.Single().Key);
    }

    [Fact]
    public void DeskPlan_SameSeatCountNeedsNoMove()
    {
        var daily = new SeatChartLayout(4, 6, 2);
        var exam = new SeatChartLayout(1, 6, 8);

        var plan = DeskMovingPlanCalculator.Build(daily, exam, dailyIsDoubleDesk: true);

        Assert.Equal(0, plan.MoveOutDesks);
        Assert.Equal(0, plan.MoveInDesks);
        Assert.Equal(48, plan.KeepDesks);
    }

    [Fact]
    public void DeskPlan_ExamSmallerMovesOut()
    {
        var daily = new SeatChartLayout(4, 6, 2);   // 48
        var exam = new SeatChartLayout(1, 5, 8);    // 40

        var plan = DeskMovingPlanCalculator.Build(daily, exam, dailyIsDoubleDesk: false);

        Assert.Equal(8, plan.MoveOutDesks);
        Assert.Equal(40, plan.KeepDesks);
    }

    [Fact]
    public void DeskPlan_ExamLargerMovesIn()
    {
        var daily = new SeatChartLayout(4, 6, 2);   // 48
        var exam = new SeatChartLayout(1, 7, 8);    // 56

        var plan = DeskMovingPlanCalculator.Build(daily, exam, dailyIsDoubleDesk: false);

        Assert.Equal(8, plan.MoveInDesks);
    }

    [Fact]
    public void Sticker_PageCountIsCeilOfPerPage()
    {
        var options = new DeskStickerOptions { Columns = 3, Rows = 4 };   // 12/页

        Assert.Equal(1, DeskStickerRenderer.PageCount(12, options));
        Assert.Equal(2, DeskStickerRenderer.PageCount(13, options));
        Assert.Equal(3, DeskStickerRenderer.PageCount(30, options));
    }

    [Fact]
    public void Sticker_RenderProducesPngWithExpectedSize()
    {
        var document = new DeskStickerDocument("考场桌贴", new[]
        {
            new DeskSticker("同学1", "2026001", 1, "九年级6班", "第 1 考场"),
        }, string.Empty);
        var options = new DeskStickerOptions();

        var png = DeskStickerRenderer.Render(document, options, 0, 1.0f);

        using var codec = SKCodec.Create(new MemoryStream(png));
        Assert.NotNull(codec);
        Assert.Equal((int)DeskStickerRenderer.PageWidth, codec!.Info.Width);
        Assert.Equal((int)DeskStickerRenderer.PageHeight, codec.Info.Height);
    }

    [Fact]
    public void StickerPdf_ExportsValidMultiPagePdf()
    {
        var students = MakeStudents(30);
        var document = new DeskStickerDocument("考场桌贴",
            students.Select((s, i) => new DeskSticker(s.Name, s.Number, i + 1, "九年级6班", "第 1 考场")).ToList(),
            string.Empty);
        var options = new DeskStickerOptions { Columns = 3, Rows = 4 };

        var path = Path.Combine(_root, "stickers.pdf");
        DeskStickerPdfExporter.Export(document, options, path);

        Assert.True(File.Exists(path));
        Assert.Equal("%PDF", File.ReadAllText(path, System.Text.Encoding.ASCII)[..4]);
        Assert.True(new FileInfo(path).Length > 5000);
    }

    [Fact]
    public void StickerPdf_EmptyRosterThrows()
    {
        var document = new DeskStickerDocument("考场桌贴", Array.Empty<DeskSticker>(), string.Empty);

        Assert.Throws<SeatGridException>(() =>
            DeskStickerPdfExporter.Export(document, new DeskStickerOptions(), Path.Combine(_root, "x.pdf")));
    }

    // ----------------------------- 1.28.0 空间变换与还原 -----------------------------

    [Fact]
    public void Layout_AppendRowKeepsAssignmentsIntact()
    {
        var layout = new SeatChartLayout(2, 2, 2);
        var appended = layout.AppendRow();

        Assert.Equal(3, appended.Rows);
        Assert.Equal(2, appended.Groups);
        Assert.Equal(2, appended.Columns);
        Assert.Equal(layout.CellCount + layout.Groups * layout.Columns, appended.CellCount);
    }

    [Fact]
    public void Layout_RemoveLastRowReportsRemovedPositions()
    {
        var layout = new SeatChartLayout(2, 3, 2);
        var (shrunk, removed) = layout.RemoveLastRow();

        Assert.Equal(2, shrunk.Rows);
        Assert.Equal(4, removed.Count);   // 2 组 × 2 列
        Assert.All(removed, p => Assert.Equal(2, p.Row));
    }

    [Fact]
    public void Layout_RemoveLastRowKeepsEarlierRows()
    {
        var layout = new SeatChartLayout(2, 3, 2);
        var (shrunk, _) = layout.RemoveLastRow();

        foreach (var seat in shrunk.AllSeats())
            Assert.True(seat.Row <= 1);
    }

    [Fact]
    public void Layout_AppendColumnDoesNotShiftExistingColumns()
    {
        var layout = new SeatChartLayout(1, 1, 2);
        var appended = layout.AppendColumn();

        // 已有列的位置与含义完全不变——追加绝不把已有列平移（那会改掉所有同桌关系）。
        Assert.Equal(3, appended.Columns);
        Assert.True(appended.Contains(new SeatPosition(0, 0, 0)));
        Assert.True(appended.Contains(new SeatPosition(0, 0, 1)));
        Assert.True(appended.Contains(new SeatPosition(0, 0, 2)));
    }

    [Fact]
    public void Layout_RemoveLastCannotShrinkBelowOne()
    {
        var layout = new SeatChartLayout(1, 1, 1);

        Assert.Empty(layout.RemoveLastRow().Removed);
        Assert.Empty(layout.RemoveLastColumn().Removed);
        Assert.Empty(layout.RemoveLastGroup().Removed);
    }

    [Fact]
    public void Composer_MirrorColumnsSwapsWithinGroupOnly()
    {
        var students = MakeStudents(2);
        var layout = new SeatChartLayout(1, 1, 2);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
            [new SeatPosition(0, 0, 1)] = students[1],
        };

        var mirrored = SeatChartComposer.MirrorColumns(seats, layout, out var blocked);

        Assert.Equal(0, blocked);
        Assert.Equal(students[0].Name, mirrored[new SeatPosition(0, 0, 1)].Name);
        Assert.Equal(students[1].Name, mirrored[new SeatPosition(0, 0, 0)].Name);
    }

    [Fact]
    public void Composer_MirrorGroupsSwapsOutermostGroups()
    {
        var students = MakeStudents(2);
        var layout = new SeatChartLayout(4, 1, 1);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
            [new SeatPosition(3, 0, 0)] = students[1],
        };

        var mirrored = SeatChartComposer.MirrorGroups(seats, layout, out var blocked);

        Assert.Equal(0, blocked);
        Assert.Equal(students[0].Name, mirrored[new SeatPosition(3, 0, 0)].Name);
        Assert.Equal(students[1].Name, mirrored[new SeatPosition(0, 0, 0)].Name);
    }

    [Fact]
    public void Composer_MirrorSkipsAislesAndReports()
    {
        var students = MakeStudents(1);
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 1), true);
        var layout = new SeatChartLayout(1, 1, 2, canvas);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
        };

        var mirrored = SeatChartComposer.MirrorColumns(seats, layout, out var blocked);

        Assert.Equal(1, blocked);
        Assert.Equal(new SeatPosition(0, 0, 0), mirrored.Single().Key);
    }

    [Fact]
    public void ShiftOnScreen_LeavesStudentInPlaceWhenTargetIsAisle()
    {
        var students = MakeStudents(1);
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 1), true);
        var layout = new SeatChartLayout(1, 1, 3, canvas);
        var seats = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
        };

        var shifted = SeatChartComposer.ShiftOnScreen(
            seats, layout, ViewMode.Teacher, 1, 0, out var blocked);

        Assert.Equal(1, blocked);
        Assert.Equal(new SeatPosition(0, 0, 0), shifted.Single().Key);
    }

    [Fact]
    public void Presets_UShapeDisablesCenterFrontSeats()
    {
        var layout = new SeatChartLayout(4, 6, 2);
        var canvas = SeatCanvasPresets.UShape(layout);

        Assert.NotNull(canvas);
        // 中间两组靠讲台的前 3 排成为过道。
        Assert.True(canvas!.IsDisabled(new SeatPosition(1, 0, 0)));
        Assert.True(canvas.IsDisabled(new SeatPosition(2, 2, 1)));
        // 两侧组一格未动。
        Assert.False(canvas.IsDisabled(new SeatPosition(0, 0, 0)));
        Assert.False(canvas.IsDisabled(new SeatPosition(3, 0, 0)));
    }

    [Fact]
    public void Presets_UShapeWidensCenterCorridor()
    {
        var layout = new SeatChartLayout(4, 6, 2);
        var canvas = SeatCanvasPresets.UShape(layout);

        Assert.NotNull(canvas);
        Assert.True(canvas!.GroupCorridor(1) > 0);
    }

    [Fact]
    public void Presets_IslandsGroupsSeatsIntoBlocks()
    {
        var layout = new SeatChartLayout(4, 2, 2);
        var canvas = SeatCanvasPresets.Islands(layout, islandGroups: 2);

        Assert.NotNull(canvas);
        Assert.True(canvas!.GroupCorridor(1) >= 1.0f);
        Assert.Equal(0, canvas.GroupCorridor(0));
    }

    [Fact]
    public void Presets_StaggeredRowsShortensEvenRows()
    {
        var layout = new SeatChartLayout(1, 4, 2);
        var canvas = SeatCanvasPresets.StaggeredRows(layout);

        Assert.NotNull(canvas);
        // 第 2 排（索引 1）末位是过道；第 1 排完整。
        Assert.True(canvas!.IsDisabled(new SeatPosition(0, 1, 1)));
        Assert.False(canvas.IsDisabled(new SeatPosition(0, 0, 1)));
    }

    [Fact]
    public void Presets_ReturnNullWhenLayoutTooSmall()
    {
        Assert.Null(SeatCanvasPresets.UShape(new SeatChartLayout(2, 4, 2)));
        Assert.Null(SeatCanvasPresets.Islands(new SeatChartLayout(2, 4, 2)));
        Assert.Null(SeatCanvasPresets.StaggeredRows(new SeatChartLayout(2, 4, 1)));
        Assert.Null(SeatCanvasPresets.FrontFilled(new SeatChartLayout(2, 2, 2)));
    }

    [Fact]
    public void Presets_OutputIsOrdinaryCanvasAndRoundTrips()
    {
        var layout = new SeatChartLayout(4, 6, 2);
        var canvas = SeatCanvasPresets.UShape(layout);
        Assert.NotNull(canvas);

        var settings = new SeatChartSettings { Groups = 4, Rows = 6, Columns = 2, Canvas = canvas! };
        var students = MakeStudents(2);
        var path = Path.Combine(_root, "ushape.xlsx");
        SeatGridConfigIO.Export(path, settings, students, new Dictionary<SeatPosition, Student>());

        var imported = SeatGridConfigIO.Import(path);

        // 往返后走廊数组会被补零到布局长度（Normalize 的正常归一化），按逐边界取值比较。
        Assert.Equal(canvas!.DisabledSeats.Count, imported.Settings.Canvas.DisabledSeats.Count);
        Assert.Equal(canvas.GroupCorridor(1), imported.Settings.Canvas.GroupCorridor(1));
        Assert.Equal(canvas.GroupCorridor(0), imported.Settings.Canvas.GroupCorridor(0));
    }

    [Fact]
    public void Geometry_TieredRowsIncreaseDepthMonotonically()
    {
        var settings = new SeatChartSettings
        {
            Groups = 1, Rows = 4, Columns = 1, CellScalePercent = 100, TierRowGrowthPercent = 20,
        };
        var layout = settings.GetLayout();

        var offsets = SeatChartGeometry.Measure(layout, settings, ViewMode.Student);

        var first = offsets.Row[1] - offsets.Row[0];
        var second = offsets.Row[2] - offsets.Row[1];
        var third = offsets.Row[3] - offsets.Row[2];
        Assert.True(second > first, $"{second} 应大于 {first}");
        Assert.True(third > second, $"{third} 应大于 {second}");
    }

    [Fact]
    public void Geometry_TieredRowsKeepFrontRowAtOrigin()
    {
        var settings = new SeatChartSettings
        {
            Groups = 1, Rows = 4, Columns = 1, TierRowGrowthPercent = 20,
        };
        var offsets = SeatChartGeometry.Measure(settings.GetLayout(), settings, ViewMode.Student);

        Assert.Equal(0f, offsets.Row[0]);
    }

    [Fact]
    public void Geometry_TierInTeacherViewGrowsTowardBackWall()
    {
        // 教师视角的显示序是倒的（讲台在下）：阶梯必须仍然朝后墙加深，不能反向。
        var settings = new SeatChartSettings
        {
            Groups = 1, Rows = 4, Columns = 1, TierRowGrowthPercent = 20,
        };
        var offsets = SeatChartGeometry.Measure(settings.GetLayout(), settings, ViewMode.Teacher);

        // 显示序第 1 段 = 物理最后排，应最宽。
        Assert.True(offsets.Row[1] - offsets.Row[0] > offsets.Row[3] - offsets.Row[2]);
    }

    [Fact]
    public void SeatingEngine_UShapeStillFillsFrontFirst()
    {
        var students = new List<Student>
        {
            new() { Name = "高个", Height = 190 },
            new() { Name = "最矮", Height = 150 },
        };
        var canvas = SeatCanvasPresets.UShape(new SeatChartLayout(4, 6, 2));
        var layout = new SeatChartLayout(4, 6, 2, canvas);

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.Height);

        // 最矮的仍落在最靠前的那一排（Row = 0）。
        var shortSeat = SeatOf(result, students[1].Id);
        Assert.NotNull(shortSeat);
        Assert.Equal(0, shortSeat!.Value.Row);
    }

    [Fact]
    public void SeatZone_ZoneRestrictionFlowsThroughConstraints()
    {
        var students = MakeStudents(4);
        var layout = new SeatChartLayout(1, 4, 2);
        var constraints = new ArrangeConstraints
        {
            Zones = new Dictionary<Guid, StudentZone>
            {
                [students[3].Id] = new StudentZone(
                    new[] { SeatRect.RowBand(layout, 0, 0) }, Array.Empty<SeatRect>()),
            },
        };

        var result = SeatingEngine.Arrange(students, layout, ArrangeMode.RosterOrder, constraints);
        var seat = SeatOf(result, students[3].Id);

        Assert.NotNull(seat);
        Assert.Equal(0, seat!.Value.Row);
    }

    private static SeatPosition? SeatOf(ArrangeResult result, Guid id)
    {
        foreach (var (position, student) in result.Assignments)
            if (student.Id == id) return position;
        return null;
    }

    private static List<Student> MakeStudents(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Student { Name = $"同学{i + 1}", Gender = i % 2 == 0 ? Gender.Male : Gender.Female })
            .ToList();

    private static SeatPosition? SeatOf(IReadOnlyDictionary<SeatPosition, Student> assignments, Guid id)
    {
        foreach (var (position, student) in assignments)
            if (student.Id == id) return position;
        return null;
    }
}
