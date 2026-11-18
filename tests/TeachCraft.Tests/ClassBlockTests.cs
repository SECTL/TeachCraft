using ClassBlock.Core.Import;
using ClassBlock.Core.Models;
using ClassBlock.Core.Rendering;
using ClassBlock.Core.Timetable;
using SkiaSharp;

namespace TeachCraft.Tests;

public sealed class ClassBlockTests : IDisposable
{
    private readonly string _root;

    public ClassBlockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "ClassBlock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void MasterImporter_ReadsClassesServicesAndHeadTeachers()
    {
        var path = ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx");

        var master = MasterScheduleImporter.Import(path);

        Assert.Equal("九年级", master.Info.Grade);
        Assert.Equal("2026年春季学期", master.Info.Term);
        Assert.Equal("2026年5月", master.Info.Month);
        Assert.Equal(2, master.Classes.Count);
        Assert.Equal("九（1）班", master.Classes[0].Name);
        Assert.Equal("九1", master.Classes[0].Key);

        var first = master.Classes[0];
        Assert.Equal(5, first.Days.Count);
        Assert.Equal("语文", first.Days[0].Periods[0].Subject);
        Assert.Equal("张兰", first.Days[0].Periods[0].Teacher);
        Assert.Equal("培优辅差", first.Days[0].AfterSchool1.Subject);
        Assert.Equal("张兰", first.Days[0].EveningStudy);

        Assert.Equal("王兰", master.HeadTeachers["九1"]);
        Assert.Equal(new[] { "周兰", "吴梅", "郑兰", "王梅", "周兰" }, master.LunchStudy["九1"]);
        Assert.Equal("赵兰", master.AfterSchool["九1"][0].Service1);
        Assert.Equal("钱红", master.AfterSchool["九1"][0].Service2);
        Assert.Equal("语文", master.EarlyReading[1]);
        Assert.Equal("英语", master.EarlyReading[2]);
        Assert.Equal("升旗", master.EarlyReading[0]);
    }

    [Fact]
    public void TimetableBuilder_FollowsReferenceRowOrder()
    {
        var master = MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx"));
        var timetable = TimetableBuilder.Build(master, "九1", new TimetableSettings { ShowTeachers = true });

        Assert.Equal("九（1）班课程表", timetable.Title);
        Assert.Contains("2026年春季学期", timetable.Subtitle);
        Assert.Contains("班主任 王兰", timetable.Subtitle);

        var labels = timetable.Rows.Select(r => r.Label).ToArray();
        Assert.Equal(
            new[] { "早读", "1", "2", "", "3", "4", "", "午学", "5", "6", "7", "课服1", "课服2", "", "", "晚修" },
            labels);

        var lunch = timetable.Rows.Single(r => r.Kind == TimetableRowKind.Lunch);
        Assert.True(lunch.IsFullWidth);
        Assert.Contains("午餐", lunch.MergedText);

        var lunchStudy = timetable.Rows.Single(r => r.Label == "午学");
        Assert.Equal("周兰老师", lunchStudy.Cells[0]);

        var service = timetable.Rows.Single(r => r.Label == "课服1");
        Assert.Equal("培优辅差\n赵兰", service.Cells[0]);

        var evening = timetable.Rows.Single(r => r.Kind == TimetableRowKind.Evening);
        Assert.Equal(2, evening.Times.Count);
        Assert.Equal("张兰老师", evening.Cells[0]);

        var noTeachers = TimetableBuilder.Build(master, "九1", new TimetableSettings());
        Assert.Equal("语文", noTeachers.Rows.Single(r => r.Label == "1").Cells[0]);
    }

    [Fact]
    public void ClassBlock_RendersPngAndPdf()
    {
        var master = MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx"));
        var timetable = TimetableBuilder.Build(master, "九1", new TimetableSettings());

        var png = Path.Combine(_root, "timetable.png");
        TimetableRenderer.RenderPng(timetable, png, 2.0f);

        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 10_000);

        using (var bitmap = SKBitmap.Decode(png))
        {
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width >= 900);
            Assert.True(bitmap.Height > bitmap.Width);
        }

        var pdf = Path.Combine(_root, "timetable.pdf");
        TimetableRenderer.RenderPdf(timetable, pdf);

        Assert.True(File.Exists(pdf));
        Assert.True(new FileInfo(pdf).Length > 10_000);
        Assert.Equal("%PDF", File.ReadAllText(pdf)[..4]);
    }

    // ------------------------------------------------------------------
    // 日块右界（P2-19）：8 列日块只有 7 节 + 课服1，课服2 / 晚修不得读到下一天。

    [Fact]
    public void MasterImporter_EightColumnDayBlocks_LeaveService2AndEveningEmpty()
    {
        // 修复前必红：groupEnd 取整表末列，课服2 读到下一天第 1 节、晚修回退列读到第 2 节。
        var path = ClassBlockFixtures.CreateNarrowDayBlockWorkbook(
            _root, dayWidth: 8, includeServiceHeader: true);

        var master = MasterScheduleImporter.Import(path);
        var cls = master.Classes[0];

        Assert.Equal(2, master.Classes.Count);
        Assert.Equal(5, cls.Days.Count);
        foreach (var day in cls.Days)
        {
            Assert.Equal("语文", day.Periods[0].Subject);   // 每天第一节都是语文
            Assert.False(day.AfterSchool1.IsEmpty);         // 8 列日块有「课后服务」表头，课服1 可读
            Assert.True(day.AfterSchool2.IsEmpty);          // 但没有课服2，必须留空
            Assert.Equal(string.Empty, day.EveningStudy);
        }

        Assert.Contains(master.Warnings, w => w.Contains("没有找到") && w.Contains("晚修") && w.Contains("留空"));
    }

    [Fact]
    public void MasterImporter_SevenColumnDayBlocks_LeaveServiceAndEveningEmpty()
    {
        var path = ClassBlockFixtures.CreateNarrowDayBlockWorkbook(
            _root, dayWidth: 7, includeServiceHeader: false);

        var master = MasterScheduleImporter.Import(path);
        var cls = master.Classes[0];

        foreach (var day in cls.Days)
        {
            Assert.True(day.AfterSchool1.IsEmpty);
            Assert.True(day.AfterSchool2.IsEmpty);
            Assert.Equal(string.Empty, day.EveningStudy);
        }

        Assert.Contains(master.Warnings, w => w.Contains("课后服务"));
    }

    [Fact]
    public void MasterImporter_StandardTenColumnTable_HasNoMissingColumnWarning()
    {
        var master = MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx"));

        Assert.DoesNotContain(master.Warnings, w => w.Contains("没有找到课后服务") || w.Contains("没有找到晚修"));
        Assert.Equal("培优辅差", master.Classes[0].Days[0].AfterSchool1.Subject);
        Assert.Equal("张兰", master.Classes[0].Days[0].EveningStudy);
    }

    // ------------------------------------------------------------------
    // 早读诊断串（P2-20）：只进警告，绝不进 EarlyReadingRule / 脚注。

    [Fact]
    public void MasterImporter_UnparsedEarlyReadingNote_DiagnosticGoesToWarningsOnly()
    {
        var path = ClassBlockFixtures.CreateMasterWorkbook(_root, "diagnostic.xlsx");
        using (var workbook = new ClosedXML.Excel.XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("2-课程表");
            sheet.Cell(8, 1).Value =
                "早读安排：每天由语文课代表领读古诗，英语课代表带读单词，请各班准时开始，值日教师巡查记录。";
            workbook.Save();
        }

        var master = MasterScheduleImporter.Import(path);

        Assert.Equal(string.Empty, master.Info.EarlyReadingRule);
        Assert.Contains(master.Warnings, w => w.Contains("未解析"));

        var timetable = TimetableBuilder.Build(master, "九1", new TimetableSettings());
        Assert.DoesNotContain(timetable.Footnotes, f => f.Contains("未解析"));
        Assert.Empty(timetable.Footnotes);
        Assert.DoesNotContain(timetable.Rows, r => r.Label == "早读");
    }

    [Fact]
    public void MasterImporter_ParsedEarlyReadingNote_KeepsRuleInFootnotes()
    {
        var master = MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx"));
        var timetable = TimetableBuilder.Build(master, "九1", new TimetableSettings());

        Assert.Equal("周一 升旗 · 周二、周四 语文 · 周三、周五 英语", master.Info.EarlyReadingRule);
        Assert.Contains(timetable.Footnotes, f => f.StartsWith("早读：", StringComparison.Ordinal));
        Assert.DoesNotContain(timetable.Footnotes, f => f.Contains("未解析"));
    }

    // ------------------------------------------------------------------
    // 早读部分命中（P3-25）：只认出 N 天时必须告警，不能静默缺三天。

    [Fact]
    public void MasterImporter_PartialEarlyReading_ReportsRecognizedDayCount()
    {
        var path = ClassBlockFixtures.CreateMasterWorkbook(_root, "partial.xlsx");
        using (var workbook = new ClosedXML.Excel.XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("2-课程表");
            sheet.Cell(8, 1).Value = "备注：早读（二、四语文），请各班按时开始，值日教师巡查记录。";
            workbook.Save();
        }

        var master = MasterScheduleImporter.Import(path);

        Assert.Equal("语文", master.EarlyReading[1]);
        Assert.Equal("语文", master.EarlyReading[3]);
        Assert.True(string.IsNullOrEmpty(master.EarlyReading[0]));
        Assert.Contains(master.Warnings, w => w.Contains("只认出 2 天") && w.Contains("未识别"));
    }

    [Fact]
    public void MasterImporter_FullEarlyReading_NoPartialWarning()
    {
        var master = MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx"));

        Assert.DoesNotContain(master.Warnings, w => w.Contains("只认出"));
    }

    // ------------------------------------------------------------------
    // 班级空态两态（P3-24）：第二行与「清除搜索」跟着状态走。

    [Fact]
    public void ClassBlockViewModel_EmptyStateTextsFollowTwoStates()
    {
        var vm = new ClassBlock.App.ClassBlockViewModel();

        Assert.True(vm.IsClassListEmpty);
        Assert.Equal("还没有总表", vm.ClassEmptyText);
        Assert.Equal("导入总表后，这里会出现各班。", vm.ClassEmptyHint);
        Assert.False(vm.HasSearchText);

        vm.LoadMaster(
            MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx")),
            "master.xlsx");

        Assert.False(vm.IsClassListEmpty);
        Assert.False(vm.HasSearchText);

        vm.SearchText = "不存在的班";
        Assert.True(vm.IsClassListEmpty);
        Assert.True(vm.HasSearchText);
        Assert.Equal("没有匹配的班级", vm.ClassEmptyText);
        Assert.Equal("换个关键词试试，或清除搜索。", vm.ClassEmptyHint);

        vm.SearchText = string.Empty;
        Assert.False(vm.IsClassListEmpty);
        Assert.False(vm.HasSearchText);
    }

    // ------------------------------------------------------------------
    // 打印失败拆两报（P3-23）与 Report 接网关（P2-13）——窗口代码无头不可实例化，
    // 用源码形态断言钉住：两段 try 各自独立、Report 走 ErrorText.Describe。

    // ------------------------------------------------------------------
    // 周六 / 周日列（P3-28）：只出五天课表，但必须如实告警。

    [Fact]
    public void MasterImporter_SixDayTable_WarnsWeekendColumnsIgnored()
    {
        var path = ClassBlockFixtures.CreateMasterWorkbook(_root, "weekend.xlsx");
        using (var workbook = new ClosedXML.Excel.XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("2-课程表");
            // 在第 6 天（第 52 列起）放一个「星期六」表头，与真实总表把周六排进来的写法一致。
            sheet.Range(sheet.Cell(2, 52), sheet.Cell(2, 61)).Merge();
            sheet.Cell(2, 52).Value = "星期六";
            for (var period = 0; period < 7; period++)
                sheet.Cell(4, 52 + period).Value = period + 1;
            sheet.Cell(5, 52).Value = "语文张兰";
            workbook.Save();
        }

        var master = MasterScheduleImporter.Import(path);

        Assert.Equal(5, master.Classes[0].Days.Count);   // 仍然只出五天
        Assert.Contains(master.Warnings, w => w.Contains("周六") && w.Contains("已忽略"));
    }

    [Fact]
    public void MasterImporter_FiveDayTable_NoWeekendWarning()
    {
        var master = MasterScheduleImporter.Import(ClassBlockFixtures.CreateMasterWorkbook(_root, "master.xlsx"));

        Assert.DoesNotContain(master.Warnings, w => w.Contains("周六") || w.Contains("周日"));
    }

    [Fact]
    public void ClassBlockWindow_PrintSeparatesRenderFromOpenFailure()
    {
        var source = RepoRoot.Read(Path.Combine("src", "ClassBlock.App", "ClassBlockWindow.axaml.cs"));

        // 唤起失败必须说「已生成」并给出路径；旧实现把两件事并进一个 try，
        // 一律报「生成打印文件失败」，老师重试永远失败也拿不到文件。
        Assert.Contains("课程表 PDF 已生成，但没有找到能打开它的程序", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception openEx)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassBlockWindow_ReportUsesErrorTextGateway()
    {
        var source = RepoRoot.Read(Path.Combine("src", "ClassBlock.App", "ClassBlockWindow.axaml.cs"));

        Assert.Contains("ErrorText.Describe(ex, action)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$" + "\"{prefix}：{ex.Message}\"", source, StringComparison.Ordinal);
    }
}
