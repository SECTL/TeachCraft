using System.Reflection;
using GradeStrip.App.ViewModels;
using GradeStrip.Core.Comparison;
using GradeStrip.Core.Model;

namespace TeachCraft.Tests;

/// <summary>
/// 分晓的输出保护与关窗门控（审计 P0-2 / P0-1 最低版）。
///
/// 为什么测静态守卫而不是走 ExportAsync/ImportAsync 全流程：
/// 分晓的导入导出都包在 RunBusyAsync 里，而它第一件事就是
/// `Dispatcher.UIThread.InvokeAsync(...)`——无 UI 线程的测试环境会永久挂起。
/// 因此把守卫抽成纯函数单独断言；完整链路用走查参数在真实运行中验证。
///
/// 为什么放 TeachCraft.Tests 而不是 GradeStrip.Tests：后者只引用
/// GradeStrip.Core，看不到 App 层；本工程已按同一模式引用 MeetRoster.App。
/// </summary>
public sealed class GradeStripExportGuardTests
{
    [Fact]
    public void OverwriteGuard_RejectsExportOntoImportedSource()
    {
        var source = Path.Combine(Path.GetTempPath(), "exam-sheet.xlsx");
        Assert.True(MainViewModel.WouldOverwriteImportedSource(source, new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_IsCaseInsensitive()
    {
        var source = Path.Combine(Path.GetTempPath(), "Exam-Sheet.xlsx");
        var exportTo = Path.Combine(Path.GetTempPath(), "exam-sheet.XLSX");
        Assert.True(MainViewModel.WouldOverwriteImportedSource(exportTo, new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_NormalizesRelativePaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        var source = Path.Combine(cwd, "relative-source.xlsx");
        Assert.True(MainViewModel.WouldOverwriteImportedSource("relative-source.xlsx", new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_AllowsDistinctTarget()
    {
        var source = Path.Combine(Path.GetTempPath(), "exam-a.xlsx");
        var exportTo = Path.Combine(Path.GetTempPath(), "exam-a-成绩条.xlsx");
        Assert.False(MainViewModel.WouldOverwriteImportedSource(exportTo, new[] { source }));
    }

    [Fact]
    public void OverwriteGuard_RejectsWhenAnySessionCollides()
    {
        // 多场考试：只要命中任意一场的源文件就要拒绝。
        var first = Path.Combine(Path.GetTempPath(), "exam-1.xlsx");
        var second = Path.Combine(Path.GetTempPath(), "exam-2.xlsx");
        Assert.True(MainViewModel.WouldOverwriteImportedSource(second, new[] { first, second }));
    }

    [Fact]
    public void OverwriteGuard_IgnoresBlankSourcePaths()
    {
        var exportTo = Path.Combine(Path.GetTempPath(), "out.xlsx");
        Assert.False(MainViewModel.WouldOverwriteImportedSource(exportTo, new[] { "", "  " }));
    }

    /// <summary>
    /// 关窗门控（审计 P0-1 最低版）。走查工具集只截图、不碰窗口生命周期，
    /// 所以门控抽成纯函数单测；走查通道必须放行，否则一次截图会被确认框卡住。
    /// </summary>
    [Theory]
    [InlineData(true, false, false, true)]   // 有数据、未确认过、非走查 → 拦一次
    [InlineData(false, false, false, false)] // 无数据 → 不拦
    [InlineData(true, true, false, false)]   // 已确认过（第二次 Close）→ 放行
    [InlineData(true, false, true, false)]   // 走查参数启动 → 放行
    public void ShouldConfirmOnClose_GatesAsExpected(
        bool hasData, bool closeConfirmed, bool isWalkthrough, bool expected)
    {
        Assert.Equal(expected,
            GradeStrip.App.Views.MainWindow.ShouldConfirmOnClose(hasData, closeConfirmed, isWalkthrough));
    }

    // ---------------------------------------------------------------- 报表来源（审计 P2-1）

    private static readonly BindingFlags PrivateInstance =
        BindingFlags.NonPublic | BindingFlags.Instance;

    private static List<ExamSession> SessionsOf(MainViewModel vm) =>
        (List<ExamSession>)typeof(MainViewModel).GetField("_sessions", PrivateInstance)!.GetValue(vm)!;

    private static List<OverviewReport> StandaloneReportsOf(MainViewModel vm) =>
        (List<OverviewReport>)typeof(MainViewModel)
            .GetField("_standaloneReports", PrivateInstance)!.GetValue(vm)!;

    private static ExamData MinimalExam(string examName) => new()
    {
        ExamName = examName,
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
    };

    private static ExamSession MinimalSession(int order, string path) => new()
    {
        Order = order,
        Label = $"第 {order} 次考试",
        SourcePath = path,
        SourceFileName = Path.GetFileName(path),
        Data = MinimalExam($"第{order}次考试"),
    };

    [Fact]
    public void ExportGuard_RejectsImportedReportSource()
    {
        // 修复前必红：守卫只比对成绩总表 SourcePath，报表只记文件名，
        // 导出成绩条可以直接把刚导入的分析报表原件覆盖掉（审计 P2-1）。
        var reportPath = Path.Combine(Path.GetTempPath(), "班级成绩对比.xlsx");
        var vm = new MainViewModel();
        var session = MinimalSession(1, Path.Combine(Path.GetTempPath(), "成绩总表.xlsx"));
        session.Reports.Add(new OverviewReport
        {
            Kind = OverviewReportKind.ClassComparison,
            Title = "班级成绩对比",
            SourceFile = Path.GetFileName(reportPath),
            SourceFullPath = reportPath,
        });
        SessionsOf(vm).Add(session);

        var sources = vm.CollectImportedSources().ToList();

        Assert.Contains(reportPath, sources);
        Assert.True(MainViewModel.WouldOverwriteImportedSource(reportPath, sources),
            "报表原件必须纳入导出守卫，否则导出会覆盖刚导入的报表");
    }

    [Fact]
    public void ExportGuard_RejectsStandaloneReportSource()
    {
        // 还没建立成绩会话时导入的报表同样是来源，不能因为没挂到会话上就漏拦。
        var reportPath = Path.Combine(Path.GetTempPath(), "试卷分析.xlsx");
        var vm = new MainViewModel();
        StandaloneReportsOf(vm).Add(new OverviewReport
        {
            Kind = OverviewReportKind.QuestionAnalysis,
            SourceFile = Path.GetFileName(reportPath),
            SourceFullPath = reportPath,
        });

        Assert.True(MainViewModel.WouldOverwriteImportedSource(reportPath, vm.CollectImportedSources()));
    }

    [Fact]
    public void ExportGuard_IgnoresReportsWithoutFullPath()
    {
        // 旧数据 / 内存构造的报表没有完整路径：跳过即可，不得抛异常、也不得误拦别的目标。
        var vm = new MainViewModel();
        var session = MinimalSession(1, Path.Combine(Path.GetTempPath(), "成绩总表.xlsx"));
        session.Reports.Add(new OverviewReport
        {
            Kind = OverviewReportKind.ClassComparison,
            SourceFile = "只有文件名.xlsx",
        });
        SessionsOf(vm).Add(session);

        var sources = vm.CollectImportedSources().ToList();

        Assert.DoesNotContain(sources, s => s.Contains("只有文件名"));
        Assert.False(MainViewModel.WouldOverwriteImportedSource(
            Path.Combine(Path.GetTempPath(), "导出.xlsx"), sources));
    }

    [Fact]
    public void ExportGuard_AllowsExportWhenOnlyReportNameMatchesButPathDiffers()
    {
        // 同名不同目录的文件不算冲突——比对的是完整路径，不是文件名。
        var reportPath = Path.Combine(Path.GetTempPath(), "sub-a", "报表.xlsx");
        var exportTo = Path.Combine(Path.GetTempPath(), "sub-b", "报表.xlsx");
        var vm = new MainViewModel();
        var session = MinimalSession(1, Path.Combine(Path.GetTempPath(), "成绩总表.xlsx"));
        session.Reports.Add(new OverviewReport
        {
            Kind = OverviewReportKind.ClassComparison,
            SourceFile = "报表.xlsx",
            SourceFullPath = reportPath,
        });
        SessionsOf(vm).Add(session);

        Assert.False(MainViewModel.WouldOverwriteImportedSource(exportTo, vm.CollectImportedSources()));
    }

    // ---------------------------------------------------------------- 替换考试查重（审计 P2-2）

    [Fact]
    public void ReplaceGuard_RejectsFileAlreadyUsedByAnotherSession()
    {
        // 修复前必红：ReplaceSessionCoreAsync 没有任何查重，
        // 可以把两场考试指向同一份文件，互为基准时全班进退步恒为 0（审计 P2-2）。
        var shared = Path.Combine(Path.GetTempPath(), "shared-exam.xlsx");
        var sessions = new List<ExamSession>
        {
            MinimalSession(1, shared),
            MinimalSession(2, Path.Combine(Path.GetTempPath(), "other-exam.xlsx")),
        };

        var conflict = MainViewModel.FindSessionSourceConflict(sessions, replacingOrder: 2, shared);

        Assert.NotNull(conflict);
        Assert.Equal(1, conflict!.Order);
    }

    [Fact]
    public void ReplaceGuard_AllowsReplacingWithOwnSourceFile()
    {
        // 换成本场原来的文件 = 就地更新，必须放行。
        var own = Path.Combine(Path.GetTempPath(), "own-exam.xlsx");
        var sessions = new List<ExamSession>
        {
            MinimalSession(1, Path.Combine(Path.GetTempPath(), "first.xlsx")),
            MinimalSession(2, own),
        };

        Assert.Null(MainViewModel.FindSessionSourceConflict(sessions, replacingOrder: 2, own));
    }

    [Fact]
    public void ReplaceGuard_IsCaseInsensitiveAndNormalizesPaths()
    {
        var used = Path.Combine(Path.GetTempPath(), "Used-Exam.xlsx");
        var sessions = new List<ExamSession>
        {
            MinimalSession(1, used),
            MinimalSession(2, Path.Combine(Path.GetTempPath(), "second.xlsx")),
        };

        var conflict = MainViewModel.FindSessionSourceConflict(
            sessions, replacingOrder: 2, used.ToUpperInvariant());

        Assert.NotNull(conflict);
        Assert.Equal(1, conflict!.Order);
    }

    // ---------------------------------------------------------------- 考试管理遮罩（审计 P3-4）

    [Theory]
    [InlineData(true, false, true)]   // 遮罩开着、焦点不在输入控件 → Esc 关闭
    [InlineData(true, true, false)]   // 焦点在文本框 / 下拉框 → 让给它自己的取消语义
    [InlineData(false, false, false)] // 遮罩没开 → 不动
    public void ExamManagerEscape_ClosesOnlyWhenOverlayOpenAndNotInInput(
        bool open, bool inTextInput, bool expected)
    {
        Assert.Equal(expected,
            GradeStrip.App.Views.MainWindow.ShouldCloseExamManagerOnEscape(open, inTextInput));
    }

    [Fact]
    public void IsNotBusy_MirrorsIsBusy()
    {
        // 遮罩有 400ms 延迟才亮，这半秒里操作按钮必须靠 IsNotBusy 自己先置灰（审计 P3-4）。
        var vm = new MainViewModel();

        Assert.True(vm.IsNotBusy);

        vm.IsBusy = true;
        Assert.False(vm.IsNotBusy);

        vm.IsBusy = false;
        Assert.True(vm.IsNotBusy);
    }

    /// <summary>读源码做结构断言（与 LandedOptimizationTests 同款）：XAML / 文案没有运行时断言面。</summary>
    private static string ReadSource(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ExamManager_ActionButtonsAreDisabledWhileBusy()
    {
        // 五个操作按钮（设为主考 / ↑ / ↓ / 替换 / 删除）整组绑 IsNotBusy。
        var xaml = ReadSource("src", "GradeStrip.App", "Views", "MainWindow.axaml");

        Assert.Contains("DataContext.IsNotBusy", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceConfirm_WarnsAboutDuplicateSourceFile()
    {
        // 替换确认必须写明「新文件不能与其他场次重复」，否则老师不知道刚被拒的原因（审计 P2-2）。
        var source = ReadSource("src", "GradeStrip.App", "Views", "MainWindow.axaml.cs");

        Assert.Contains("新文件不能与其他场次重复", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportSaveDialog_TitleNamesTheOverwriteConstraint()
    {
        // Avalonia 的覆盖提示是系统对话框，没有自定义文案面；约束只能落在保存框标题上（审计 P2-5）。
        var source = ReadSource("src", "GradeStrip.App", "Views", "MainWindow.axaml.cs");

        Assert.Contains("不能覆盖已导入的成绩总表或分析报表", source, StringComparison.Ordinal);
    }
}
