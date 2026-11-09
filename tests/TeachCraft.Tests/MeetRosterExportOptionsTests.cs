using MeetRoster.App;
using MeetRoster.Core.Export;
using MeetRoster.Core.Import;

namespace TeachCraft.Tests;

/// <summary>
/// 竞名「导出设置」与「赛程仅显示该班选手」的视图模型回归。
/// 只构造视图模型与临时工作簿，不初始化 Avalonia 窗口。
/// </summary>
public sealed class MeetRosterExportOptionsTests : IDisposable
{
    private readonly string _root;

    public MeetRosterExportOptionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MeetRoster.ExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响测试结论 */ }
    }

    // ---------- 导出设置 ----------

    [Fact]
    public void ExportOptions_ListsAllClassesPlusOnePerClass()
    {
        var project = BuildProject();

        var vm = new MeetExportOptionsViewModel(project, "七年级2班");

        Assert.Equal(3, vm.Targets.Count); // 全部 + 2 个班
        Assert.True(vm.Targets[0].IsAllClasses);
        Assert.Equal("七年级2班", vm.SelectedTarget.ClassName);
        Assert.True(vm.IsSingleClass);
        Assert.True(vm.CanHighlight);
    }

    [Fact]
    public void ExportOptions_AllClassesDisablesHighlight()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), null);

        Assert.True(vm.SelectedTarget.IsAllClasses);
        Assert.False(vm.CanHighlight);

        var request = Build(vm);
        Assert.False(request.Options.HighlightClass);
        Assert.True(request.Options.IsAllClasses);
    }

    [Fact]
    public void ExportOptions_FileNameFollowsTargetAndFormat()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");
        Assert.Equal("七年级1班参赛手册.xlsx", vm.FileName);

        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Pdf);
        Assert.Equal("七年级1班参赛手册.pdf", vm.FileName);

        vm.SelectedTarget = vm.Targets[0];
        Assert.EndsWith("·全部班级.pdf", vm.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportOptions_RejectsWhenNothingSelected()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), null)
        {
            IncludeRoster = false,
            IncludeEvents = false,
            IncludeSchedule = false,
            IncludeClassSummary = false,
        };

        Assert.False(vm.TryBuild(out _, out var error));
        Assert.Contains("至少勾选", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportOptions_SummaryReflectsCheckedPages()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班")
        {
            IncludeEvents = false,
            IncludeClassSummary = false,
        };

        Assert.Contains("报名名单", vm.ExportSummary, StringComparison.Ordinal);
        Assert.Contains("参赛日程", vm.ExportSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("单项报名", vm.ExportSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportOptions_RejectsEmptyProject()
    {
        var vm = new MeetExportOptionsViewModel(new MeetRoster.Core.Models.MeetProject(), null);

        Assert.False(vm.TryBuild(out _, out var error));
        Assert.Contains("还没有导入", error, StringComparison.Ordinal);
    }

    // ---------- 赛程「全部」比赛日 ----------

    [Fact]
    public async Task ViewModel_AllDaysItemIsFirstAndCountsOnlyRealDays()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());

        // 这份 fixture 的比赛日程都在同一天：「全部」+ 1 个真实比赛日。
        Assert.Equal(2, vm.ScheduleDays.Count);
        Assert.True(vm.ScheduleDays[0].IsAllDays);
        Assert.False(vm.ScheduleDays[1].IsAllDays);
        Assert.Equal("全部", vm.ScheduleDays[0].Name);
        // 徽标只数真实比赛日，不含合成项。
        Assert.Equal("01", vm.ScheduleDayCountLabel);
    }

    [Fact]
    public async Task ViewModel_AllDaysShowsEveryDayWithDayLabels()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateTwoDayOrderBook());

        vm.SelectSection("schedule");
        vm.SelectDay(vm.ScheduleDays.First(d => d.IsAllDays));

        Assert.Equal("全部比赛日", vm.ScheduleDayTitle);
        // 每天第一组带日期标签，其余组不带；两天都有标签。
        var labels = vm.ScheduleGroups.Where(g => g.ShowDayLabel).Select(g => g.DayLabel).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, l => l.Contains("第一天", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("第二天", StringComparison.Ordinal));

        // 两天全部时段的条目都在，不只第一天。
        var entries = vm.ScheduleGroups.SelectMany(g => g.Slots).SelectMany(s => s.Entries).ToList();
        Assert.Contains(entries, e => e.AthleteName == "苏晚晴");
        Assert.Contains(entries, e => e.AthleteName == "林见夏");
        Assert.StartsWith("2 天 ·", vm.ScheduleDaySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_SingleDayStillWorksAfterAllDaysAdded()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateTwoDayOrderBook());

        vm.SelectSection("schedule");
        var secondDay = vm.ScheduleDays.Single(d => !d.IsAllDays && d.Name == "第二天");
        vm.SelectDay(secondDay);

        Assert.StartsWith("第二天", vm.ScheduleDayTitle, StringComparison.Ordinal);
        Assert.All(vm.ScheduleGroups, g => Assert.False(g.ShowDayLabel));
        Assert.DoesNotContain(
            vm.ScheduleGroups.SelectMany(g => g.Slots).SelectMany(s => s.Entries),
            e => e.AthleteName == "苏晚晴");
    }

    [Fact]
    public async Task ViewModel_AllDaysWithOnlyClassKeepsFilter()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateTwoDayOrderBook());

        vm.SelectSection("schedule");
        vm.SelectDay(vm.ScheduleDays.First(d => d.IsAllDays));
        vm.ScheduleHighlightClass = vm.AllClasses.First(c => c.Name == "七年级2班");
        vm.OnlyHighlightedClassEntries = true;

        var entries = vm.ScheduleGroups.SelectMany(g => g.Slots).SelectMany(s => s.Entries).ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal("七年级2班", e.ClassName));
    }

    // ---------- 赛程「仅显示该班」 ----------

    [Fact]
    public async Task ViewModel_OnlyClassEntries_HidesOtherClasses()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        vm.SelectSection("schedule");

        var all = vm.ScheduleGroups.Sum(g => g.Slots.Count);
        Assert.True(all > 0);

        vm.ScheduleHighlightClass = vm.AllClasses.First(c => c.Name == "七年级1班");
        vm.OnlyHighlightedClassEntries = true;

        var visibleEntries = vm.ScheduleGroups
            .SelectMany(g => g.Slots)
            .SelectMany(s => s.Entries)
            .ToList();

        Assert.NotEmpty(visibleEntries);
        Assert.All(visibleEntries, e => Assert.Equal("七年级1班", e.ClassName));
    }

    [Fact]
    public async Task ViewModel_ClearingHighlight_AlsoClearsOnlyClassFilter()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());

        vm.ScheduleHighlightClass = vm.AllClasses.First();
        vm.OnlyHighlightedClassEntries = true;

        vm.ClearScheduleHighlight();

        Assert.False(vm.OnlyHighlightedClassEntries);
        Assert.False(vm.HasScheduleHighlight);
    }

    [Fact]
    public async Task ViewModel_OrderBookImport_IsNotDirtyAndBlocksOverwritingSource()
    {
        var vm = new MeetRosterViewModel();
        var path = CreateOrderBook();

        await vm.ImportOrderBookAsync(path);

        Assert.False(vm.IsDirty);
        Assert.True(vm.IsFromOrderBook);
        Assert.True(vm.CanExportHandBook);
        Assert.True(vm.IsExportPathBlocked(path));
        Assert.False(vm.IsExportPathBlocked(Path.Combine(_root, "别处.xlsx")));
    }

    [Fact]
    public async Task ViewModel_ExportHandBook_WritesFileToGivenPath()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());

        var request = Build(new MeetExportOptionsViewModel(vm.Project, "七年级1班"));
        var target = Path.Combine(_root, "导出", request.FileName);

        await vm.ExportHandBookAsync(request, target);

        Assert.True(File.Exists(target), vm.StatusText);
        Assert.Contains("已导出", vm.StatusText, StringComparison.Ordinal);
    }

    // ---------- fixtures ----------

    private static MeetExportRequest Build(MeetExportOptionsViewModel vm)
    {
        Assert.True(vm.TryBuild(out var request, out var error), error);
        return request;
    }

    private MeetRoster.Core.Models.MeetProject BuildProject() =>
        OrderBookImporter.Import(CreateOrderBook()).Project;

    /// <summary>两个比赛日：第一天 女子100米（1班 苏晚晴 / 2班 林见夏），第二天 男子200米（2班 沈观澜）。</summary>
    private string CreateTwoDayOrderBook()
    {
        var path = Path.Combine(_root, "示例中学两日秩序册.xlsx");
        using var workbook = new ClosedXML.Excel.XLWorkbook();

        var roster = workbook.AddWorksheet("代表队名单");
        roster.Cell(1, 1).Value = "代表队名单";
        roster.Cell(4, 1).Value = "七年级 1班 (1)人";
        roster.Cell(5, 1).Value = "女子";
        roster.Cell(5, 2).Value = "0001";
        roster.Cell(5, 3).Value = "苏晚晴";
        roster.Cell(8, 1).Value = "七年级 2班 (2)人";
        roster.Cell(9, 1).Value = "男子";
        roster.Cell(9, 2).Value = "0021";
        roster.Cell(9, 3).Value = "沈观澜";
        roster.Cell(10, 1).Value = "女子";
        roster.Cell(10, 2).Value = "0022";
        roster.Cell(10, 3).Value = "林见夏";

        var schedule = workbook.AddWorksheet("竞赛日程");
        schedule.Cell(1, 1).Value = "示例中学田径运动会";
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];

        schedule.Cell(4, 1).Value = "2026-11-11  上午  径赛";
        for (var i = 0; i < heads.Length; i++) schedule.Cell(6, 1 + i).Value = heads[i];
        WriteSlotRow(schedule, 7, "女子", "100米", "预赛", 2, "11:00");

        schedule.Cell(10, 1).Value = "2026-11-12  上午  径赛";
        for (var i = 0; i < heads.Length; i++) schedule.Cell(12, 1 + i).Value = heads[i];
        WriteSlotRow(schedule, 13, "男子", "200米", "预赛", 1, "09:00");

        var grouping = workbook.AddWorksheet("项目分组表");
        WriteIndividualBlock(grouping, 1, "女子100米", "预赛",
            ("0001", "苏晚晴", "1班"), ("0022", "林见夏", "2班"));
        WriteIndividualBlock(grouping, 9, "男子200米", "预赛",
            ("0021", "沈观澜", "2班"));

        workbook.SaveAs(path);
        return path;

        static void WriteSlotRow(ClosedXML.Excel.IXLWorksheet sheet, int row, string gender,
            string eventName, string stage, int count, string time)
        {
            sheet.Cell(row, 2).Value = "七年级";
            sheet.Cell(row, 3).Value = gender;
            sheet.Cell(row, 4).Value = eventName;
            sheet.Cell(row, 5).Value = stage;
            sheet.Cell(row, 6).Value = count;
            sheet.Cell(row, 7).Value = 1;
            sheet.Cell(row, 8).Value = 7;
            sheet.Cell(row, 9).Value = time;
        }

        static void WriteIndividualBlock(ClosedXML.Excel.IXLWorksheet sheet, int startRow,
            string eventName, string stage, params (string Bib, string Name, string Class)[] athletes)
        {
            sheet.Cell(startRow, 1).Value = "七年级";
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
    }

    private string CreateOrderBook()
    {
        var path = Path.Combine(_root, "示例中学秩序册.xlsx");
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var roster = workbook.AddWorksheet("代表队名单");
        roster.Cell(1, 1).Value = "代表队名单";

        roster.Cell(4, 1).Value = "七年级 1班 (2)人";
        roster.Cell(5, 1).Value = "男子";
        roster.Cell(5, 2).Value = "0001";
        roster.Cell(5, 3).Value = "陈屿";
        roster.Cell(6, 1).Value = "女子";
        roster.Cell(6, 2).Value = "0002";
        roster.Cell(6, 3).Value = "苏晚晴";

        roster.Cell(9, 1).Value = "七年级 2班 (2)人";
        roster.Cell(10, 1).Value = "男子";
        roster.Cell(10, 2).Value = "0021";
        roster.Cell(10, 3).Value = "沈观澜";
        roster.Cell(11, 1).Value = "女子";
        roster.Cell(11, 2).Value = "0022";
        roster.Cell(11, 3).Value = "林见夏";

        var schedule = workbook.AddWorksheet("竞赛日程");
        schedule.Cell(1, 1).Value = "示例中学田径运动会";
        schedule.Cell(4, 1).Value = "2026-11-11  上午  径赛";
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];
        for (var i = 0; i < heads.Length; i++) schedule.Cell(6, 1 + i).Value = heads[i];
        schedule.Cell(7, 2).Value = "七年级";
        schedule.Cell(7, 3).Value = "女子";
        schedule.Cell(7, 4).Value = "100米";
        schedule.Cell(7, 5).Value = "预赛";
        schedule.Cell(7, 6).Value = 2;
        schedule.Cell(7, 7).Value = 1;
        schedule.Cell(7, 8).Value = 7;
        schedule.Cell(7, 9).Value = "11:00";

        var grouping = workbook.AddWorksheet("项目分组表");
        grouping.Cell(1, 1).Value = "七年级";
        grouping.Cell(1, 3).Value = "女子100米";
        grouping.Cell(1, 6).Value = "预赛";
        grouping.Cell(1, 7).Value = "2人/1组/取 7 名";
        grouping.Cell(2, 1).Value = "第 1 组";
        grouping.Cell(2, 2).Value = 1;
        grouping.Cell(2, 3).Value = 2;
        grouping.Cell(3, 2).Value = "0002";
        grouping.Cell(3, 3).Value = "0022";
        grouping.Cell(4, 2).Value = "苏晚晴";
        grouping.Cell(4, 3).Value = "林见夏";
        grouping.Cell(5, 2).Value = "1班";
        grouping.Cell(5, 3).Value = "2班";

        workbook.SaveAs(path);
        return path;
    }
}
