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

    // ---------- 团体队员：秩序册不点名，靠报名表补 ----------

    [Fact]
    public async Task TeamMembers_FilledFromRegistrationAfterOrderBookImport()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());

        // 秩序册没有团体队员 → 赛程里是占位标签。
        var relay = FindTeamSlot(vm, "4x100米接力");
        Assert.All(relay.Entries, e => Assert.False(e.TeamMembersFromRegistration));

        // 再导入一份带团体勾选的报名表 → 赛程补出真实队员。
        await vm.ImportRegistrationFilesAsync([CreateRegistrationWithTeam()]);

        var refreshed = FindTeamSlot(vm, "4x100米接力");
        var class1 = refreshed.Entries.Single(e => e.ClassName == "七年级1班");
        Assert.True(class1.TeamMembersFromRegistration);
        Assert.Equal("陈屿、许砚", class1.TeamMembers);
        Assert.Contains("团体队员", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TeamMembers_CarriedOverWhenOrderBookReimportedAfterRegistration()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        await vm.ImportRegistrationFilesAsync([CreateRegistrationWithTeam()]);

        // 再导入一次秩序册：报名表带来的团体勾选要按姓名沿用，不能又被抹成占位标签。
        await vm.ImportOrderBookAsync(CreateOrderBook());

        var relay = FindTeamSlot(vm, "4x100米接力");
        var class1 = relay.Entries.Single(e => e.ClassName == "七年级1班");
        Assert.True(class1.TeamMembersFromRegistration);
        Assert.Equal("陈屿、许砚", class1.TeamMembers);
        Assert.Contains("沿用报名表", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TeamMembers_NotInventedWhenNobodyRegistered()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());

        var relay = FindTeamSlot(vm, "4x100米接力");

        // 没人勾团体项目时，不许凭空编队员，只留占位标签。
        Assert.All(relay.Entries, entry =>
        {
            Assert.False(entry.TeamMembersFromRegistration);
            Assert.Equal(string.Empty, entry.TeamMembers);
        });
    }

    [Fact]
    public async Task Export_UsesTeamMembersInAthleteColumn()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        await vm.ImportRegistrationFilesAsync([CreateRegistrationWithTeam()]);

        var summary = MeetExportBuilder.Build(
            vm.Project, new MeetExportOptions { ClassName = "七年级1班" }).Single();

        var relay = summary.Entries.First(e => e.EventName == "4x100米接力");
        Assert.Equal("陈屿、许砚", relay.AthleteLabel);
        Assert.True(relay.TeamMembersFromRegistration);

        // 单项仍然显示本人姓名。
        var hundred = summary.Entries.First(e => e.EventName == "100米");
        Assert.Equal(hundred.AthleteName, hundred.AthleteLabel);
    }

    private static MeetRoster.Core.Models.MeetSlot FindTeamSlot(MeetRosterViewModel vm, string eventName) =>
        vm.Project.Days
            .SelectMany(d => d.Slots)
            .First(s => s.EventName == eventName && s.Entries.Count > 0);

    /// <summary>七年级1班的陈屿 / 许砚勾了 4x100 接力（团体项目1）。</summary>
    private string CreateRegistrationWithTeam()
    {
        var path = Path.Combine(_root, "七年级1班运动会报名表.xlsx");
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var sheet = workbook.AddWorksheet("导入模板");
        sheet.Cell(1, 1).Value = "示例中学田径运动会";
        sheet.Cell(2, 1).Value = "参赛单位";
        sheet.Cell(2, 2).Value = "七年级1班";
        sheet.Cell(7, 1).Value = "姓名";
        sheet.Cell(7, 2).Value = "性别";
        sheet.Cell(7, 3).Value = "团体项目1";
        sheet.Cell(7, 4).Value = "团体项目2";
        sheet.Cell(7, 5).Value = "团体项目3";
        sheet.Cell(7, 6).Value = "比赛项目1";
        sheet.Cell(7, 7).Value = "比赛项目2";

        // 与秩序册名单同名，才能按姓名对上。
        sheet.Cell(8, 1).Value = "陈屿";
        sheet.Cell(8, 2).Value = "男子";
        sheet.Cell(8, 3).Value = "4x100米接力";
        sheet.Cell(9, 1).Value = "许砚";
        sheet.Cell(9, 2).Value = "男子";
        sheet.Cell(9, 3).Value = "4x100米接力";
        workbook.SaveAs(path);
        return path;
    }

    [Fact]
    public async Task TeamMembers_ChipCarriesFullRosterForWrapping()
    {
        // 20*50 接力能报十几人：chip 必须拿到完整名单（靠界面折行），
        // 不能在 ViewModel 里截断——截断后老师看不到到底谁上场。
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        await vm.ImportRegistrationFilesAsync([CreateRegistrationWithTeam()]);

        vm.SelectSection("schedule");
        var entry = vm.ScheduleGroups
            .SelectMany(g => g.Slots)
            .SelectMany(s => s.Entries)
            .First(e => e.TeamMembersFromRegistration);

        Assert.Equal("陈屿、许砚", entry.TeamMembers);
        Assert.Contains("团体队员", entry.EntryToolTip, StringComparison.Ordinal);
        Assert.Contains("陈屿", entry.EntryToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamMembers_EntryToolTipFallsBackToAthleteForIndividual()
    {
        var entry = new MeetRoster.App.ScheduleEntryViewModel(
            new MeetRoster.Core.Models.MeetEntry
            {
                ClassName = "七年级1班",
                AthleteName = "苏晚晴",
            });

        Assert.Equal("苏晚晴", entry.EntryToolTip);
        Assert.False(entry.TeamMembersFromRegistration);
    }

    // ---------- Excel 样式 ----------

    [Fact]
    public void ExportOptions_FormatSwitchTogglesStyleAndLayoutVisibility()
    {
        // 复现用户报告的顺序：Excel → PDF（样式该消失、版式该出现）→ 碰一下样式 → 切回 Excel。
        // 旧实现漏了 ShowStyleChoice 的通知：切到 PDF 后 Excel 样式还在，
        // 点过之后才消失，切回 Excel 又再也不出现。
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        Assert.True(vm.ShowStyleChoice);
        Assert.False(vm.ShowLayoutChoice);

        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Pdf);

        Assert.Contains(nameof(vm.ShowStyleChoice), raised);
        Assert.Contains(nameof(vm.ShowLayoutChoice), raised);
        Assert.False(vm.ShowStyleChoice);   // PDF 下不该出现 Excel 样式
        Assert.True(vm.ShowLayoutChoice);

        // 切到 PDF 后再动一下版式，然后切回 Excel：样式选择必须回来。
        vm.SelectedLayout = vm.Layouts.Single(l => l.Layout == MeetPdfLayout.Card);
        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Excel);

        Assert.True(vm.ShowStyleChoice);
        Assert.False(vm.ShowLayoutChoice);
        Assert.True(vm.ContentChecksEnabled);  // 回到 Excel 后内容勾选恢复可用
    }

    [Fact]
    public void ExportOptions_ChangingStyleDoesNotHideIt()
    {
        // 点样式本身不该把样式区藏起来（旧实现的症状：点一下就消失）。
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");
        vm.SelectedStyle = vm.Styles.Single(s => s.Style == MeetExcelStyle.OriginalHighlight);

        Assert.True(vm.ShowStyleChoice);
        Assert.Contains("底色", vm.StyleHint, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportOptions_ExcelOffersTwoStyles()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");

        Assert.Equal(2, vm.Styles.Count);
        Assert.Equal(MeetExcelStyle.Workbook, vm.SelectedStyle.Style);
        Assert.True(vm.ShowStyleChoice);      // 默认 Excel，显示样式选择
        Assert.False(vm.ShowLayoutChoice);
    }

    [Fact]
    public void ExportOptions_OriginalStyleRequiresSourceWorkbook()
    {
        var project = BuildProject();          // 秩序册导入，带 SourcePath
        var vm = new MeetExportOptionsViewModel(project, "七年级1班");
        Assert.True(vm.CanUseOriginalStyle);

        vm.SelectedStyle = vm.Styles.Single(s => s.Style == MeetExcelStyle.OriginalHighlight);
        Assert.Contains("底色", vm.StyleHint, StringComparison.Ordinal);
        Assert.Equal(MeetExcelStyle.OriginalHighlight, vm.BuildOptions().ExcelStyle);

        // 原文件不在了：不可用，且 TryBuild 给出可操作提示。
        project.SourcePath = Path.Combine(_root, "已被移动.xlsx");
        Assert.False(vm.CanUseOriginalStyle);
        Assert.Contains("重新导入", vm.StyleHint, StringComparison.Ordinal);
        Assert.False(vm.TryBuild(out _, out var error));
        Assert.Contains("原表高亮", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportOptions_OriginalStyleHiddenForPdf()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");
        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Pdf);

        Assert.False(vm.ShowStyleChoice);
        Assert.True(vm.ShowLayoutChoice);
    }

    // ---------- 长任务提示 ----------

    [Fact]
    public async Task ViewModel_LongImportRaisesBusyTitleAndSettlesBack()
    {
        var vm = new MeetRosterViewModel();
        var path = CreateOrderBook();

        await vm.ImportOrderBookAsync(path);

        // 完成后必须收干净：遮罩不残留、IsBusy 归位，否则界面会一直卡着。
        Assert.False(vm.IsBusy);
        Assert.False(vm.ShowBusyOverlay);
        // 忙碌期间写过标题与说明（读操作结束不覆盖，供事后排查）。
        Assert.Contains("秩序册", vm.BusyTitle, StringComparison.Ordinal);
        Assert.Contains("本机", vm.BusyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModel_FailedImportStillClearsBusyState()
    {
        var vm = new MeetRosterViewModel();

        await vm.ImportOrderBookAsync(Path.Combine(_root, "不存在.xlsx"));

        // 失败路径也必须收尾，否则遮罩会盖在界面上再也退不掉。
        Assert.False(vm.IsBusy);
        Assert.False(vm.ShowBusyOverlay);
        Assert.Contains("找不到", vm.StatusText, StringComparison.Ordinal);
    }

    // ---------- 统一导入入口 ----------

    [Fact]
    public async Task ViewModel_ScheduleOnlyWorkbookKeepsExistingClasses()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        var classCount = vm.AllClasses.Count;
        var athleteCount = vm.AllClasses.Sum(c => c.AthleteCount);

        // 只有赛程的工作簿走赛程管线：班级与名单保持不变。
        await vm.ImportScheduleFileAsync(CreateScheduleOnlyWorkbook());

        Assert.Equal(classCount, vm.AllClasses.Count);
        Assert.Equal(athleteCount, vm.AllClasses.Sum(c => c.AthleteCount));
        Assert.True(vm.HasScheduleDays);
    }

    // ---------- PDF 版式 ----------

    [Fact]
    public void ExportOptions_PdfOffersTwoLayouts()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");

        Assert.Equal(2, vm.Layouts.Count);
        Assert.Equal(MeetPdfLayout.Handbook, vm.SelectedLayout.Layout);
        Assert.False(vm.ShowLayoutChoice); // 默认 Excel，不显示版式选择
        Assert.True(vm.ContentChecksEnabled);

        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Pdf);
        Assert.True(vm.ShowLayoutChoice);
        Assert.False(vm.IsCardLayout);
    }

    [Fact]
    public void ExportOptions_CardLayoutDisablesContentChecksAndSaysSo()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");
        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Pdf);
        vm.SelectedLayout = vm.Layouts.Single(l => l.Layout == MeetPdfLayout.Card);

        Assert.True(vm.IsCardLayout);
        Assert.False(vm.ContentChecksEnabled);
        Assert.Contains("赛程卡", vm.ExportSummary, StringComparison.Ordinal);

        var request = Build(vm);
        Assert.Equal(MeetPdfLayout.Card, request.Options.Layout);
    }

    [Fact]
    public void ExportOptions_LayoutFlowsIntoRequest()
    {
        var vm = new MeetExportOptionsViewModel(BuildProject(), "七年级1班");
        vm.SelectedFormat = vm.Formats.Single(f => f.Format == MeetExportFormat.Pdf);
        vm.SelectedLayout = vm.Layouts.Single(l => l.Layout == MeetPdfLayout.Card);

        Assert.Equal(MeetPdfLayout.Card, vm.BuildOptions().Layout);

        vm.SelectedLayout = vm.Layouts.Single(l => l.Layout == MeetPdfLayout.Handbook);
        Assert.Equal(MeetPdfLayout.Handbook, vm.BuildOptions().Layout);
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

    /// <summary>只有「竞赛日程 + 项目分组表」的工作簿（往届赛程形态，没有代表队名单）。</summary>
    private string CreateScheduleOnlyWorkbook()
    {
        var path = Path.Combine(_root, "只有赛程.xlsx");
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var schedule = workbook.AddWorksheet("竞赛日程");
        schedule.Cell(1, 1).Value = "示例中学";
        schedule.Cell(4, 1).Value = "2026-11-11  上午  径赛";
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];
        for (var i = 0; i < heads.Length; i++) schedule.Cell(6, 1 + i).Value = heads[i];
        schedule.Cell(7, 2).Value = "七年级";
        schedule.Cell(7, 3).Value = "女子";
        schedule.Cell(7, 4).Value = "100米";
        schedule.Cell(7, 5).Value = "预赛";
        schedule.Cell(7, 6).Value = 1;
        schedule.Cell(7, 7).Value = 1;
        schedule.Cell(7, 8).Value = 7;
        schedule.Cell(7, 9).Value = "11:00";
        workbook.AddWorksheet("项目分组表").Cell(1, 1).Value = "项目分组表";
        workbook.SaveAs(path);
        return path;
    }

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

        roster.Cell(4, 1).Value = "七年级 1班 (3)人";
        roster.Cell(5, 1).Value = "男子";
        roster.Cell(5, 2).Value = "0001";
        roster.Cell(5, 3).Value = "陈屿";
        roster.Cell(5, 4).Value = "0003";
        roster.Cell(5, 5).Value = "许砚";
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

        // 团体项目：日程里有 4x100 接力，分组表按班列队（不点名队员）。
        schedule.Cell(10, 1).Value = "2026-11-11  下午  径赛";
        for (var i = 0; i < heads.Length; i++) schedule.Cell(12, 1 + i).Value = heads[i];
        schedule.Cell(13, 2).Value = "七年级";
        schedule.Cell(13, 3).Value = "混合";
        schedule.Cell(13, 4).Value = "4x100米接力";
        schedule.Cell(13, 5).Value = "决赛";
        schedule.Cell(13, 6).Value = 2;
        schedule.Cell(13, 7).Value = 1;
        schedule.Cell(13, 8).Value = 7;
        schedule.Cell(13, 9).Value = "14:30";

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

        // 团体项目块：只列参赛班。
        grouping.Cell(8, 1).Value = "七年级";
        grouping.Cell(8, 3).Value = "混合4x100米接力";
        grouping.Cell(8, 6).Value = "决赛";
        grouping.Cell(8, 7).Value = "2队/1组/取 7 名";
        grouping.Cell(9, 1).Value = "第 1 组";
        grouping.Cell(9, 2).Value = 1;
        grouping.Cell(9, 3).Value = 2;
        grouping.Cell(10, 2).Value = "1班";
        grouping.Cell(10, 3).Value = "2班";

        workbook.SaveAs(path);
        return path;
    }
}
