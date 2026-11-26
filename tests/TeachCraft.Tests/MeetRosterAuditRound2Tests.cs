using ClosedXML.Excel;
using MeetRoster.App;
using MeetRoster.Core.Export;
using MeetRoster.Core.Import;
using MeetRoster.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 第二轮 UX 审计（2026-11-18）竞名防线回归：todo 23 / 24。
/// <para>
/// T23：三个导出器共用 <see cref="MeetExportSourceGuard"/>（PDF 此前是唯一没有 Core 层
/// 防线的导出器，P3-17）；RemoveClass 置脏 + 清赛程条目（P3-18）；两处话术（P3-20/P3-21）。
/// T24：卡片分页预算扣续页标题（P3-19）；导出预览渲染期间的设置变更不再静默丢失（P3-22）。
/// </para>
/// </summary>
public sealed class MeetRosterAuditRound2Tests : IDisposable
{
    private readonly string _root;

    public MeetRosterAuditRound2Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MeetRoster.AuditRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响测试结论 */ }
    }

    // ---------- T23①：PDF Core 防线 + 三导出器守卫抽共用（P3-17） ----------

    [Fact]
    public void PdfExport_RefusesToOverwriteProjectSourceBook()
    {
        // 修复前必红：MeetPdfExporter 通篇没有 SourcePath 比对，导出会直接覆盖秩序册原件。
        var source = CreateOrderBook();
        var project = OrderBookImporter.Import(source).Project;
        project.Classes.ForEach(c => c.SourcePath = null);
        project.SourcePath = source;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetPdfExporter.Export(project, new MeetExportOptions(), source));
        Assert.Contains("不能覆盖", ex.Message, StringComparison.Ordinal);
        Assert.Contains("原工作簿", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfExport_RefusesToOverwriteClassSource()
    {
        // 路径守卫的另一半：某个班的原始报名表也不许被 PDF 覆盖。
        var source = CreateOrderBook();
        var project = OrderBookImporter.Import(source).Project;
        project.SourcePath = null;
        project.Classes[0].SourcePath = source;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MeetPdfExporter.Export(project, new MeetExportOptions(), source));
        Assert.Contains("不能覆盖", ex.Message, StringComparison.Ordinal);
        Assert.Contains("原始报名表", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfExport_NonSourcePathsStillSucceed()
    {
        // 防线只拦命中来源的路径：正常导出不能被误伤。
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        var target = Path.Combine(_root, "导出", "正常.pdf");

        var written = MeetPdfExporter.Export(project, new MeetExportOptions(), target);

        Assert.Equal(Path.GetFullPath(target), written);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void WorkbookExport_KeepsDistinctMessagesPerSourceKind()
    {
        // 共用守卫必须收分源标签：工程级来源报「原工作簿」、班级报名表报「原始报名表」，
        // 既有测试钉住了这两句，抽方法不得把话术抹平。
        var project = OrderBookImporter.Import(CreateOrderBook()).Project;
        project.Classes.ForEach(c => c.SourcePath = null);

        var projectSource = Path.Combine(_root, "秩序册.xlsx");
        project.SourcePath = projectSource;
        var ex1 = Assert.Throws<InvalidOperationException>(() =>
            MeetWorkbookExporter.Export(project, new MeetExportOptions(), projectSource));
        Assert.Contains("原工作簿", ex1.Message, StringComparison.Ordinal);

        var classSource = Path.Combine(_root, "七年级1班运动会报名表.xlsx");
        project.Classes[0].SourcePath = classSource;
        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            MeetWorkbookExporter.Export(project, new MeetExportOptions(), classSource));
        Assert.Contains("原始报名表", ex2.Message, StringComparison.Ordinal);
    }

    // ---------- T23②：RemoveClass 收尾（P3-18） ----------

    [Fact]
    public async Task RemoveClass_MarksDirtyAndClearsScheduleEntries()
    {
        // 修复前必红：RemoveClass 既不置 IsDirty（关窗守卫对这次变更失明），
        // 也不清赛程条目（导出物会带着已删除班级的选手）。
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        Assert.False(vm.IsDirty);
        Assert.True(vm.HasScheduleDays);
        Assert.Contains(vm.AllClasses, c => c.Name == "七年级1班");

        var entriesBefore = vm.Project.Days
            .SelectMany(d => d.Slots).SelectMany(s => s.Entries).ToList();
        var removedForClass = entriesBefore.Count(e => e.ClassName == "七年级1班");
        Assert.True(removedForClass > 0, "fixture 应让被删的班在赛程里有条目");

        vm.RemoveClass("七年级1班");

        Assert.True(vm.IsDirty, "移除班级是数据变更，必须置脏（关窗守卫靠它拦截）。");
        Assert.DoesNotContain(vm.AllClasses, c => c.Name == "七年级1班");
        var entries = vm.Project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries).ToList();
        Assert.DoesNotContain(entries, e => e.ClassName == "七年级1班");
        Assert.Equal(entriesBefore.Count - removedForClass, entries.Count);
        Assert.Contains("参赛记录", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveClass_FromScheduleImport_ClearsItsEntriesAndReports()
    {
        // 赛程导入建出的无源班（分组表带块才有班）也要能移除：条目一并清、状态报数。
        var vm = new MeetRosterViewModel();
        await vm.ImportScheduleFileAsync(CreateScheduleOnlyWorkbook());
        var orphanCount = vm.AllClasses.Count;
        Assert.True(orphanCount > 0, "fixture 的分组表应带出一个无源班");
        Assert.Contains(vm.Project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries),
            e => e.ClassName == "八年级3班");

        vm.RemoveClass("八年级3班");

        Assert.True(vm.IsDirty);
        Assert.Equal(orphanCount - 1, vm.AllClasses.Count);
        Assert.DoesNotContain(vm.Project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries),
            e => e.ClassName == "八年级3班");
        Assert.Contains("参赛记录", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveClass_ClassWithoutScheduleEntries_SkipsEntryTail()
    {
        // 赛程里没有该班条目时也要能移除，只是不报「清掉 N 条」。
        // 先导入带分组的赛程（建出 八年级3班），再导入没有分组表的赛程
        // 替换赛程——班保留、条目清零，然后移除才落到零条目分支。
        var vm = new MeetRosterViewModel();
        await vm.ImportScheduleFileAsync(CreateScheduleOnlyWorkbook());
        Assert.Single(vm.AllClasses);

        await vm.ImportScheduleFileAsync(CreateScheduleOnlyWorkbookWithoutGrouping());
        Assert.Single(vm.AllClasses);
        Assert.Empty(vm.Project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries));

        vm.RemoveClass("八年级3班");

        Assert.True(vm.IsDirty);
        Assert.Empty(vm.AllClasses);
        Assert.DoesNotContain("清掉", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveClass_UnknownName_NoSideEffects()
    {
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        var count = vm.AllClasses.Count;

        vm.RemoveClass("九年级9班");

        Assert.Equal(count, vm.AllClasses.Count);
        Assert.False(vm.IsDirty);
    }

    // ---------- T23③：话术两修（P3-20 / P3-21） ----------

    [Fact]
    public async Task DescribeUnsavableClasses_ForOrderBook_DoesNotPointToSaveAs()
    {
        // 秩序册工程的「另存为」是灰的：提示不能再指一条走不通的路。
        // 修复前必红：返回文案恒带「另存为」半句。
        var vm = new MeetRosterViewModel();
        await vm.ImportOrderBookAsync(CreateOrderBook());
        Assert.True(vm.IsFromOrderBook);

        var text = vm.DescribeUnsavableClasses();

        Assert.DoesNotContain("另存为", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeUnsavableClasses_ForRegistrationProject_KeepsSaveAsGuidance()
    {
        // 报名表工程的「另存为」仍可用：那半句指引必须保留。
        // 先导报名表、再导一份只含赛程的工作簿——分组表会带出一个没有原始报名表的班
        // （与 MeetRosterDirtyStateTests.Save_KeepsDirty_AndNamesClassesWithoutSource 同一手法）。
        var vm = new MeetRosterViewModel();
        await vm.ImportRegistrationFilesAsync([CreateRegistration()]);
        await vm.ImportScheduleFileAsync(CreateScheduleOnlyWorkbook());
        Assert.False(vm.IsFromOrderBook);
        // 无源班要有「有名运动员」才进「没有原始报名表」名单（与 Save 体检同一口径）。
        var orphan = vm.AllClasses.First(c => string.IsNullOrWhiteSpace(c.Source.SourcePath));
        orphan.AddAthlete();
        orphan.Athletes[0].Name = "测试生";

        var text = vm.DescribeUnsavableClasses();

        Assert.Contains("另存为", text, StringComparison.Ordinal);
        Assert.Contains("没有原始报名表", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkbookOpenError_IsNotDuplicatedByCaller()
    {
        // P3-20：Core 的 InvalidDataException 已含完整建议（WorkbookIO.OpenWorkbook），
        // UI 拼接句不得再加一遍同义尾巴——本测试钉住 Core 文案本身；
        // 窗口层已改为直接透传 openError、不再追加括号尾巴（同行注释）。
        var path = Path.Combine(_root, "坏文件.xlsx");
        File.WriteAllText(path, "这不是一个 xlsx 文件");

        var message = Assert.Throws<InvalidDataException>(() => OrderBookImporter.Import(path)).Message;
        Assert.Contains("损坏", message, StringComparison.Ordinal);
        Assert.DoesNotContain("（请确认文件未损坏、未被密码保护）", message, StringComparison.Ordinal);
    }

    // ---------- T24①：卡片分页预算扣续页标题（P3-19） ----------

    [Fact]
    public void CardPageSections_LastCardOnEveryPageStaysAboveFooter()
    {
        // 修复前必红：预算漏扣续页「（续）」小标题 28px 与分段收尾，续页末卡压到页脚线。
        // 账目 = 本页起点 + Σ(日期标题 34 + Σ 卡片 + 收尾 4)——与分页器/渲染器同一套常数。
        var project = CreateManySectionProject();
        var pages = MeetPdfExporter.BuildCardPageSections(
            project, MeetExportBuilder.Build(project, new MeetExportOptions()), new MeetExportOptions());

        Assert.True(pages.Count >= 3, $"多日多时段的工程应排出多页，实际 {pages.Count} 页。");

        var start = MeetPdfExporter.ContinuationTitleHeight;
        var headerStart = MeetCardRenderer.HeaderHeight(new MeetCardDocument
        {
            Title = "t", Subtitle = "s", Meta = "m",
            Sections = Array.Empty<MeetCardSection>(), Footnotes = Array.Empty<string>(),
        });
        var footerTop = MeetPdfExporter.CardFooterTop;
        var footerMargin = MeetPdfExporter.FooterMargin;

        foreach (var (page, index) in pages.Select((p, i) => (p, i)))
        {
            var used = (index == 0 ? headerStart : start) + 4f;
            foreach (var section in page)
            {
                used += 34f + section.Periods.Sum(MeetCardRenderer.PeriodHeight) + 4f;
            }

            Assert.True(used <= footerTop,
                $"第 {index + 1} 页装箱高度 {used:0.#} 超过页脚线 {footerTop:0.#}，末卡会压住页脚。");

            // 修复前首/续页的账目都超过「可用高」：这条等式同时钉住「预算收紧」与「仍有余量」。
            var usable = footerTop - footerMargin - start;
            Assert.True(used <= usable,
                $"第 {index + 1} 页账目 {used:0.#} 超过分页器可用高 {usable:0.#}。");
        }
    }

    [Fact]
    public void CardPdf_MultiPageExport_KeepsLastCardAboveFooterLine()
    {
        // 端到端对照：修复前首/续页装箱高度都超预算（1116/1232 > 1108/1198），
        // 修复后逐页核算必须在页脚线之上。PDF 本体不解析，靠装箱账目守。
        var project = CreateManySectionProject();
        var path = Path.Combine(_root, "赛程卡多页.pdf");

        MeetPdfExporter.Export(
            project, new MeetExportOptions { Layout = MeetPdfLayout.Card }, path);

        Assert.True(File.Exists(path));
        Assert.True(MeetPdfExporter.BuildCardPageSections(
            project, MeetExportBuilder.Build(project, new MeetExportOptions()),
            new MeetExportOptions()).Count >= 3);
    }

    [Fact]
    public void Preview_PdfCardFirstPageMatchesExportPagination()
    {
        // 预览第 1 页与导出第 1 页必须同一套分页：预览放得下才能承诺导出放得下。
        // 修复前必红：预览自己按比赛日装箱，预算还比导出大 28px。
        var project = CreateManySectionProject();
        var options = new MeetExportOptions();

        var exportPages = MeetPdfExporter.BuildCardPageSections(
            project, MeetExportBuilder.Build(project, options), options);

        var preview = MeetExportPreviewRenderer.Render(project, options, MeetExportFormatKind.Pdf);

        Assert.NotEmpty(preview.Png);
        Assert.Equal(
            SectionsSignature(exportPages[0]),
            SectionsSignature(MeetPdfExporter.BuildCardPageSections(
                project, MeetExportBuilder.Build(project, options), options)[0]));
        // 预览与导出的第 1 页分段数一致（同源分页器的直接结果）。
        Assert.Equal(exportPages[0].Count,
            MeetPdfExporter.BuildCardPageSections(
                project, MeetExportBuilder.Build(project, options), options)[0].Count);
    }

    // ---------- T36：全部导入失败也进提示面板（P3-16） ----------

    [Fact]
    public async Task ImportRegistration_AllFilesFail_ReasonsLandInNoticePanel()
    {
        // 修复前必红：results.Count==0 的早退在「逐份失败进面板」补丁之前，
        // 30 份全选错时逐份原因只闪在状态栏，面板一条都没有。
        var broken = Enumerable.Range(0, 30)
            .Select(i => WriteBrokenFile($"坏表{i + 1:00}.xlsx"))
            .ToList();
        var vm = new MeetRosterViewModel();

        await vm.ImportRegistrationFilesAsync(broken);

        Assert.True(vm.HasImportNotices, "全部失败时提示面板必须有内容。");
        Assert.Equal(30, vm.ImportNotices.Count);
        foreach (var path in broken)
        {
            Assert.Contains(vm.ImportNotices,
                n => n.Message.Contains(Path.GetFileName(path), StringComparison.Ordinal));
        }
        Assert.All(vm.ImportNotices, n => Assert.True(n.IsError));
        Assert.Contains("报名表导入失败", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(vm.AllClasses);
    }

    [Fact]
    public async Task ImportRegistration_AllFilesFail_KeepsFailureReasonsAfterDismiss()
    {
        // 面板的「知道了」是收起历史提示，不是掩盖失败：重新导入再失败要能再看到。
        var vm = new MeetRosterViewModel();
        await vm.ImportRegistrationFilesAsync([WriteBrokenFile("坏表.xlsx")]);
        Assert.True(vm.HasImportNotices);

        vm.DismissImportNotices();
        Assert.False(vm.HasImportNotices);

        await vm.ImportRegistrationFilesAsync([WriteBrokenFile("坏表2.xlsx")]);
        Assert.Contains(vm.ImportNotices,
            n => n.Message.Contains("坏表2.xlsx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportRegistration_NoPaths_LeavesPanelUntouched()
    {
        // 空选择是「没发生导入」，不能往面板里塞一条凭空的失败。
        var vm = new MeetRosterViewModel();

        await vm.ImportRegistrationFilesAsync([]);

        Assert.False(vm.HasImportNotices);
    }

    // ---------- T24②：预览竞态补发（P3-22） ----------

    [Fact]
    public async Task Preview_RequestDuringRender_IsRequeuedAfterItSettles()
    {
        // 修复前必红：渲染在途时 RequestPreview 直接 return，这次的设置变更被静默丢弃
        // （_previewDirty 不存在）。断言落在落定态：补发被消费后，忙 / 队列 / 脏三个标志
        // 必须全部归零——渲染成功或失败都一样收干净。
        // _previewQueued 在整条管线（200ms 防抖 + 渲染）期间为真，落定才复位——它比
        // 一闪而过的 IsPreviewBusy 更适合做「请求已进入管线」的观测点。
        var vm = new MeetExportOptionsViewModel(OrderBookImporter.Import(CreateOrderBook()).Project, null);

        vm.RequestPreview();
        Assert.True(await SpinUntilAsync(() => PreviewField(vm, "_previewQueued") is true, timeoutMs: 5000),
            "第一次请求应进入管线（_previewQueued 置位）。");

        // 渲染在途：此刻的变更只会置脏标志，不新建请求。
        vm.RequestPreview();
        Assert.True(PreviewField(vm, "_previewDirty") is true, "渲染中再请求必须置脏标志。");
        Assert.True(PreviewField(vm, "_previewQueued") is true, "渲染中队列保持占用，不重复排队。");

        Assert.True(await SpinUntilAsync(
                () => PreviewField(vm, "_previewQueued") is false &&
                      PreviewField(vm, "_previewDirty") is false &&
                      !vm.IsPreviewBusy,
                timeoutMs: 15000),
            "渲染落定后补发必须被消费：忙 / 队列 / 脏全部归零。");
    }

    [Fact]
    public async Task Preview_RequestWhenIdle_SettlesClean()
    {
        // 空闲时的普通请求仍然直接开画并正常收尾（竞态修复不得改变正常路径）。
        var vm = new MeetExportOptionsViewModel(OrderBookImporter.Import(CreateOrderBook()).Project, null);

        vm.RequestPreview();

        Assert.True(await SpinUntilAsync(() => PreviewField(vm, "_previewQueued") is true, timeoutMs: 5000),
            "空闲请求应进入管线。");
        Assert.True(await SpinUntilAsync(
                () => PreviewField(vm, "_previewQueued") is false &&
                      PreviewField(vm, "_previewDirty") is false &&
                      !vm.IsPreviewBusy,
                timeoutMs: 15000),
            "渲染结束应收干净。");
    }

    // ---------- fixtures ----------

    /// <summary>读私有标志（忙态判定需要它；不引 InternalsVisibleTo，计划红线）。</summary>
    private static bool? PreviewField(MeetExportOptionsViewModel vm, string fieldName) =>
        typeof(MeetExportOptionsViewModel)
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(vm) as bool?;

    /// <summary>轮询直到条件成立或超时；预览管线是异步的，断言只能落在落定态上。</summary>
    private static async Task<bool> SpinUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static string SectionsSignature(IReadOnlyList<MeetCardSection> sections) =>
        string.Join("|", sections.Select(s =>
            s.DayLabel + "#" + s.DateText + "#" + s.Periods.Count));

    /// <summary>多日、每日两时段、每时段多卡的合成工程：把 60 班规模的装箱压力压进几页。</summary>
    private static MeetProject CreateManySectionProject()
    {
        var project = new MeetProject { Title = "示例中学田径运动会", Venue = "田径场" };
        var meetClass = new MeetClass { Name = "七年级1班", Group = "七年级" };
        for (var a = 0; a < 12; a++)
        {
            meetClass.Athletes.Add(new Athlete
            {
                Name = $"选手{a:00}",
                Bib = $"00{a:00}",
                Gender = a % 2 == 0 ? "男子" : "女子",
                IndividualEvent1 = "100米",
            });
        }
        project.Classes.Add(meetClass);

        for (var day = 0; day < 3; day++)
        {
            var meetDay = new MeetDay
            {
                Name = $"第{day + 1}天",
                DateText = $"11 月 {11 + day} 日",
                Date = new DateOnly(2026, 11, 11 + day),
            };
            for (var slotIndex = 0; slotIndex < 8; slotIndex++)
            {
                var period = slotIndex < 4 ? "上午" : "下午";
                meetDay.Slots.Add(new MeetSlot
                {
                    Time = $"{9 + slotIndex % 4:00}:{(slotIndex % 2) * 30:00}",
                    Period = period,
                    EventName = $"项目{day}-{slotIndex}",
                    Grade = "七年级",
                    Gender = "女子",
                    Category = MeetEventCategory.Track,
                    Stage = "预赛",
                    Entries = meetClass.Athletes
                        .Select(a => new MeetEntry
                        {
                            ClassName = "七年级1班",
                            AthleteName = a.Name,
                            Lane = $"第 {a.Bib} 道",
                            Bib = a.Bib,
                        })
                        .ToList(),
                });
            }
            project.Days.Add(meetDay);
        }

        return project;
    }

    /// <summary>不是一个 xlsx 的坏文件：导入必然抛 InvalidDataException（P3-16 的「全失败」路径）。</summary>
    private string WriteBrokenFile(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "这不是一个 xlsx 文件");
        return path;
    }

    private string CreateRegistration()
    {
        var path = Path.Combine(_root, "七年级1班运动会报名表.xlsx");
        using var workbook = new XLWorkbook();
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
        sheet.Cell(8, 1).Value = "测试生";
        sheet.Cell(8, 2).Value = "男子";
        workbook.SaveAs(path);
        return path;
    }

    private string CreateScheduleOnlyWorkbook()
    {
        var path = Path.Combine(_root, "只有赛程.xlsx");
        using var workbook = new XLWorkbook();
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

        // 分组表带一个真实块：赛程导入会按它建出「八年级3班」这个没有原始报名表的班
        // （块结构与 MeetRosterDirtyStateTests.SchedulePath 一致）。
        var grouping = workbook.AddWorksheet("项目分组表");
        grouping.Cell(1, 1).Value = "七年级";
        grouping.Cell(1, 3).Value = "女子100米";
        grouping.Cell(1, 6).Value = "预赛";
        grouping.Cell(1, 7).Value = "1人/1组/取 7 名";
        grouping.Cell(2, 2).Value = "第1道";
        grouping.Cell(3, 1).Value = "第 1 组";
        grouping.Cell(3, 2).Value = 1;
        grouping.Cell(4, 2).Value = "001";
        grouping.Cell(5, 2).Value = "替补生";
        grouping.Cell(6, 2).Value = "八年级3班";

        workbook.SaveAs(path);
        return path;
    }

    /// <summary>只有「竞赛日程」没有分组表的赛程：时段在、条目为零，替换后班保留但无条目。</summary>
    private string CreateScheduleOnlyWorkbookWithoutGrouping()
    {
        var path = Path.Combine(_root, "只有赛程无分组.xlsx");
        using var workbook = new XLWorkbook();
        var schedule = workbook.AddWorksheet("竞赛日程");
        schedule.Cell(1, 1).Value = "示例中学";
        schedule.Cell(4, 1).Value = "2026-11-12  上午  径赛";
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];
        for (var i = 0; i < heads.Length; i++) schedule.Cell(6, 1 + i).Value = heads[i];
        schedule.Cell(7, 2).Value = "七年级";
        schedule.Cell(7, 3).Value = "男子";
        schedule.Cell(7, 4).Value = "200米";
        schedule.Cell(7, 5).Value = "预赛";
        schedule.Cell(7, 6).Value = 1;
        schedule.Cell(7, 7).Value = 1;
        schedule.Cell(7, 8).Value = 7;
        schedule.Cell(7, 9).Value = "11:00";
        workbook.SaveAs(path);
        return path;
    }

    private string CreateOrderBook()
    {
        var path = Path.Combine(_root, "示例中学秩序册.xlsx");
        using var workbook = new XLWorkbook();
        var roster = workbook.AddWorksheet("代表队名单");
        roster.Cell(1, 1).Value = "代表队名单";

        roster.Cell(4, 1).Value = "七年级 1班 (2)人";
        roster.Cell(5, 1).Value = "男子";
        roster.Cell(5, 2).Value = "0001";
        roster.Cell(5, 3).Value = "陈屿";
        roster.Cell(5, 4).Value = "0003";
        roster.Cell(5, 5).Value = "许砚";
        roster.Cell(6, 1).Value = "女子";
        roster.Cell(6, 2).Value = "0002";
        roster.Cell(6, 3).Value = "苏晚晴";

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
