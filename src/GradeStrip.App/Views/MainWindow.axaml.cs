using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using GradeStrip.App.ViewModels;
using GradeStrip.Core.Comparison;
using TeachCraft.Shared;

namespace GradeStrip.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        // 系统开了「减弱动效」：入场缩放与忙碌旋转不再挂载（样式挂在 Window.allow-motion 下），
        // 旋转弧换成不确定进度条。探测失败按正常动效处理。
        if (TeachCraft.Shared.MotionPreference.ReduceMotion)
        {
            Classes.Remove("allow-motion");
            BusySpinner.IsVisible = false;
            BusyIndeterminate.IsVisible = true;
        }
        else
        {
            Classes.Add("allow-motion");
        }

        // 与其余窗口一致：打开后把窗口钳进屏幕工作区（此前是唯一引入 WindowFit 却从不调用的窗口）。
        Opened += (_, _) => WindowFit.Clamp(this);

        // Esc 关闭考试管理遮罩：走隧道阶段（handledEventsToo），按钮吞掉 KeyDown 也照样能收到。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// 关窗是否要先确认。分晓的考试会话只存在内存里——没有自动保存、也没有工程文件，
    /// 一次误触 X 就丢掉整个下午的导入与设置（审计 P0-1）。
    ///
    /// 抽成纯函数是为了可测：走查工具集只截图、不碰窗口生命周期。
    /// 走查必须放行，否则一次 --gradestrip-shot 就会被确认框卡住。
    /// public 是刻意的：测试工程要能直接断言这条门控。
    /// </summary>
    public static bool ShouldConfirmOnClose(bool hasData, bool closeConfirmed, bool isWalkthrough) =>
        hasData && !closeConfirmed && !isWalkthrough;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var isWalkthrough = args.Skip(1).Any(a => a.StartsWith("--", StringComparison.Ordinal));

        if (!ShouldConfirmOnClose(ViewModel.HasData, _closeConfirmed, isWalkthrough))
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var discard = await ConfirmDialog.ShowAsync(
                this,
                "关闭分晓？",
                $"当前 {ViewModel.StudentCount} 名学生的成绩与报表只保存在内存中，关闭后不会保留。"
                + "可先「导出成绩条」留档。",
                "放弃并关闭",
                "继续编辑",
                danger: true);

            if (!discard) return;

            _closeConfirmed = true;
            Close();
        });
    }

    /// <summary>把窗口内容渲染成 PNG，用于设计走查（--shot 参数触发）。</summary>
    public void CaptureTo(string path)
    {
        var pixelSize = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bitmap.Render(this);

        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择成绩总表",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel 工作簿")
                {
                    Patterns = new[] { "*.xlsx", "*.xls", "*.xlsm", "*.zip" },
                },
            },
        });

        if (files.Count > 0)
        {
            var path = GetSafeLocalPath(files[0])!;
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.Status = "压缩包不能直接导入。请先解压后选择其中的 .xlsx / .xls 表格导入；若解压后文件名出现乱码，推荐使用 Windows 自带解压或 Bandizip。";
                ViewModel.StatusBrush = Palette.Clay;
                return;
            }

            if (!ViewModel.HasData)
                await ViewModel.LoadAsync(path);
            else
                await ViewModel.AddSessionAsync(path);
        }
    }

    /// <summary>导入智学网分析报表（统计表 / 试卷分析），支持一次多选。</summary>
    private async void OnImportReports(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入分析报表",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("智学网分析报表")
                {
                    Patterns = new[] { "*.xlsx", "*.xls", "*.xlsm", "*.zip" },
                },
            },
        });

        if (files.Count == 0) return;

        var paths = files
            .Select(GetSafeLocalPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count == 0) return;

        if (paths.Any(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Status = "压缩包不能直接导入。请先解压后选择其中的 .xlsx / .xls 表格导入；若解压后文件名出现乱码，推荐使用 Windows 自带解压或 Bandizip。";
            ViewModel.StatusBrush = Palette.Clay;
            paths = paths.Where(p => !p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (paths.Count > 0) await ViewModel.ImportReportsAsync(paths);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            // 保存框自带的「覆盖？」提示是系统对话框，只有一句「文件已存在」，说不出
            // 这个文件是刚导入的成绩总表 / 分析报表。把约束写进标题，让老师在选文件名时
            // 就能看到（审计 P2-5 收尾）；命中后 ViewModel 里还有中文话术与 Core 兜底。
            Title = "导出成绩条（不能覆盖已导入的成绩总表或分析报表）",
            SuggestedFileName = ViewModel.SuggestedFileName(),
            DefaultExtension = "xlsx",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Excel 工作簿") { Patterns = new[] { "*.xlsx" } },
            },
            ShowOverwritePrompt = true,
        });

        if (file is not null) await ViewModel.ExportAsync(GetSafeLocalPath(file)!);
    }

    private void OnNavOverview(object? sender, RoutedEventArgs e)
    {
        ViewModel.Detail = null;
        ViewModel.SubjectDetail = null;
        ViewModel.Section = 0;
        OverviewScrollViewer.Offset = new Avalonia.Vector(0, 0);
    }

    private void OnNavStrips(object? sender, RoutedEventArgs e) => ViewModel.Section = 1;

    /// <summary>展开 / 收起学生排行与单科学生排名（超过 50 人折叠）。</summary>
    private void OnToggleStudents(object? sender, RoutedEventArgs e) => ViewModel.ToggleStudentsExpanded();

    private void OnOpenStudent(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StudentRow row)
        {
            ViewModel.OpenStudent(row);
            StudentDetailScrollViewer.Offset = new Avalonia.Vector(0, 0);
        }
    }

    private void OnSubjectClick(object? sender, RoutedEventArgs e)
    {
        string? name = null;
        if (sender is Button btn && btn.Tag is string tag)
            name = tag;
        else if ((sender as Control)?.DataContext is SubjectStat stat)
            name = stat.Name;
        else if ((sender as Control)?.DataContext is DetailSubjectRow detailRow)
            name = detailRow.Name;
        else if ((sender as Control)?.DataContext is SubjectTrajectoryRow trajRow)
            name = trajRow.SubjectName;

        if (!string.IsNullOrEmpty(name))
        {
            ViewModel.OpenSubject(name);
            SubjectDetailScrollViewer.Offset = new Avalonia.Vector(0, 0);
        }
    }

    private void OnStudentNameClick(object? sender, RoutedEventArgs e)
    {
        string? name = null;
        if (sender is Button btn && btn.Tag is string tag)
            name = tag;
        else if ((sender as Control)?.DataContext is SubjectStudentRow row)
            name = row.StudentName;

        if (!string.IsNullOrEmpty(name))
        {
            ViewModel.OpenStudentByName(name);
            StudentDetailScrollViewer.Offset = new Avalonia.Vector(0, 0);
        }
    }

    private void OnBackToOverview(object? sender, RoutedEventArgs e)
    {
        ViewModel.Detail = null;
        ViewModel.SubjectDetail = null;
        ViewModel.Section = 0;
        OverviewScrollViewer.Offset = new Avalonia.Vector(0, 0);
    }

    private void OnSelectClassRank(object? sender, RoutedEventArgs e) => ViewModel.UseSchoolRank = false;

    private void OnSelectSchoolRank(object? sender, RoutedEventArgs e) => ViewModel.UseSchoolRank = true;

    private void OnSelectCompPrevious(object? sender, RoutedEventArgs e) =>
        ViewModel.ComparisonMode = ComparisonBaseline.PreviousExam;

    private void OnSelectCompFirst(object? sender, RoutedEventArgs e) =>
        ViewModel.ComparisonMode = ComparisonBaseline.FirstExam;

    private void OnSelectCompOriginal(object? sender, RoutedEventArgs e) =>
        ViewModel.ComparisonMode = ComparisonBaseline.OriginalFile;

    private void OnSelectTrendScore(object? sender, RoutedEventArgs e) =>
        ViewModel.StudentTrendChartMode = 0;

    private void OnSelectTrendClassRank(object? sender, RoutedEventArgs e) =>
        ViewModel.StudentTrendChartMode = 1;

    private void OnSelectTrendSchoolRank(object? sender, RoutedEventArgs e) =>
        ViewModel.StudentTrendChartMode = 2;

    private void OnOpenExamManager(object? sender, RoutedEventArgs e) =>
        ViewModel.OpenExamManager();

    private void OnCloseExamManager(object? sender, RoutedEventArgs e) =>
        ViewModel.CloseExamManager();

    /// <summary>
    /// 考试管理遮罩的 Esc 判定。抽成纯函数是为了可测：窗口事件本身要在真实窗口里才触发，
    /// 而「遮罩开着 + 焦点不在输入控件里」这条规则必须能单独断言（审计 P3-4）。
    /// 输入控件里的 Esc 放行——文本框 / 下拉框有自己的取消语义，不该被窗口抢走。
    /// </summary>
    public static bool ShouldCloseExamManagerOnEscape(bool isExamManagerOpen, bool sourceIsTextInput) =>
        isExamManagerOpen && !sourceIsTextInput;

    /// <summary>
    /// 窗口级键盘：考试管理遮罩打开时 Esc 关闭回概览（审计 P3-4）。
    ///
    /// 接在窗口层而不是遮罩上：焦点可能还留在背后的「考试管理」按钮上，
    /// 那条事件链根本不经过遮罩，挂在遮罩上的 KeyDown 收不到。
    /// 用 Tunnel 是因为按钮会把 KeyDown 吞成 handled（空格 / 回车激活），
    /// 隧道阶段先一步拿到，才能保证「任何位置按 Esc 都退得掉」。
    /// 输入控件里的 Esc 不拦——那是文本框 / 下拉框自己的事。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!ShouldCloseExamManagerOnEscape(
                ViewModel.IsExamManagerOpen, e.Source is TextBox or ComboBox)) return;

        ViewModel.CloseExamManager();
        e.Handled = true;
    }

    private void OnSelectSessionFromNav(object? sender, RoutedEventArgs e)
    {
        var order = (sender as Control)?.Tag as int? ?? ((sender as Control)?.DataContext as ExamSessionItem)?.Order;
        if (order.HasValue) ViewModel.SetActiveSession(order.Value);
    }

    private void OnSetActiveSession(object? sender, RoutedEventArgs e)
    {
        var order = (sender as Control)?.Tag as int? ?? ((sender as Control)?.DataContext as ExamSessionItem)?.Order;
        if (order.HasValue) ViewModel.SetActiveSession(order.Value);
    }

    private void OnMoveSessionUp(object? sender, RoutedEventArgs e)
    {
        var order = (sender as Control)?.Tag as int? ?? ((sender as Control)?.DataContext as ExamSessionItem)?.Order;
        if (order.HasValue) ViewModel.MoveSessionUp(order.Value);
    }

    private void OnMoveSessionDown(object? sender, RoutedEventArgs e)
    {
        var order = (sender as Control)?.Tag as int? ?? ((sender as Control)?.DataContext as ExamSessionItem)?.Order;
        if (order.HasValue) ViewModel.MoveSessionDown(order.Value);
    }

    private async void OnAddExamSession(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择考试成绩表",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel 工作簿") { Patterns = new[] { "*.xlsx", "*.xls", "*.xlsm" } },
            },
        });

        if (files.Count > 0)
        {
            await ViewModel.AddSessionAsync(GetSafeLocalPath(files[0])!);
        }
    }

    private async void OnReplaceSession(object? sender, RoutedEventArgs e)
    {
        var order = (sender as Control)?.Tag as int? ?? ((sender as Control)?.DataContext as ExamSessionItem)?.Order;
        if (order is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"替换【第 {order.Value} 次考试】成绩表",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel 工作簿") { Patterns = new[] { "*.xlsx", "*.xls", "*.xlsm" } },
            },
        });

        if (files.Count > 0)
        {
            if (!await ConfirmDialog.ShowAsync(
                    this, "替换这场考试？",
                    $"第 {order.Value} 场考试的成绩、分析与对比基准会被新文件整体覆盖，且无法撤销。"
                    + "新文件不能与其他场次重复——两场指向同一份表格，互为基准时全班进退步会恒为 0。",
                    "替换", "取消", danger: true))
                return;

            await ViewModel.ReplaceSessionAsync(order.Value, GetSafeLocalPath(files[0])!);
        }
    }

    private void OnRemoveSession(object? sender, RoutedEventArgs e)
    {
        var order = (sender as Control)?.Tag as int? ?? ((sender as Control)?.DataContext as ExamSessionItem)?.Order;
        if (order.HasValue)
        {
            ViewModel.RequestRemoveSession(order.Value);
        }
    }

    private void OnConfirmRemoveSession(object? sender, RoutedEventArgs e) => ViewModel.ConfirmRemoveSession();

    private void OnCancelRemoveSession(object? sender, RoutedEventArgs e) => ViewModel.CancelRemoveSession();

    private void OnDismissNotice(object? sender, RoutedEventArgs e) => ViewModel.DismissNotice();

    private void OnToggleAbsent(object? sender, PointerPressedEventArgs e) =>
        ViewModel.ExcludeAbsent = !ViewModel.ExcludeAbsent;

    /// <summary>KPI 卡是 Border 实现的开关，补一条键盘路径（Enter / 空格）。</summary>
    private void OnKpiKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        ViewModel.ExcludeAbsent = !ViewModel.ExcludeAbsent;
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        var paths = files?
            .Select(GetSafeLocalPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths is { Count: > 0 })
            await ViewModel.ImportDroppedFilesAsync(paths);
    }

    private static string? GetSafeLocalPath(Avalonia.Platform.Storage.IStorageItem? item)
    {
        if (item is null) return null;
        var p = item.TryGetLocalPath();
        if (string.IsNullOrEmpty(p))
        {
            if (item.Path.IsAbsoluteUri && item.Path.IsFile)
                p = item.Path.LocalPath;
            else if (item.Path.IsAbsoluteUri)
                p = Uri.UnescapeDataString(item.Path.AbsolutePath);
            else
                p = item.Path.ToString();
        }
        return p?.Normalize(System.Text.NormalizationForm.FormC);
    }
}