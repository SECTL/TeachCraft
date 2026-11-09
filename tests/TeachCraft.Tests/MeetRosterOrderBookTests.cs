using ClosedXML.Excel;
using MeetRoster.Core.Export;
using MeetRoster.Core.Import;
using MeetRoster.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 秩序册（一份工作簿含「代表队名单 + 竞赛日程 + 项目分组表」）的导入与导出回归。
/// 全部使用内存构造的合成工作簿：虚构学校、虚构姓名，不含任何真实数据。
/// </summary>
public sealed class MeetRosterOrderBookTests : IDisposable
{
    private readonly string _root;

    public MeetRosterOrderBookTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MeetRoster.OrderBookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响测试结论 */ }
    }

    // ---------- 导入 ----------

    [Fact]
    public void OrderBook_ReadsRosterWithBibsAndGenders()
    {
        var path = CreateOrderBook();

        var result = OrderBookImporter.Import(path);

        Assert.True(result.HasRoster);
        Assert.True(result.HasSchedule);
        Assert.Equal(2, result.Project.Classes.Count);

        var class1 = result.Project.Classes.Single(c => c.Name == "七年级1班");
        // 块头写 4 人，其中一处是「例：张三」占位 → 实际 3 名运动员。
        Assert.Equal(3, class1.Athletes.Count);
        Assert.Equal("0001", class1.Athletes[0].Bib);
        Assert.Equal("陈屿", class1.Athletes[0].Name);
        Assert.Equal("男子", class1.Athletes[0].Gender);
        Assert.Equal("苏晚晴", class1.Athletes[2].Name);
        Assert.Equal("女子", class1.Athletes[2].Gender);
        // 领队 / 教练 / 工作人员 三个字段来自块头下方的三行。
        Assert.Equal("沈青禾", class1.LeaderContact);
        Assert.Equal("韩知微", class1.Coach);
        Assert.Equal("顾知远", class1.Staff);
    }

    [Fact]
    public void OrderBook_AppliesGroupingEventsToAthletes()
    {
        var path = CreateOrderBook();

        var result = OrderBookImporter.Import(path);

        var su = result.Project.Classes
            .Single(c => c.Name == "七年级1班").Athletes
            .Single(a => a.Name == "苏晚晴");

        // 分组表里 苏晚晴 报了女子 100 米与跳高 → 两个单项格都要落上。
        Assert.Contains("100米", new[] { su.IndividualEvent1, su.IndividualEvent2 });
        Assert.Contains("跳高", new[] { su.IndividualEvent1, su.IndividualEvent2 });

        var chen = result.Project.Classes
            .Single(c => c.Name == "七年级1班").Athletes
            .Single(a => a.Name == "陈屿");
        Assert.Contains("200米", new[] { chen.IndividualEvent1, chen.IndividualEvent2 });
    }

    [Fact]
    public void OrderBook_RecordsTeamEventsOnTheClassNotOnEveryAthlete()
    {
        var path = CreateOrderBook();

        var result = OrderBookImporter.Import(path);
        var class1 = result.Project.Classes.Single(c => c.Name == "七年级1班");

        // 团体项目按班登记：秩序册不点名队员，不能伪造成「全班都跑接力」。
        Assert.Contains("4x100米接力", class1.TeamEvents);
        Assert.All(class1.Athletes, a => Assert.Equal(string.Empty, a.TeamEvent1));
    }

    [Fact]
    public void OrderBook_SkipsPlaceholderRowsAndReportsThem()
    {
        var path = CreateOrderBook();

        var result = OrderBookImporter.Import(path);

        Assert.DoesNotContain(result.Project.Classes,
            c => c.Athletes.Any(a => a.Name.StartsWith("例", StringComparison.Ordinal)));
        Assert.Contains(result.Issues, i => i.Message.Contains("占位", StringComparison.Ordinal));
    }

    [Fact]
    public void OrderBook_ScheduleKeepsDaysSlotsAndLanes()
    {
        var path = CreateOrderBook();

        var result = OrderBookImporter.Import(path);

        // 两个段落都在 2026-11-11：按日期归并后是 1 个比赛日、5 个时段。
        Assert.Single(result.Project.Days);
        var firstDay = result.Project.Days[0];
        Assert.Equal(5, firstDay.Slots.Count);
        var slot = firstDay.Slots.Single(s => s.EventName == "100米" && s.Gender == "女子");

        Assert.Equal("11:00", slot.Time);
        Assert.Equal(2, slot.Entries.Count);
        Assert.Equal("七年级1班", slot.Entries[0].ClassName);
        Assert.Contains("第 1 道", slot.Entries[0].Lane, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderBook_ThrowsWhenNothingRecognizable()
    {
        var path = Path.Combine(_root, "空白.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Sheet1");
            sheet.Cell(1, 1).Value = "随便写点什么";
            workbook.SaveAs(path);
        }

        var ex = Assert.Throws<InvalidDataException>(() => OrderBookImporter.Import(path));
        Assert.Contains("秩序册", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LooksLikeOrderBook_DetectsSheetNames()
    {
        var path = CreateOrderBook();

        Assert.True(OrderBookImporter.LooksLikeOrderBook(path));
        Assert.False(OrderBookImporter.LooksLikeOrderBook(Path.Combine(_root, "不存在.xlsx")));
    }

    // ---------- 导出模型 ----------

    [Fact]
    public void ExportBuilder_SingleClassFiltersScheduleEntries()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var summary = MeetExportBuilder.Build(project, new MeetExportOptions { ClassName = "七年级1班" })
            .Single();

        Assert.Equal("七年级1班", summary.Title);
        Assert.Equal(3, summary.AthleteCount);
        // 只列该班的参赛记录：与工程里该班的条目数一致，且不夹带其他班。
        Assert.Equal(
            summary.Entries.Count,
            project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries)
                .Count(e => e.ClassName == "七年级1班"));
        Assert.DoesNotContain(summary.Entries, line => line.AthleteName == "林见夏");
    }

    [Fact]
    public void ExportBuilder_AllClassesKeepsEveryone()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var summaries = MeetExportBuilder.Build(project, new MeetExportOptions());

        Assert.Equal(2, summaries.Count);
        Assert.Equal(6, summaries.Sum(s => s.AthleteCount));
    }

    // ---------- Excel 导出 ----------

    [Fact]
    public void WorkbookExport_WritesFourSheetsAndHighlightFill()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        var path = Path.Combine(_root, "导出", "七年级1班参赛手册.xlsx");

        var target = MeetWorkbookExporter.Export(
            project, new MeetExportOptions { ClassName = "七年级1班" }, path);

        Assert.True(File.Exists(target));
        using var workbook = new XLWorkbook(target);
        Assert.Equal(
            new[] { "班级报名", "单项名单", "赛程", "班级汇总" },
            workbook.Worksheets.Select(w => w.Name).ToArray());

        var roster = workbook.Worksheet("班级报名");
        Assert.Contains("七年级1班", roster.CellsUsed().Select(c => c.GetString()));

        var schedule = workbook.Worksheet("赛程");
        var highlighted = schedule.CellsUsed()
            .Count(c => c.Style.Fill.BackgroundColor == XLColor.FromHtml("#FBE4D7"));
        Assert.True(highlighted > 0, "单班导出应给该班条目加底色。");

        // 表头必须写出来：日期与上午/下午各占一行，其后才是列名。
        var headerRow = schedule.CellsUsed()
            .Select(c => c.Address.RowNumber)
            .Where(r => schedule.Cell(r, 1).GetString() == "时间")
            .DefaultIfEmpty(0)
            .Min();
        Assert.True(headerRow > 0, "赛程页缺少表头行。");
        Assert.Equal("参赛人", schedule.Cell(headerRow, 5).GetString());
        Assert.Equal("组次 / 道次", schedule.Cell(headerRow, 6).GetString());
    }

    [Fact]
    public void WorkbookExport_RefusesToOverwriteSource()
    {
        var path = CreateOrderBook();
        var project = OrderBookImporter.Import(path).Project;
        // 把某一班的来源指向秩序册本身，模拟「导出到同一个文件」。
        project.Classes[0].SourcePath = path;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetWorkbookExporter.Export(project, new MeetExportOptions(), path));
        Assert.Contains("不能覆盖", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbookExport_RejectsEmptyProject()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetWorkbookExporter.Export(new MeetProject(), new MeetExportOptions(),
                Path.Combine(_root, "空.xlsx")));
        Assert.Contains("还没有导入", ex.Message, StringComparison.Ordinal);
    }

    // ---------- PDF 导出 ----------

    [Fact]
    public void PdfExport_ProducesValidPdfWithPages()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        var path = Path.Combine(_root, "导出", "全部班级.pdf");

        var target = MeetPdfExporter.Export(project, new MeetExportOptions(), path);

        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 5000);
        Assert.Equal("%PDF", File.ReadAllText(target)[..4]);
    }

    [Fact]
    public void PdfExport_LongRosterSpillsOntoMorePages()
    {
        // 60 人 × 4 个班 = 240 行，一页放不下；跨页必须继续输出，不能把后半截画到页外。
        var project = CreateLargeProject(classes: 4, athletesPerClass: 60);
        var path = Path.Combine(_root, "长名单.pdf");

        MeetPdfExporter.Export(project, new MeetExportOptions(), path);

        var pages = CountPdfPages(path);
        Assert.True(pages >= 3, $"长名单应跨多页，实际 {pages} 页。");
    }

    // ---------- 分组表跨页 ----------

    [Fact]
    public void ScheduleImport_GroupSplitAcrossLanePages_ReadsEveryAthlete()
    {
        // 真实秩序册按每页 8 道排版：一组 12 人会被拆成「第 1 页 1–8 道 + 续页 9–12 道」。
        // 旧实现只读第一页，34 人的 800 米只读到 16 人。
        var path = Path.Combine(_root, "跨页分组.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var schedule = workbook.AddWorksheet("竞赛日程");
            schedule.Cell(1, 1).Value = "示例中学田径运动会";
            schedule.Cell(4, 1).Value = "2026-11-11  上午  径赛";
            WriteScheduleHeader(schedule, 6);
            WriteScheduleRow(schedule, 7, "七年级", "女子", "800米", "决赛", 12, 1, 7, "14:36");

            var grouping = workbook.AddWorksheet("项目分组表");
            grouping.Cell(1, 1).Value = "七年级";
            grouping.Cell(1, 3).Value = "女子800米";
            grouping.Cell(1, 6).Value = "决赛";
            grouping.Cell(1, 7).Value = "12人/1组/取 7 名";

            // 第一页：第 1 组，1–8 道
            grouping.Cell(2, 1).Value = "第 1 组";
            for (var i = 0; i < 8; i++)
            {
                var column = 2 + i;
                grouping.Cell(2, column).Value = i + 1;
                grouping.Cell(3, column).Value = $"00{i + 1:00}";
                grouping.Cell(4, column).Value = $"甲{i + 1:00}";
                grouping.Cell(5, column).Value = $"{i + 1}班";
            }

            // 续页：重新给一行道次（9–12），后面才是号码 / 姓名 / 班级
            for (var i = 0; i < 4; i++)
            {
                var column = 2 + i;
                grouping.Cell(6, column).Value = 9 + i;
                grouping.Cell(7, column).Value = $"00{9 + i:00}";
                grouping.Cell(8, column).Value = $"乙{i + 1:00}";
                grouping.Cell(9, column).Value = $"{9 + i}班";
            }

            workbook.SaveAs(path);
        }

        var result = ScheduleWorkbookImporter.Import(path);

        var slot = result.Days[0].Slots.Single(s => s.EventName == "800米");
        Assert.Equal(12, slot.Entries.Count);
        Assert.Contains(slot.Entries, e => e.AthleteName == "乙04");
        Assert.Contains(slot.Entries, e => e.Lane.Contains("第 12 道", StringComparison.Ordinal));
    }

    // ---------- 统一导入入口 ----------

    [Fact]
    public void DetectKind_SeparatesOrderBookFromScheduleOnly()
    {
        var orderBook = CreateOrderBook();
        Assert.Equal(MeetWorkbookKind.OrderBook, OrderBookImporter.DetectKind(orderBook));

        var scheduleOnly = Path.Combine(_root, "只有赛程.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var schedule = workbook.AddWorksheet("竞赛日程");
            schedule.Cell(1, 1).Value = "示例中学";
            schedule.Cell(4, 1).Value = "2026-11-11  上午  径赛";
            WriteScheduleHeader(schedule, 6);
            WriteScheduleRow(schedule, 7, "七年级", "女子", "100米", "预赛", 1, 1, 7, "11:00");
            workbook.AddWorksheet("项目分组表").Cell(1, 1).Value = "项目分组表";
            workbook.SaveAs(scheduleOnly);
        }

        Assert.Equal(MeetWorkbookKind.ScheduleOnly, OrderBookImporter.DetectKind(scheduleOnly));
    }

    [Fact]
    public void DetectKind_ReturnsUnknownForForeignWorkbook()
    {
        var path = Path.Combine(_root, "不相干.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Sheet1").Cell(1, 1).Value = "随便写点什么";
            workbook.SaveAs(path);
        }

        Assert.Equal(MeetWorkbookKind.Unknown, OrderBookImporter.DetectKind(path));
        Assert.Equal(MeetWorkbookKind.Unknown,
            OrderBookImporter.DetectKind(Path.Combine(_root, "不存在.xlsx")));
        // 只有赛程的表不算秩序册，旧入口要如实说「不是」。
        Assert.False(OrderBookImporter.LooksLikeOrderBook(path));
    }

    [Fact]
    public void Import_RecordsSourcePathForOriginalStyleExport()
    {
        var path = CreateOrderBook();

        var result = OrderBookImporter.Import(path);

        Assert.Equal(Path.GetFullPath(path), result.Project.SourcePath);
    }

    // ---------- 原表高亮 ----------

    [Fact]
    public void OriginalHighlight_PaintsOnlyTheTargetClassAndKeepsEverythingElse()
    {
        var path = CreateOrderBook();
        var project = OrderBookImporter.Import(path).Project;
        var target = Path.Combine(_root, "导出", "原表高亮.xlsx");

        var output = MeetOriginalWorkbookHighlighter.Export(
            project, new MeetExportOptions { ClassName = "七年级1班" }, target);

        Assert.True(File.Exists(output));

        using var workbook = new XLWorkbook(output);
        // 原表的工作表都还在，另加一张说明页。
        Assert.Contains("代表队名单", workbook.Worksheets.Select(w => w.Name));
        Assert.Contains("项目分组表", workbook.Worksheets.Select(w => w.Name));
        Assert.Contains("高亮说明", workbook.Worksheets.Select(w => w.Name));

        var roster = workbook.Worksheet("代表队名单");
        var fill = XLColor.FromHtml("#FBE4D7");

        // 七年级1班块（第 4 行起）被涂色；七年级2班块（第 12 行起）没有。
        Assert.Equal(fill, roster.Cell(4, 1).Style.Fill.BackgroundColor);
        Assert.Equal(fill, roster.Cell(8, 3).Style.Fill.BackgroundColor);
        Assert.NotEqual(fill, roster.Cell(12, 1).Style.Fill.BackgroundColor);
        Assert.NotEqual(fill, roster.Cell(13, 3).Style.Fill.BackgroundColor);

        // 内容原样保留。
        Assert.Equal("七年级 1班 (4)人", roster.Cell(4, 1).GetString());
        Assert.Equal("苏晚晴", roster.Cell(9, 3).GetString());

        // 分组表里该班的单元格被涂色，别班的没有。
        // 块结构：块头 / 道次 / 号码 / 姓名 / 班级——班级在第 8 行。
        var grouping = workbook.Worksheet("项目分组表");
        Assert.Equal(fill, grouping.Cell(8, 2).Style.Fill.BackgroundColor);   // 1班 苏晚晴
        Assert.NotEqual(fill, grouping.Cell(8, 3).Style.Fill.BackgroundColor); // 2班 林见夏
        // 同一行的号码 / 姓名不该被刷成色块。
        Assert.NotEqual(fill, grouping.Cell(6, 2).Style.Fill.BackgroundColor);
        Assert.NotEqual(fill, grouping.Cell(7, 2).Style.Fill.BackgroundColor);
    }

    [Fact]
    public void OriginalHighlight_DoesNotModifyTheSourceFile()
    {
        var path = CreateOrderBook();
        var before = File.GetLastWriteTimeUtc(path);
        var bytesBefore = File.ReadAllBytes(path);
        var project = OrderBookImporter.Import(path).Project;

        MeetOriginalWorkbookHighlighter.Export(
            project, new MeetExportOptions { ClassName = "七年级1班" },
            Path.Combine(_root, "副本.xlsx"));

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
    }

    [Fact]
    public void OriginalHighlight_RefusesToOverwriteSource()
    {
        var path = CreateOrderBook();
        var project = OrderBookImporter.Import(path).Project;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetOriginalWorkbookHighlighter.Export(
                project, new MeetExportOptions { ClassName = "七年级1班" }, path));
        Assert.Contains("不能覆盖", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalHighlight_ExplainsWhenSourceIsGone()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        project.SourcePath = Path.Combine(_root, "已被移动.xlsx");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetOriginalWorkbookHighlighter.Export(
                project, new MeetExportOptions { ClassName = "七年级1班" },
                Path.Combine(_root, "出.xlsx")));
        Assert.Contains("重新导入", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalHighlight_ReportsWhenClassIsAbsent()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetOriginalWorkbookHighlighter.Export(
                project, new MeetExportOptions { ClassName = "九年级9班" },
                Path.Combine(_root, "查无此班.xlsx")));
        Assert.Contains("没有找到", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalHighlight_GroupingDoesNotSpillIntoOtherGrades()
    {
        // 分组表的班级列只写「6班」，块头才给年级。早先只做 EndsWith("6班")，
        // 结果七 / 八 / 九年级的 6 班一起被刷成色块。必须按块头拼全名精确比对。
        var path = CreateMultiGradeOrderBook();
        var project = OrderBookImporter.Import(path).Project;
        var target = Path.Combine(_root, "跨年级高亮.xlsx");

        MeetOriginalWorkbookHighlighter.Export(
            project, new MeetExportOptions { ClassName = "九年级6班" }, target);

        using var workbook = new XLWorkbook(target);
        var grouping = workbook.Worksheet("项目分组表");
        var fill = XLColor.FromHtml("#FBE4D7");

        // 三个块依次从第 1 / 8 / 15 行开始，班级行在块头 +4。
        // 九年级 6 班被涂色；七年级 / 八年级的 6 班不涂。
        Assert.Equal(fill, grouping.Cell(19, 2).Style.Fill.BackgroundColor);    // 九年级块
        Assert.NotEqual(fill, grouping.Cell(5, 2).Style.Fill.BackgroundColor);   // 七年级块
        Assert.NotEqual(fill, grouping.Cell(12, 2).Style.Fill.BackgroundColor);  // 八年级块

        // 整表里被涂色的「N班」必须只出现在九年级块内。
        var paintedInOtherGrades = 0;
        var grade = string.Empty;
        for (var row = 1; row <= grouping.LastRowUsed()!.RowNumber(); row++)
        {
            var a = grouping.Cell(row, 1).GetString().Trim();
            if (a.StartsWith("七", StringComparison.Ordinal) ||
                a.StartsWith("八", StringComparison.Ordinal) ||
                a.StartsWith("九", StringComparison.Ordinal))
                grade = a[..3];

            for (var column = 2; column <= 9; column++)
            {
                var cell = grouping.Cell(row, column);
                if (cell.Style.Fill.BackgroundColor != fill) continue;
                if (!cell.GetString().Trim().EndsWith("班", StringComparison.Ordinal)) continue;
                if (!string.Equals(grade, "九年级", StringComparison.Ordinal)) paintedInOtherGrades++;
            }
        }

        Assert.Equal(0, paintedInOtherGrades);
    }

    // ---------- 卡片版式 ----------

    [Fact]
    public void CardBuilder_GroupsEntriesIntoDayPeriodAndCard()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var document = MeetCardBuilder.Build(
            project,
            MeetExportBuilder.Build(project, new MeetExportOptions { ClassName = "七年级1班" }),
            new MeetExportOptions { ClassName = "七年级1班" });

        Assert.NotEmpty(document.Sections);
        Assert.All(document.Sections, section =>
        {
            Assert.NotEmpty(section.Periods);
            Assert.All(section.Periods, period => Assert.NotEmpty(period.Items));
        });

        // 卡片标题与时段来自真实赛程，chip 指向该班选手。
        var items = document.Sections.SelectMany(s => s.Periods).SelectMany(p => p.Items).ToList();
        Assert.Contains(items, i => i.EventName == "100米");
        // 苏晚晴报了 100 米与跳高，两张卡里都会出现她；任取一条核对班级前缀即可。
        var su = items.SelectMany(i => i.Entries).First(e => e.AthleteName == "苏晚晴");
        Assert.Equal("七年级1班", su.ClassName);
        Assert.False(su.ShowClass); // 单班导出不重复写班级
    }

    [Fact]
    public void CardBuilder_AllClassesKeepsClassPrefixOnChips()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var document = MeetCardBuilder.Build(
            project, MeetExportBuilder.Build(project, new MeetExportOptions()), new MeetExportOptions());

        var entries = document.Sections
            .SelectMany(s => s.Periods)
            .SelectMany(p => p.Items)
            .SelectMany(i => i.Entries)
            .ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.ShowClass, "全部班级导出时 chip 要带班级前缀。"));
    }

    [Fact]
    public void CardPdf_ExportsValidPdfWithCards()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        var path = Path.Combine(_root, "导出", "赛程卡.pdf");

        var target = MeetPdfExporter.Export(
            project, new MeetExportOptions { ClassName = "七年级1班", Layout = MeetPdfLayout.Card }, path);

        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 5000);
        Assert.Equal("%PDF", File.ReadAllText(target)[..4]);
    }

    [Fact]
    public void CardPdf_MultiDaySpillsToMorePagesWithoutCuttingCards()
    {
        // 两个比赛日各若干卡片：分页必须按时段装箱，不能把一张卡劈成两半。
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        var path = Path.Combine(_root, "赛程卡跨页.pdf");

        MeetPdfExporter.Export(
            project, new MeetExportOptions { ClassName = "七年级1班", Layout = MeetPdfLayout.Card }, path);

        Assert.True(CountPdfPages(path) >= 1);
    }

    // ---------- 导出预览 ----------

    [Fact]
    public void Preview_PdfHandbookReturnsPngWithCaption()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var preview = MeetExportPreviewRenderer.Render(
            project, new MeetExportOptions { ClassName = "七年级1班" }, MeetExportFormatKind.Pdf);

        Assert.NotEmpty(preview.Png);
        Assert.Equal("PNG", DecodeFormat(preview.Png));
        Assert.Contains("参赛手册", preview.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_PdfCardReturnsPngWithCardCaption()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var preview = MeetExportPreviewRenderer.Render(
            project,
            new MeetExportOptions { ClassName = "七年级1班", Layout = MeetPdfLayout.Card },
            MeetExportFormatKind.Pdf);

        Assert.NotEmpty(preview.Png);
        Assert.Contains("赛程卡", preview.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_ExcelListsSelectedSheets()
    {
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;

        var preview = MeetExportPreviewRenderer.Render(
            project,
            new MeetExportOptions { ClassName = "七年级1班", IncludeEvents = false },
            MeetExportFormatKind.Excel);

        Assert.NotEmpty(preview.Png);
        Assert.Contains("Excel", preview.Caption, StringComparison.Ordinal);
        Assert.Contains("班级报名", preview.Caption, StringComparison.Ordinal);
        Assert.DoesNotContain("单项名单", preview.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_EmptyProjectReturnsPlaceholder()
    {
        var preview = MeetExportPreviewRenderer.Render(
            new MeetProject(), new MeetExportOptions(), MeetExportFormatKind.Pdf);

        Assert.NotEmpty(preview.Png);
        Assert.Contains("无法预览", preview.Caption, StringComparison.Ordinal);
    }

    // ---------- fixtures ----------

    /// <summary>
    /// 三个年级各有 6 班，分组表里都只写「6班」——用来验证高亮不会跨年级串色。
    /// </summary>
    private string CreateMultiGradeOrderBook()
    {
        var path = Path.Combine(_root, "跨年级秩序册.xlsx");
        using var workbook = new XLWorkbook();

        var roster = workbook.AddWorksheet("代表队名单");
        roster.Cell(1, 1).Value = "代表队名单";
        var row = 4;
        foreach (var grade in new[] { "七年级", "八年级", "九年级" })
        {
            roster.Cell(row, 1).Value = $"{grade} 6班 (1)人";
            roster.Cell(row + 1, 1).Value = "男子";
            roster.Cell(row + 1, 2).Value = "0001";
            roster.Cell(row + 1, 3).Value = $"{grade}选手";
            row += 4;
        }

        var schedule = workbook.AddWorksheet("竞赛日程");
        schedule.Cell(1, 1).Value = "示例中学田径运动会";
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];

        var grouping = workbook.AddWorksheet("项目分组表");
        var groupRow = 1;
        foreach (var (grade, time) in new[] { ("七年级", "09:00"), ("八年级", "09:30"), ("九年级", "10:00") })
        {
            var section = 3 + (groupRow / 8) * 3;
            schedule.Cell(section, 1).Value = $"2026-11-11  上午  径赛";
            for (var i = 0; i < heads.Length; i++) schedule.Cell(section + 2, 1 + i).Value = heads[i];
            schedule.Cell(section + 3, 2).Value = grade;
            schedule.Cell(section + 3, 3).Value = "女子";
            schedule.Cell(section + 3, 4).Value = "100米";
            schedule.Cell(section + 3, 5).Value = "预赛";
            schedule.Cell(section + 3, 6).Value = 1;
            schedule.Cell(section + 3, 7).Value = 1;
            schedule.Cell(section + 3, 8).Value = 7;
            schedule.Cell(section + 3, 9).Value = time;

            grouping.Cell(groupRow, 1).Value = grade;
            grouping.Cell(groupRow, 3).Value = "女子100米";
            grouping.Cell(groupRow, 6).Value = "预赛";
            grouping.Cell(groupRow, 7).Value = "1人/1组/取 7 名";
            grouping.Cell(groupRow + 1, 1).Value = "第 1 组";
            grouping.Cell(groupRow + 1, 2).Value = 1;
            grouping.Cell(groupRow + 2, 2).Value = "0001";
            grouping.Cell(groupRow + 3, 2).Value = $"{grade}选手";
            grouping.Cell(groupRow + 4, 2).Value = "6班";
            groupRow += 7;
        }

        workbook.SaveAs(path);
        return path;
    }

    /// <summary>
    /// 合成秩序册：两个班、两场比赛日。
    /// 七年级1班 4 人（含一处「例：张三」占位），七年级2班 3 人；
    /// 分组表给 100 米 / 200 米 / 跳高 与 4x100 接力。
    /// </summary>
    private string CreateOrderBook()
    {
        var path = Path.Combine(_root, "示例中学秩序册.xlsx");
        using var workbook = new XLWorkbook();
        WriteRoster(workbook.AddWorksheet("代表队名单"));
        WriteSchedule(workbook.AddWorksheet("竞赛日程"));
        WriteGrouping(workbook.AddWorksheet("项目分组表"));
        workbook.SaveAs(path);
        return path;
    }

    private static void WriteRoster(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "代表队名单";

        // 班块 1
        sheet.Cell(4, 1).Value = "七年级 1班 (4)人";
        sheet.Cell(5, 1).Value = "领队: ";
        sheet.Cell(5, 2).Value = "沈青禾";
        sheet.Cell(6, 1).Value = "教练: ";
        sheet.Cell(6, 2).Value = "韩知微";
        sheet.Cell(7, 1).Value = "工作人员: ";
        sheet.Cell(7, 2).Value = "顾知远";
        sheet.Cell(8, 1).Value = "男子";
        sheet.Cell(8, 2).Value = "0001";
        sheet.Cell(8, 3).Value = "陈屿";
        sheet.Cell(8, 4).Value = "0002";
        sheet.Cell(8, 5).Value = "许砚";
        sheet.Cell(9, 1).Value = "女子";
        sheet.Cell(9, 2).Value = "0003";
        sheet.Cell(9, 3).Value = "苏晚晴";
        sheet.Cell(9, 4).Value = "0004";
        sheet.Cell(9, 5).Value = "例：张三";

        // 班块 2
        sheet.Cell(12, 1).Value = "七年级 2班 (3)人";
        sheet.Cell(13, 1).Value = "男子";
        sheet.Cell(13, 2).Value = "0021";
        sheet.Cell(13, 3).Value = "沈观澜";
        sheet.Cell(13, 4).Value = "0022";
        sheet.Cell(13, 5).Value = "陆行舟";
        sheet.Cell(14, 1).Value = "女子";
        sheet.Cell(14, 2).Value = "0023";
        sheet.Cell(14, 3).Value = "林见夏";
    }

    private static void WriteSchedule(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "竞赛日程";
        sheet.Cell(4, 1).Value = "2026-11-11  上午  径赛";
        WriteScheduleHeader(sheet, 6);
        WriteScheduleRow(sheet, 7, "七年级", "女子", "100米", "预赛", 2, 1, 7, "11:00");
        WriteScheduleRow(sheet, 8, "七年级", "男子", "100米", "预赛", 3, 1, 7, "11:06");
        WriteScheduleRow(sheet, 9, "七年级", "男子", "200米", "预赛", 2, 1, 7, "11:12");
        WriteScheduleRow(sheet, 10, "七年级", "女子", "跳高", "决赛", 1, 1, 7, "11:18");

        sheet.Cell(13, 1).Value = "2026-11-11  下午  田赛";
        WriteScheduleHeader(sheet, 15);
        WriteScheduleRow(sheet, 16, "七年级", "混合", "4x100米接力", "决赛", 2, 1, 7, "14:30");
    }

    private static void WriteScheduleHeader(IXLWorksheet sheet, int row)
    {
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];
        for (var i = 0; i < heads.Length; i++) sheet.Cell(row, 1 + i).Value = heads[i];
    }

    private static void WriteScheduleRow(IXLWorksheet sheet, int row, string grade, string gender,
        string eventName, string stage, int count, int groups, int advance, string time)
    {
        sheet.Cell(row, 2).Value = grade;
        sheet.Cell(row, 3).Value = gender;
        sheet.Cell(row, 4).Value = eventName;
        sheet.Cell(row, 5).Value = stage;
        sheet.Cell(row, 6).Value = count;
        sheet.Cell(row, 7).Value = groups;
        sheet.Cell(row, 8).Value = advance;
        sheet.Cell(row, 9).Value = time;
    }

    private static void WriteGrouping(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = "项目分组表";

        // 女子 100 米：七年级1班 苏晚晴（第 1 组第 1 道）、七年级2班 林见夏（第 1 组第 2 道）
        WriteIndividualBlock(sheet, 4, "七年级", "女子100米", "预赛",
            ("0003", "苏晚晴", "1班"), ("0023", "林见夏", "2班"));

        // 男子 100 米：陈屿、许砚、沈观澜、陆行舟
        WriteIndividualBlock(sheet, 12, "七年级", "男子100米", "预赛",
            ("0001", "陈屿", "1班"), ("0002", "许砚", "1班"),
            ("0021", "沈观澜", "2班"), ("0022", "陆行舟", "2班"));

        // 男子 200 米：陈屿 + 占位
        WriteIndividualBlock(sheet, 20, "七年级", "男子200米", "预赛",
            ("0001", "陈屿", "1班"), ("0099", "例：张三", "1班"));

        // 女子跳高：苏晚晴
        WriteIndividualBlock(sheet, 28, "七年级", "女子跳高", "决赛",
            ("0003", "苏晚晴", "1班"));

        // 4x100 接力：按班列队，不点名队员
        sheet.Cell(36, 1).Value = "七年级";
        sheet.Cell(36, 3).Value = "混合4x100米接力";
        sheet.Cell(36, 6).Value = "决赛";
        sheet.Cell(36, 7).Value = "2队/1组/取 7 名";
        sheet.Cell(37, 1).Value = "第 1 组";
        sheet.Cell(37, 2).Value = 1;
        sheet.Cell(37, 3).Value = 2;
        sheet.Cell(38, 2).Value = "1班";
        sheet.Cell(38, 3).Value = "2班";
    }

    /// <summary>块头 / 道次 / 组号 / 号码 / 姓名 / 班级（与真实秩序册的组块结构一致）。</summary>
    private static void WriteIndividualBlock(IXLWorksheet sheet, int startRow, string grade,
        string eventName, string stage, params (string Bib, string Name, string Class)[] athletes)
    {
        sheet.Cell(startRow, 1).Value = grade;
        sheet.Cell(startRow, 3).Value = eventName;
        sheet.Cell(startRow, 6).Value = stage;
        sheet.Cell(startRow, 7).Value = $"{athletes.Length}人/1组/取 7 名";

        sheet.Cell(startRow + 1, 1).Value = "第 1 组";
        for (var i = 0; i < athletes.Length; i++)
        {
            var column = 2 + i;
            sheet.Cell(startRow + 1, column).Value = i + 1;
            sheet.Cell(startRow + 2, column).Value = athletes[i].Bib;
            sheet.Cell(startRow + 3, column).Value = athletes[i].Name;
            sheet.Cell(startRow + 4, column).Value = athletes[i].Class;
        }
    }

    private static MeetProject CreateLargeProject(int classes, int athletesPerClass)
    {
        var project = new MeetProject { Title = "示例中学田径运动会", Venue = "田径场" };
        for (var c = 1; c <= classes; c++)
        {
            var meetClass = new MeetClass { Name = $"七年级{c}班", Group = "七年级" };
            for (var a = 1; a <= athletesPerClass; a++)
            {
                meetClass.Athletes.Add(new Athlete
                {
                    Name = $"学生{c:00}{a:00}",
                    Bib = $"{c:00}{a:00}",
                    Gender = a % 2 == 0 ? "男子" : "女子",
                    IndividualEvent1 = "100米",
                });
            }

            project.Classes.Add(meetClass);
        }

        var day = new MeetDay { Name = "第一天", DateText = "11 月 11 日", Date = new DateOnly(2026, 11, 11) };
        day.Slots.Add(new MeetSlot
        {
            Time = "11:00",
            Period = "上午",
            EventName = "100米",
            Grade = "七年级",
            Gender = "女子",
            Category = MeetEventCategory.Track,
            Stage = "预赛",
        });
        project.Days.Add(day);
        return project;
    }

    /// <summary>只数 PDF 里的 /Type /Page 对象，避免为测试引入 PDF 解析依赖。</summary>
    private static int CountPdfPages(string path)
    {
        var text = File.ReadAllText(path, System.Text.Encoding.Latin1);
        return System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page[^s]").Count;
    }

    /// <summary>按文件头判断图片格式，避免为测试引入解码依赖。</summary>
    private static string DecodeFormat(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            ? "PNG"
            : "OTHER";
}
