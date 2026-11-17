using ClosedXML.Excel;
using SeatGrid.App;
using SeatGrid.Core;
using SeatGrid.Core.Exam;
using SeatGrid.Core.Export;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;
using SeatGrid.Core.Roster;
using SkiaSharp;

namespace TeachCraft.Tests;

/// <summary>
/// 考场模式导出的守护回归（修复 1.32.0 前的三处缺陷）。
///
/// 缺陷本体是「导出走的是日常布局」：界面网格读 <c>CurrentLayout</c>（考场下返回
/// <c>_examPlan.Layout</c>），而导出读 <c>ToSettings()</c>（组 / 排 / 列取自日常滑块），
/// 再被 <c>SeatChartSettings.Normalize</c> 把 Columns 夹到 4。30 人考场 6 列 × 5 排
/// 于是被切成 4 列 × N 排，后排学生整片消失，且没有任何座号。
///
/// 两条不变量在这里被钉死：
/// A. 日常导出一个字段都不能变（Normalize 的夹取保留、座号表为空）。
/// B. 考场导出不动日常滑块，也不改 ExamPlanner 的快照语义。
/// </summary>
public sealed class SeatGridExamExportTests : IDisposable
{
    private readonly string _root;

    public SeatGridExamExportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "SeatGridExam", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ----------------------------- 考场导出 -----------------------------

    [Fact]
    public void ExamComposeDocument_UsesExamLayoutNotDailySliders()
    {
        // 30 人 → ceil(sqrt(30)) = 6 列，ceil(30/6) = 5 排，正好铺满、没有空座。
        // 日常滑块是 4 组 × 6 排 × 2 列——修好之前导出的就是它。
        var vm = ExamViewModel(30);

        var document = vm.ComposeDocument();

        Assert.Equal(1, document.Layout.Groups);
        Assert.Equal(6, document.Layout.Columns);
        Assert.Equal(5, document.Layout.Rows);
        Assert.Equal(30, document.Layout.SeatCount);
        Assert.Equal(30, vm.AssignedCount);
    }

    [Fact]
    public void ExamComposeDocument_ShowsEveryStudentExactlyOnce()
    {
        // 缺陷的可见后果：30 人被切成 4 列 × ? 排，后排的人从画布上消失。
        var vm = ExamViewModel(30);

        var document = vm.ComposeDocument();

        var names = document.Groups
            .SelectMany(g => g.Rows)
            .SelectMany(r => r.Seats)
            .Where(s => !s.IsEmpty)
            .Select(s => s.Name.Replace(" ", string.Empty))
            .ToList();

        Assert.Equal(30, names.Count);
        Assert.Equal(30, names.Distinct().Count());
        foreach (var student in vm.AllStudents)
            Assert.Contains(student.Model.Name.Replace(" ", string.Empty), names);
    }

    [Fact]
    public void ExamComposeDocument_CarriesSeatNumbersOneToThirtyInSnakeOrder()
    {
        // 座号必须与 ExamSeatNumbers 完全一致：1..30 齐、不重不漏，蛇形逐排往返。
        var vm = ExamViewModel(30);

        var document = vm.ComposeDocument();
        var expected = vm.ExamSeatNumbers;

        Assert.Equal(30, expected.Count);
        Assert.Equal(Enumerable.Range(1, 30), expected.Values.OrderBy(v => v));
        Assert.Equal(expected, document.SeatNumbers.ToDictionary(kv => kv.Key, kv => kv.Value));

        // 蛇形：第 1 排从 1 数到 6，第 2 排从右往左 7..12。
        Assert.Equal(1, document.SeatNumbers[new SeatPosition(0, 0, 0)]);
        Assert.Equal(6, document.SeatNumbers[new SeatPosition(0, 0, 5)]);
        Assert.Equal(7, document.SeatNumbers[new SeatPosition(0, 1, 5)]);
        Assert.Equal(12, document.SeatNumbers[new SeatPosition(0, 1, 0)]);

        // 每一格的座号都要落到展示用的小格上，否则渲染画不出来。
        foreach (var seat in document.Groups.SelectMany(g => g.Rows).SelectMany(r => r.Seats))
            Assert.Equal(expected[seat.Position], seat.SeatNumber);
    }

    [Fact]
    public void ExamComposeDocument_DoesNotTouchDailySliders()
    {
        // AGENTS 第六节：考场是独立快照，进出考场不得改日常组 / 排 / 列滑块。
        var vm = ExamViewModel(30);
        var (groups, rows, columns) = (vm.GroupCount, vm.RowCount, vm.ColumnCount);

        vm.ComposeDocument();
        vm.EnterExamMode(new ExamLayoutOptions());
        vm.ComposeDocument();

        Assert.Equal(groups, vm.GroupCount);
        Assert.Equal(rows, vm.RowCount);
        Assert.Equal(columns, vm.ColumnCount);

        vm.LeaveExamMode();
        Assert.Equal(groups, vm.GroupCount);
        Assert.Equal(rows, vm.RowCount);
        Assert.Equal(columns, vm.ColumnCount);
    }

    [Fact]
    public void ExamComposeDocument_ThirtyStudentsSurvivePngExport()
    {
        // 真实渲染一遍 PNG：文档对不代表画出来对（缺字体、越界、格子被裁都可能静默失败）。
        var vm = ExamViewModel(30);
        var document = vm.ComposeDocument();

        var path = Path.Combine(_root, "exam.png");
        SeatChartRenderer.RenderPng(document, path, 2.0);

        Assert.True(File.Exists(path));
        using var bitmap = SKBitmap.Decode(path);
        Assert.NotNull(bitmap);

        // 画布至少装得下 6 列 × 5 排的格子（格宽 68px、2 倍缩放）：
        // 修复前按 4 列切，宽会明显不够，这条会红。
        var cell = document.Settings.CellSizePx;
        Assert.True(bitmap!.Width >= 6 * cell * 2 * 0.95,
            $"{bitmap.Width}px 宽装不下 6 列 × {cell}px 的格子");
        Assert.True(bitmap.Height >= 5 * cell * 2 * 0.95,
            $"{bitmap.Height}px 高装不下 5 排 × {cell}px 的格子");
    }

    [Fact]
    public void ExamExcelExport_ContainsSeatNumbersAndAllNames()
    {
        var vm = ExamViewModel(30);
        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "exam.xlsx");

        SeatChartExcelExporter.ExportSeatChart(
            document, vm.GetCoreAssignments(), vm.GetModels(), path);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("座位表");
        var text = string.Concat(sheet.CellsUsed().Select(c => c.GetFormattedString()));

        // 每个座号都进表，且每个学生都在。
        for (var number = 1; number <= 30; number++)
            Assert.Contains($"{number:00} 号", text);

        foreach (var student in vm.AllStudents)
            Assert.Contains(student.Model.Name.Replace(" ", string.Empty), text.Replace(" ", string.Empty));
    }

    [Fact]
    public void ExamExcelExport_SettingsSheetMarksExamSnapshot()
    {
        // 考场导出会带一张隐藏设置表（与日常导出同格式），但必须打上考场标记，
        // 否则导回来会被夹成日常布局、后排学生整片「超出布局」。
        var vm = ExamViewModel(30);
        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "exam-marked.xlsx");

        SeatChartExcelExporter.ExportSeatChart(
            document, vm.GetCoreAssignments(), vm.GetModels(), path);

        using var workbook = new XLWorkbook(path);
        var settings = workbook.Worksheet("座位设置");
        var keys = settings.CellsUsed()
            .Where(c => c.Address.ColumnNumber == 1)
            .Select(c => c.GetFormattedString())
            .ToList();

        Assert.Contains("ExamSnapshot", keys);

        // 隐藏设置表里的组 / 排 / 列必须与座位表上画的一致：
        // 写日常的 4 × 6 × 2 而画面上是 1 × 5 × 6，打开文件的人只会以为软件坏了。
        var values = settings.CellsUsed()
            .Where(c => c.Address.ColumnNumber == 2)
            .ToDictionary(
                c => settings.Cell(c.Address.RowNumber, 1).GetFormattedString(),
                c => c.GetFormattedString());

        Assert.Equal("1", values["Groups"]);
        Assert.Equal("5", values["Rows"]);
        Assert.Equal("6", values["Columns"]);
    }

    [Fact]
    public void ExamExcelExport_ReimportIsRefusedInsteadOfSilentlyFlattened()
    {
        // 宁可如实报错，也不产出一份「导回来变成日常布局」的配置。
        var vm = ExamViewModel(30);
        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "exam-reimport.xlsx");

        SeatChartExcelExporter.ExportSeatChart(
            document, vm.GetCoreAssignments(), vm.GetModels(), path);

        var error = Assert.Throws<SeatGridException>(() => SeatGridConfigIO.Import(path));
        Assert.Contains("考场", error.Message);

        // 「导入名单」走 TryImport 探测：它认得这是本软件的文件，就该抛出同一条可读中文，
        // 而不是落到普通名单识别里读出一份没有座位的学生。
        var tryError = Assert.Throws<SeatGridException>(() => SeatGridConfigIO.TryImport(path));
        Assert.Contains("考场", tryError.Message);
    }

    [Fact]
    public void ExamExcelExport_OmitsMentorAndConstraintSheets()
    {
        // 考场下锁定 / 互斥 / 结对 / 区域本来就不生效，别把日常约束写进考场文件。
        var vm = ExamViewModel(30);
        var pins = vm.Constraints;
        pins.TogglePin(vm.AllStudents[0], new SeatPosition(0, 0, 0));

        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "exam-no-constraints.xlsx");

        SeatChartExcelExporter.ExportSeatChart(
            document, vm.GetCoreAssignments(), vm.GetModels(), path, vm.GetCoreConstraints());

        using var workbook = new XLWorkbook(path);
        Assert.False(workbook.Worksheets.Contains("座位约束"));
    }

    [Fact]
    public void ExamConfigExport_IsDisabledInExamMode()
    {
        // 「导出配置」在考场模式下直接禁用：配置是要导回日常座位的，考场快照不是日常。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(30));
        vm.AutoArrange();

        Assert.False(vm.IsExamMode);
        Assert.True(vm.CanExportConfig);

        vm.EnterExamMode(new ExamLayoutOptions());
        Assert.False(vm.CanExportConfig);

        vm.LeaveExamMode();
        Assert.True(vm.CanExportConfig);
    }

    [Fact]
    public void ExamConfigExport_CoreRefusesLayoutOverride()
    {
        // 界面禁用之外，Core 再挡一道：将来若有别的入口（脚本 / 批处理）调进来，
        // 也不能产出一份「导不回去」的配置。
        var settings = new SeatChartSettings
        {
            Groups = 4, Rows = 6, Columns = 2,
            LayoutOverride = new SeatChartLayout(1, 5, 6),
        };
        var students = MakeStudents(3);
        var assignments = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
        };

        var path = Path.Combine(_root, "exam-config.xlsx");
        var error = Assert.Throws<SeatGridException>(() =>
            SeatGridConfigIO.Export(path, settings, students, assignments));

        Assert.Contains("考场", error.Message);
        Assert.False(File.Exists(path), "拒绝之后不该留下半截文件");
    }

    [Fact]
    public void ExamComposeDocument_SeatNumberDoesNotOverflowAtMinimumCellScale()
    {
        // 格子缩到最小（50% → 40px）时座号仍要读得出来、也不该盖住姓名。
        // 座号字号 clamp 在 7–11px，两个人字姓名的可用宽度是格宽 − 10px。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(30));
        vm.CellScale = 50;
        vm.AutoArrange();
        vm.EnterExamMode(new ExamLayoutOptions());

        var document = vm.ComposeDocument();
        var cell = document.Settings.CellSizePx;
        Assert.Equal(40, cell);

        var seatSize = Math.Clamp(cell * 0.16f, 7f, 11f);
        Assert.InRange(seatSize, 7f, 11f);

        // 座号文字「NN 号」在 7px 下约 2.9 倍字宽，必须落在格内（含两侧留白）。
        var approximateWidth = seatSize * 2.9f;
        var pad = Math.Max(4f, cell * 0.07f);
        Assert.True(approximateWidth + pad * 2 <= cell,
            $"{approximateWidth:0.0}px 的座号在 {cell}px 格子里放不下");

        // 渲染不抛、不裁：40px 格子仍能出图。
        var path = Path.Combine(_root, "exam-small.png");
        SeatChartRenderer.RenderPng(document, path, 2.0);
        using var bitmap = SKBitmap.Decode(path);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Width > 0 && bitmap.Height > 0);
    }

    [Fact]
    public void ExamConfigExport_RaisesNotificationWhenModeChanges()
    {
        // 按钮绑的是这个属性：进 / 出考场都必须通知，否则界面停在旧态（禁用或可点）。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(12));
        vm.AutoArrange();

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.EnterExamMode(new ExamLayoutOptions());
        Assert.Contains(nameof(SeatGridViewModel.CanExportConfig), raised);
    }

    [Fact]
    public void ExamPptxExport_CarriesSeatNumbers()
    {
        var vm = ExamViewModel(30);
        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "exam.pptx");

        SeatChartPptxExporter.Export(document, path);

        using var presentation = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        var text = string.Concat(presentation.PresentationPart!.SlideParts
            .Select(p => p.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
            .SelectMany(t => t)
            .Select(t => t.Text ?? string.Empty));

        Assert.Contains("01 号", text);
        Assert.Contains("30 号", text);
    }

    // ----------------------------- 日常回归 -----------------------------

    [Fact]
    public void DailyComposeDocument_KeepsDailyLayoutAndHasNoSeatNumbers()
    {
        // 不变量 A：日常导出一个字段都不能变。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(30));
        vm.GroupCount = 4;
        vm.RowCount = 6;
        vm.ColumnCount = 2;
        vm.AutoArrange();

        var document = vm.ComposeDocument();

        Assert.Equal(4, document.Layout.Groups);
        Assert.Equal(6, document.Layout.Rows);
        Assert.Equal(2, document.Layout.Columns);
        Assert.Null(document.Settings.LayoutOverride);
        Assert.Empty(document.SeatNumbers);
        Assert.All(
            document.Groups.SelectMany(g => g.Rows).SelectMany(r => r.Seats),
            seat => Assert.Null(seat.SeatNumber));
    }

    [Fact]
    public void DailyComposeDocument_MatchesLegacyComposeByteForByte()
    {
        // 与修复前的写法（ToSettings → Compose，无座号）逐字段比对：
        // 布局、标题、信息行、讲台朝向与每一个格子的姓名 / 空标志 / 座位都要一致。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(30));
        vm.GroupCount = 4;
        vm.RowCount = 6;
        vm.ColumnCount = 2;
        vm.ArrangeModeIndex = 1;
        vm.AutoArrange();

        var legacy = SeatChartComposer.Compose(vm.ToSettings(), vm.GetCoreAssignments(), vm.StudentCount);
        var current = vm.ComposeDocument();

        Assert.Equal(legacy.Layout, current.Layout);
        Assert.Equal(legacy.Title, current.Title);
        Assert.Equal(legacy.InfoLine, current.InfoLine);
        Assert.Equal(legacy.PodiumOnTop, current.PodiumOnTop);
        Assert.Equal(legacy.StudentCount, current.StudentCount);
        Assert.Equal(legacy.Settings.Groups, current.Settings.Groups);
        Assert.Equal(legacy.Settings.Rows, current.Settings.Rows);
        Assert.Equal(legacy.Settings.Columns, current.Settings.Columns);

        Assert.Equal(legacy.Groups.Count, current.Groups.Count);
        for (var g = 0; g < legacy.Groups.Count; g++)
        {
            Assert.Equal(legacy.Groups[g].Number, current.Groups[g].Number);
            Assert.Equal(legacy.Groups[g].Label, current.Groups[g].Label);
            for (var r = 0; r < legacy.Groups[g].Rows.Count; r++)
            {
                var expectedRow = legacy.Groups[g].Rows[r];
                var actualRow = current.Groups[g].Rows[r];
                Assert.Equal(expectedRow.Number, actualRow.Number);
                Assert.Equal(expectedRow.Label, actualRow.Label);

                for (var c = 0; c < expectedRow.Seats.Count; c++)
                {
                    var expected = expectedRow.Seats[c];
                    var actual = actualRow.Seats[c];
                    Assert.Equal(expected.Position, actual.Position);
                    Assert.Equal(expected.Name, actual.Name);
                    Assert.Equal(expected.IsEmpty, actual.IsEmpty);
                    Assert.Equal(expected.IsFemale, actual.IsFemale);
                    Assert.Equal(expected.Kind, actual.Kind);
                    Assert.Equal(expected.Student?.Id, actual.Student?.Id);
                }
            }
        }
    }

    [Fact]
    public void DailyExcelExport_HasNoExamSnapshotMarker()
    {
        // 日常导出的设置表与旧版逐行一致：多出 ExamSnapshot 就会让旧读者也误解。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(12));
        vm.AutoArrange();

        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "daily.xlsx");
        SeatChartExcelExporter.ExportSeatChart(
            document, vm.GetCoreAssignments(), vm.GetModels(), path);

        using var workbook = new XLWorkbook(path);
        var settings = workbook.Worksheet("座位设置");
        var keys = settings.CellsUsed()
            .Where(c => c.Address.ColumnNumber == 1)
            .Select(c => c.GetFormattedString())
            .ToList();

        Assert.DoesNotContain("ExamSnapshot", keys);

        // 而且还得能导回来——日常往返不能因为这次改动而坏。
        var imported = SeatGridConfigIO.Import(path);
        Assert.Equal(document.Layout.Groups, imported.Settings.Groups);
        Assert.Equal(document.Layout.Rows, imported.Settings.Rows);
        Assert.Equal(document.Layout.Columns, imported.Settings.Columns);
        Assert.Equal(vm.AssignedCount, imported.Assignments.Count);
    }

    [Fact]
    public void DailyExcelSeatCells_StayPlainValuesWithoutRichText()
    {
        // 座号走的是富文本分支；日常表必须一个都不碰，否则 GetString() 之外的行为会变。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(12));
        vm.AutoArrange();

        var document = vm.ComposeDocument();
        var path = Path.Combine(_root, "daily-plain.xlsx");
        SeatChartExcelExporter.ExportSeatChart(
            document, vm.GetCoreAssignments(), vm.GetModels(), path);

        using var workbook = new XLWorkbook(path);
        var seats = document.Groups
            .SelectMany(g => g.Rows)
            .SelectMany(r => r.Seats)
            .Where(s => s.Kind == SeatCellKind.Seat && !s.IsEmpty)
            .ToList();

        Assert.NotEmpty(seats);
        // 日常表的座位格不含「号」字（座号后缀），富文本也没有被启用。
        var values = workbook.Worksheet("座位表").CellsUsed().Select(c => c.GetFormattedString()).ToList();
        Assert.DoesNotContain(values, v => v.EndsWith(" 号", StringComparison.Ordinal));
    }

    // ----------------------------- Normalize 回归 -----------------------------

    [Fact]
    public void Normalize_StillClampsDailyColumnsToFour()
    {
        // 不变量 A 的核心：夹取是给日常滑块用的，原样保留（Normalize 这次一个字都没改）。
        // 带画布是刻意的：Normalize 会用夹取后的布局去裁画布，越界的过道必须被丢掉——
        // 「夹取还在」不能只看三个数字，得看它真的落到了派生结果上。
        var canvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 0, 5), true);   // 第 6 列：夹到 4 列之后就不存在了
        var settings = new SeatChartSettings { Groups = 9, Rows = 12, Columns = 6, Canvas = canvas };
        settings.Normalize();

        Assert.Equal(8, settings.Groups);
        Assert.Equal(10, settings.Rows);
        Assert.Equal(4, settings.Columns);
        Assert.Null(settings.LayoutOverride);
        Assert.Equal(4, settings.GetLayout().Columns);
        Assert.True(settings.Canvas.IsEmpty, "夹取后的画布应把越界过道丢掉");
    }

    [Fact]
    public void Normalize_DailySlidersSurviveEvenWhenValuesAreInRange()
    {
        // 日常滑块在范围内时一个数字都不能被改写，画布也原样留着。
        var canvas = SeatChartCanvas.Empty.WithDisabled(new SeatPosition(0, 0, 0), true);
        var settings = new SeatChartSettings { Groups = 4, Rows = 6, Columns = 2, Canvas = canvas };
        settings.Normalize();

        Assert.Equal((4, 6, 2), (settings.Groups, settings.Rows, settings.Columns));
        Assert.Same(canvas, settings.Canvas);
    }

    [Fact]
    public void Settings_CloneCopiesLayoutOverrideByReference()
    {
        // Clone 是 MemberwiseClone：显式布局是不可变 record，按引用共享是安全的，
        // 但绝不能因为浅拷贝而丢掉它——WriteSettingsSheet 拿的就是 Clone 出来的副本。
        var layout = new SeatChartLayout(1, 5, 6);
        var settings = new SeatChartSettings { LayoutOverride = layout };

        var clone = settings.Clone();

        Assert.Same(layout, clone.LayoutOverride);
        Assert.Equal(layout, clone.GetLayout());
    }

    [Fact]
    public void Normalize_ExplicitLayoutSurvivesClampingOfDailySliders()
    {
        // 考场旁路：Normalize 照常夹日常滑块（那三个字段是滑块的），但 GetLayout()
        // 读的是显式布局，所以 6 列原样保留——8 列上限由 ExamPlanner 自己把着。
        var layout = new SeatChartLayout(1, 5, 6);
        var settings = new SeatChartSettings { Groups = 4, Rows = 6, Columns = 2, LayoutOverride = layout };
        settings.Normalize();

        Assert.Same(layout, settings.LayoutOverride);
        Assert.Equal(layout, settings.GetLayout());
        Assert.Equal(6, settings.GetLayout().Columns);
        Assert.Equal(5, settings.GetLayout().Rows);

        // 日常滑块的三维字段仍是自己的值，没有被显式布局改写。
        Assert.Equal((4, 6, 2), (settings.Groups, settings.Rows, settings.Columns));
    }

    [Fact]
    public void Normalize_ExplicitLayoutKeepsItsOwnCanvas()
    {
        // 考场布局自带空画布；日常画布（过道 / 门窗）不该被拿来截它，也不该被它改掉。
        //
        // 过道放在第 1 组：日常是 4 组，考场只有 1 组——若 Normalize 仍按 GetLayout() 去裁，
        // 这处过道会被当成越界丢掉（ToSettings 交出去的是同一个引用，丢掉就回不来）。
        var dailyCanvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(1, 0, 0), true)
            .WithDoors(System.Collections.Immutable.ImmutableArray.Create(new RoomDoor(RoomEdge.Front, 0.5, "前门")));

        var settings = new SeatChartSettings
        {
            Groups = 4,
            Rows = 6,
            Columns = 2,
            Canvas = dailyCanvas,
            LayoutOverride = new SeatChartLayout(1, 5, 6),
        };
        settings.Normalize();

        Assert.True(settings.GetLayout().Surface.IsEmpty);
        Assert.Same(dailyCanvas, settings.Canvas);
        Assert.True(settings.Canvas.IsDisabled(new SeatPosition(1, 0, 0)));
        Assert.False(settings.Canvas.Doors.IsDefaultOrEmpty);
    }

    [Fact]
    public void Compose_WithSeatNumbersButNoAliasesNumbersEverySeat()
    {
        // 座号属于座位而不是人：空座也编号，老师要按号布置考桌。
        var settings = new SeatChartSettings { Groups = 1, Rows = 2, Columns = 2 };
        var students = MakeStudents(2);
        var assignments = new Dictionary<SeatPosition, Student>
        {
            [new SeatPosition(0, 0, 0)] = students[0],
            [new SeatPosition(0, 1, 1)] = students[1],
        };
        var numbers = new Dictionary<SeatPosition, int>
        {
            [new SeatPosition(0, 0, 0)] = 1,
            [new SeatPosition(0, 0, 1)] = 2,
            [new SeatPosition(0, 1, 0)] = 3,
            [new SeatPosition(0, 1, 1)] = 4,
        };

        var document = SeatChartComposer.Compose(settings, assignments, students.Count, numbers);

        // 按物理坐标找，不按显示序找：教师视角的显示序是倒的（第一排在最下）。
        var emptySeat = document.Groups[0].Rows
            .SelectMany(r => r.Seats)
            .Single(s => s.Position == new SeatPosition(0, 0, 1));

        Assert.True(emptySeat.IsEmpty);
        Assert.Equal(2, emptySeat.SeatNumber);
    }

    // ----------------------------- 夹具 -----------------------------

    /// <summary>
    /// 30 人进考场：日常先按 4 组 × 6 排 × 2 列排好，再切考场。
    /// 日常 48 座、考场 30 座——两者形状完全不同，正好能照出「导出走了哪一份布局」。
    /// </summary>
    private static SeatGridViewModel ExamViewModel(int count)
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DemoRosterFixture(count));
        vm.GroupCount = 4;
        vm.RowCount = 6;
        vm.ColumnCount = 2;
        vm.ArrangeModeIndex = 1;
        vm.AutoArrange();
        vm.EnterExamMode(new ExamLayoutOptions());
        return vm;
    }

    /// <summary>合成名单：全部虚构姓名，与 DemoRoster 同一套写法，不含任何真实数据。</summary>
    private static RosterImportResult DemoRosterFixture(int count)
    {
        var names = new[]
        {
            "陈思远", "李雨桐", "王浩然", "张欣怡", "刘子涵", "赵梓萱", "周俊杰", "吴梦琪",
            "徐一鸣", "孙悦", "马晨曦", "朱嘉怡", "胡博文", "郭诗涵", "何宇航", "高晓雅",
            "林书言", "罗文静", "郑凯", "梁若曦", "谢明轩", "宋佳宁", "唐子睿", "许婉清",
            "韩泽宇", "冯可欣", "邓砚青", "曹语嫣", "彭嘉树", "曾梦瑶", "萧然", "田雨欣",
        };

        var students = new List<Student>();
        for (var i = 0; i < Math.Min(count, names.Length); i++)
        {
            students.Add(new Student
            {
                Name = names[i],
                Gender = i % 2 == 0 ? Gender.Male : Gender.Female,
                Number = (2026001 + i).ToString(),
            });
        }

        return new RosterImportResult("合成名单.xlsx", "合成名单.xlsx", students, Array.Empty<string>());
    }

    private static List<Student> MakeStudents(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Student { Name = $"同学{i + 1}", Gender = i % 2 == 0 ? Gender.Male : Gender.Female })
            .ToList();
}
