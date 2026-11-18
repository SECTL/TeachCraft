using System.Reflection;
using GradeStrip.App.ViewModels;
using GradeStrip.Core.Comparison;
using GradeStrip.Core.Model;

namespace TeachCraft.Tests;

/// <summary>
/// 第二轮 UX 审计（2026-11-18）todo 33 的分晓区与跨应用令牌回归。
///
/// <para>分晓四条是「关窗 / 拖入」这条生命周期的边缘：
/// 连按 X 叠确认框、宿主参数误判走查、失败汇总自指、只导报表关窗不确认（P3-1/2/5）；
/// 外加报表可移除（P2-23）。令牌区两条是尺寸档与 ghost 三态（P3-33/34）。</para>
///
/// <para>为什么仍然测纯函数：分晓的导入导出都包在 <c>RunBusyAsync</c> 里，它第一件事是
/// <c>Dispatcher.UIThread.InvokeAsync</c>——无 UI 线程的测试环境会永久挂起。
/// 所以凡是需要断言的判定都抽成 public static，UI 链路交给走查参数。</para>
/// </summary>
public sealed class GradeStripP3Tests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.NonPublic | BindingFlags.Instance;

    // ------------------------------------------------------------ P3-1 走查判定

    [Fact]
    public void IsWalkthroughCommandLine_OnlyRecognisesGradeStripShot()
    {
        // 修复前必红：宿主跑一次 --seatgrid-shot，分晓同进程打开时读到宿主的命令行，
        // 关窗确认被永久跳过（审计 P3-1）。
        Assert.False(MainWindowIsWalkthrough(new[] { "TeachCraft.App.exe", "--seatgrid-shot", "out.png" }));
        Assert.False(MainWindowIsWalkthrough(new[] { "TeachCraft.App.exe", "--oobe" }));
        Assert.False(MainWindowIsWalkthrough(new[] { "TeachCraft.App.exe" }));

        Assert.True(MainWindowIsWalkthrough(new[] { "TeachCraft.App.exe", "--gradestrip-shot", "out.png" }));
        Assert.True(MainWindowIsWalkthrough(new[] { "--gradestrip-shot", "out.png" }));
    }

    [Fact]
    public void IsWalkthroughCommandLine_NullIsNotWalkthrough()
    {
        Assert.False(MainWindowIsWalkthrough(null));
    }

    [Fact]
    public void CloseGuard_ReadsWalkthroughOnceNotPerClose()
    {
        // 结构断言：OnClosing 里不得再读进程命令行。
        // 失效形态：把 IsWalkthroughCommandLine(Environment.GetCommandLineArgs()) 挪回 OnClosing。
        var source = RepoRoot.Read(Path.Combine("src", "GradeStrip.App", "Views", "MainWindow.axaml.cs"));
        var onClosing = ExtractMethod(source, "protected override void OnClosing");

        Assert.DoesNotContain("Environment.GetCommandLineArgs", onClosing, StringComparison.Ordinal);
        Assert.Contains("_isWalkthrough", onClosing, StringComparison.Ordinal);
        Assert.Contains("_confirmingClose", onClosing, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseGuard_ReentrancyGateIsResetOnCancel()
    {
        // 选择「继续编辑」后闸门必须放开，否则再点一次 X 永远关不掉窗口。
        var source = RepoRoot.Read(Path.Combine("src", "GradeStrip.App", "Views", "MainWindow.axaml.cs"));
        var onClosing = ExtractMethod(source, "protected override void OnClosing");

        var reset = onClosing.IndexOf("_confirmingClose = false", StringComparison.Ordinal);
        var discardCheck = onClosing.IndexOf("if (!discard) return", StringComparison.Ordinal);

        Assert.True(reset >= 0, "取消分支必须把 _confirmingClose 复位");
        Assert.True(discardCheck > reset, "复位必须发生在判定 discard 之前（两条路径都要复位）");
    }

    // ------------------------------------------------------------ P3-5 只导报表也要确认

    [Fact]
    public void HasUnsavedContent_IsTrueForReportOnlySession()
    {
        // 修复前必红：ShouldConfirmOnClose 只看 HasData，只导入分析报表时关窗静默丢数据。
        var vm = new MainViewModel();
        Assert.False(vm.HasUnsavedContent);

        StandaloneReportsOf(vm).Add(Report("班级成绩对比"));

        Assert.False(vm.HasData);
        Assert.True(vm.HasUnsavedContent);
    }

    [Fact]
    public void HasUnsavedContent_IsFalseAfterReportRemoved()
    {
        // 摘掉最后一份报表之后就没有可丢的东西了，关窗不该再拦。
        var vm = new MainViewModel();
        var report = Report("试题分析");
        StandaloneReportsOf(vm).Add(report);

        vm.RemoveReport(report);

        Assert.False(vm.HasUnsavedContent);
        Assert.Empty(StandaloneReportsOf(vm));
    }

    [Fact]
    public void InstallGuard_UsesSameUnsavedJudgementAsCloseGuard()
    {
        // 安装更新前的扫描（UnsavedWindowGuard）与手动关窗口径必须一致：
        // 只导报表时两边都要问一句，否则「更新」会把报表无声关掉。
        var vm = new MainViewModel();
        StandaloneReportsOf(vm).Add(Report("班级成绩对比"));

        Assert.True(TeachCraft.App.Updating.UnsavedWindowGuard.HasUnsavedWork(vm));
    }

    // ------------------------------------------------------------ P2-23 报表可移除

    [Fact]
    public void RemoveReport_RemovesStandaloneAndReportsStatus()
    {
        var vm = new MainViewModel();
        var first = Report("班级成绩对比");
        var second = Report("试题分析");
        StandaloneReportsOf(vm).Add(first);
        StandaloneReportsOf(vm).Add(second);

        vm.RemoveReport(first);

        Assert.Single(StandaloneReportsOf(vm));
        Assert.Same(second, StandaloneReportsOf(vm)[0]);
        Assert.Equal("已移除报表，可从概览重新挂载", vm.Status);
    }

    [Fact]
    public void RemoveReport_RemovesFromActiveSessionNotOtherSession()
    {
        // 报表挂在会话上：只摘当前查看那一场，别的场次不受影响（审计 P2-23 的「挂错考试」场景）。
        var vm = new MainViewModel();
        var sessions = SessionsOf(vm);
        var first = Session(1, "first.xlsx");
        var second = Session(2, "second.xlsx");
        var firstReport = Report("第一次的报表");
        var secondReport = Report("第二次的报表");
        first.Reports.Add(firstReport);
        second.Reports.Add(secondReport);
        sessions.Add(first);
        sessions.Add(second);
        vm.ActiveExamIndex = 1;

        vm.RemoveReport(secondReport);

        Assert.Empty(second.Reports);
        Assert.Single(first.Reports);
        Assert.Same(firstReport, first.Reports[0]);
    }

    [Fact]
    public void RemoveReport_IgnoresUnknownReport()
    {
        // 传进来一份不在此处的报表：不抛、不改状态。
        var vm = new MainViewModel();
        StandaloneReportsOf(vm).Add(Report("真实报表"));

        vm.RemoveReport(Report("不存在的报表"));
        vm.RemoveReport(null);

        Assert.Single(StandaloneReportsOf(vm));
    }

    [Fact]
    public void ReportCardItems_ExposeRemoveCommand()
    {
        // chip 上必须挂得出移除命令，否则界面没有入口。
        var vm = new MainViewModel();
        StandaloneReportsOf(vm).Add(Report("班级成绩对比"));
        vm.Section = 0;

        var cards = BuildCards(vm);

        var card = Assert.Single(cards);
        Assert.NotNull(card.Remove);
        Assert.True(card.Remove!.CanExecute(null));
    }

    [Fact]
    public void ReportCardRemoveCommand_DetachesReport()
    {
        // 走命令而不是直接调方法：XAML 绑的就是这个命令。
        var vm = new MainViewModel();
        StandaloneReportsOf(vm).Add(Report("班级成绩对比"));
        vm.Section = 0;

        var card = BuildCards(vm).Single();
        card.Remove!.Execute(null);

        Assert.Empty(StandaloneReportsOf(vm));
    }

    // ------------------------------------------------------------ P3-2 / P3-3 拖入汇总与忽略话术

    [Fact]
    public void ClassifyDroppedFiles_CountsUnsupportedFiles()
    {
        // 修复前必红：.csv / 文件夹一律 continue，零反馈（审计 P3-3）。
        var paths = new[]
        {
            @"C:\drops\成绩表.xlsx",
            @"C:\drops\说明.csv",
            @"C:\drops\子文件夹",
            @"C:\drops\旧表.xls",
            @"C:\drops\打包.zip",
        };

        var (gradeSheets, reports, zipCount, ignored) =
            MainViewModel.ClassifyDroppedFiles(paths, _ => false);

        Assert.Equal(new[] { @"C:\drops\成绩表.xlsx", @"C:\drops\旧表.xls" }, gradeSheets);
        Assert.Empty(reports);
        Assert.Equal(1, zipCount);
        Assert.Equal(2, ignored);
    }

    [Fact]
    public void ClassifyDroppedFiles_RoutesReportsByProbe()
    {
        var paths = new[] { @"C:\drops\a.xlsx", @"C:\drops\b.xlsx" };

        var (gradeSheets, reports, _, ignored) =
            MainViewModel.ClassifyDroppedFiles(paths, p => p.EndsWith("b.xlsx", StringComparison.Ordinal));

        Assert.Single(gradeSheets);
        Assert.Single(reports);
        Assert.Equal(0, ignored);
    }

    [Fact]
    public void DropSummary_DoesNotPointAtStatusBarItJustOverwrote()
    {
        // 修复前必红：汇总句写「（详见状态栏提示）」，而它自己就是状态栏最后一句（审计 P3-2）。
        var summary = MainViewModel.BuildDropSummary(imported: 2, failed: 1, ignored: 0);

        Assert.DoesNotContain("详见状态栏", summary, StringComparison.Ordinal);
        Assert.Contains("2 场考试", summary, StringComparison.Ordinal);
        Assert.Contains("1 份失败", summary, StringComparison.Ordinal);
        Assert.Contains("上方提示", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DropSummary_ReportsIgnoredCount()
    {
        var summary = MainViewModel.BuildDropSummary(imported: 1, failed: 0, ignored: 3);

        Assert.Contains("已导入 1 场考试", summary, StringComparison.Ordinal);
        Assert.Contains("已忽略 3 个不支持的文件", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("份失败", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureNotice_CarriesPerFileReasons()
    {
        // 明细要能进横幅：文件名 + 原因，逐份列出。
        var notice = MainViewModel.BuildFailureNotice(new[]
        {
            "第一份.xlsx：导入考试失败：文件正被其他程序占用",
            "第二份.xlsx：导入考试失败：无法处理该文件",
        });

        Assert.Contains("2 份成绩表未能导入", notice, StringComparison.Ordinal);
        Assert.Contains("第一份.xlsx", notice, StringComparison.Ordinal);
        Assert.Contains("第二份.xlsx", notice, StringComparison.Ordinal);
        Assert.Equal(string.Empty, MainViewModel.BuildFailureNotice(Array.Empty<string>()));
    }

    // ------------------------------------------------------------ P3-33 窗口尺寸档

    [Fact]
    public void MainWindow_DefaultAndMinimumSizeMatchThe1120x680Tier()
    {
        // 四家课堂应用统一 1120×680；分晓此前是 1040×640 + 默认 1080×700（审计 P3-33）。
        var xaml = RepoRoot.Read(Path.Combine("src", "GradeStrip.App", "Views", "MainWindow.axaml"));

        Assert.Contains("Width=\"1120\" Height=\"680\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"1120\" MinHeight=\"680\"", xaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ P3-34 ghost 三态

    [Fact]
    public void GhostButton_HasPressedAndDisabledStates()
    {
        // 壳层 App.axaml 有完整三态；分晓只有 base + pointerover（审计 P3-34）。
        var xaml = RepoRoot.Read(Path.Combine("src", "GradeStrip.App", "App.axaml"));

        Assert.Contains("Button.ghost:pressed", xaml, StringComparison.Ordinal);
        Assert.Contains("Button.ghost:disabled", xaml, StringComparison.Ordinal);
        Assert.Contains("#E9E4D7", xaml, StringComparison.Ordinal);
        Assert.Contains("#B3AD9E", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GhostButton_BaseColorsUnchanged()
    {
        // 只补态、不改基样式色值：底色仍透明、描边仍 #D6D1C4。
        var xaml = RepoRoot.Read(Path.Combine("src", "GradeStrip.App", "App.axaml"));
        var start = xaml.IndexOf("<Style Selector=\"Button.ghost\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "Button.ghost 基样式不见了");

        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        var block = xaml[start..end];

        Assert.Contains("Value=\"Transparent\"", block, StringComparison.Ordinal);
        Assert.Contains("#D6D1C4", block, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 报表移除入口在两处样式表里都要有

    [Fact]
    public void ReportChipRemoveStyle_ExistsInBothStyleTables()
    {
        // 分晓由工具箱同进程打开，运行时的样式取自壳层 App.axaml；
        // 只加在 GradeStrip.App/App.axaml 里，走查截图里那颗按钮会退回 Fluent 默认灰底。
        foreach (var path in new[]
                 {
                     Path.Combine("src", "GradeStrip.App", "App.axaml"),
                     Path.Combine("src", "TeachCraft.App", "App.axaml"),
                 })
        {
            var xaml = RepoRoot.Read(path);
            Assert.Contains("Button.report-chip-remove", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmptyState_OffersReportImportEntry()
    {
        // 摘光报表后概览区让位给空状态，空状态里必须留一个「导入分析报表…」入口，
        // 否则状态栏那句「可从概览重新挂载」无从执行（审计 P2-23）。
        var xaml = RepoRoot.Read(Path.Combine("src", "GradeStrip.App", "Views", "MainWindow.axaml"));
        var start = xaml.IndexOf("IsVisible=\"{Binding IsEmptyVisible}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "空状态区块不见了");

        // 空状态块到「数据概览」ScrollViewer 之间的片段。
        var end = xaml.IndexOf("OverviewScrollViewer", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var block = xaml[start..end];

        Assert.Contains("OnImportReports", block, StringComparison.Ordinal);
        Assert.Contains("导入分析报表", block, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 辅助

    private static bool MainWindowIsWalkthrough(IEnumerable<string>? args) =>
        GradeStrip.App.Views.MainWindow.IsWalkthroughCommandLine(args);

    private static List<ExamSession> SessionsOf(MainViewModel vm) =>
        (List<ExamSession>)typeof(MainViewModel).GetField("_sessions", PrivateInstance)!.GetValue(vm)!;

    private static List<OverviewReport> StandaloneReportsOf(MainViewModel vm) =>
        (List<OverviewReport>)typeof(MainViewModel).GetField("_standaloneReports", PrivateInstance)!.GetValue(vm)!;

    private static IReadOnlyList<ReportCardItem> BuildCards(MainViewModel vm)
    {
        // BuildReports 是 private：用 EnsureOverviewBuilt 的公开入口（切页签）触发重建。
        var section = vm.Section;
        vm.Section = 1;
        vm.Section = section;
        return vm.ReportCards.ToList();
    }

    private static OverviewReport Report(string title) => new()
    {
        Kind = OverviewReportKind.ClassComparison,
        KindLabel = "班级对比",
        Title = title,
        SourceFile = title + ".xlsx",
        Sheets = Array.Empty<OverviewSheet>(),
    };

    private static ExamSession Session(int order, string path) => new()
    {
        Order = order,
        Label = $"第 {order} 次考试",
        SourcePath = path,
        SourceFileName = Path.GetFileName(path),
        Data = new ExamData
        {
            ExamName = $"第{order}次考试",
            Subjects = new[] { "数学" },
            Students = new List<StudentRecord>
            {
                new()
                {
                    No = 1,
                    Name = "苏晚晴",
                    Total = new SubjectScore { Score = 100, ClassRank = 1 },
                    Subjects = new Dictionary<string, SubjectScore>
                    {
                        ["数学"] = new SubjectScore { Score = 100, ClassRank = 1 },
                    },
                },
            },
        },
    };

    /// <summary>截取从方法声明起、到下一个同级成员声明前的源码片段。</summary>
    private static string ExtractMethod(string source, string declarationPrefix)
    {
        var start = source.IndexOf(declarationPrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法（声明前缀：{declarationPrefix}）——源码结构变了，测试须同步锚点");

        var next = source.IndexOf("\n    /// <summary>", start + declarationPrefix.Length, StringComparison.Ordinal);
        var nextMember = source.IndexOf("\n    private ", start + declarationPrefix.Length, StringComparison.Ordinal);
        if (nextMember >= 0 && (next < 0 || nextMember < next)) next = nextMember;
        return next < 0 ? source[start..] : source[start..next];
    }
}
