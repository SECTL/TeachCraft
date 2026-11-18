using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MeetRoster.Core.Export;
using MeetRoster.Core.Import;
using TeachCraft.Shared;

namespace MeetRoster.App;

public partial class MeetRosterWindow : Window
{
    private static readonly FilePickerFileType ExcelFilter = new("Excel 报名表")
    {
        Patterns = ["*.xlsx"],
    };

    private static readonly FilePickerFileType RosterFilter = new("学生名单")
    {
        Patterns = ["*.xlsx"],
    };

    private bool _closeConfirmed;

    public MeetRosterWindow() : this(new MeetRosterViewModel())
    {
    }

    public MeetRosterWindow(MeetRosterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Opened += (_, _) =>
        {
            WindowFit.Clamp(this);
            UpdateCompact();
        };
        SizeChanged += (_, _) => UpdateCompact();
        KeyDown += OnWindowKeyDown;
    }

    private MeetRosterViewModel ViewModel => (MeetRosterViewModel)DataContext!;

    /// <summary>窗口收窄到 1240 以下时隐藏右侧概览，避免报名表被挤成多行。</summary>
    private void UpdateCompact() => ViewModel.IsCompact = Bounds.Width < 1240;

    /// <summary>走查用：把每行的高度与行内控件位置写成文本，用于核对垂直居中。</summary>
    public void DumpRowMetrics(string path)
    {
        var lines = new List<string>();
        var rows = this.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("athlete-row"))
            .Take(6)
            .ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowTop = row.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
            lines.Add($"row[{i}] top={rowTop:F1} h={row.Bounds.Height:F1}");

            foreach (var child in row.GetVisualDescendants().OfType<Control>())
            {
                var tag = child switch
                {
                    ComboBox => "combo",
                    TextBox => "textbox",
                    Button b when b.Classes.Contains("event-cell") => "chip",
                    Button b when b.Classes.Contains("mini") => "del",
                    TextBlock t when t.Classes.Contains("table-head") => "head",
                    _ => null,
                };
                if (tag is null || child.Bounds.Height <= 0) continue;
                var top = child.TranslatePoint(new Point(0, 0), this)?.Y;
                if (top is null) continue;
                var rel = top.Value - rowTop;
                lines.Add($"  {tag} y={rel:F1} h={child.Bounds.Height:F1} " +
                          $"gapTop={rel:F1} gapBottom={row.Bounds.Height - rel - child.Bounds.Height:F1}");
            }
        }

        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 有未保存的改动才拦：没有改动时关闭不会丢任何东西。
        if (!_closeConfirmed && ViewModel.IsDirty)
        {
            e.Cancel = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                // 秩序册工程没有逐班原文件，「保存 / 另存为」都是灰的——
                // 老师改完体检问题后关窗，只有「放弃并关闭」一条路，全部修改重来。
                // 这里补一句「导出参赛手册留档」的出路（审计 P1-15 最低版 / T23）。
                if (ViewModel.IsFromOrderBook)
                {
                    var export = await ConfirmDialog.ShowAsync(
                        this, "关闭竞名？",
                        $"当前有未保存的修改（{ViewModel.AllClasses.Count} 个班）。"
                        + "秩序册工程没有逐班原文件，「保存」不可用；可以先导出参赛手册留档。",
                        "先导出留档", "放弃并关闭", danger: false);
                    if (export)
                    {
                        // 走既有的导出流程；用户取消保存框时留在编辑态不关窗。
                        await OnExportHandBookAsync();
                        // 只认**本次**真的写出的文件：旧实现一直留着上一次的路径，
                        // 在保存框里取消也会被判成「已留档」而直接关窗（审计 P2-16）。
                        if (!_archiveExport.HasArchive)
                            return;
                    }
                }

                if (await ConfirmDialog.ShowAsync(
                        this, "确认关闭？",
                        "关闭后当前修改不会保留。",
                        "放弃并关闭", "继续编辑", danger: true))
                {
                    _closeConfirmed = true;
                    Close();
                }
            });
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 关窗留档用的「本次导出」记录：进入导出先清空、只有成功才记路径。
    /// 旧实现用一个从不清理的字段，取消保存框后旧路径还在，File.Exists 误判「已留档」。
    /// </summary>
    private readonly ArchiveExportTracker _archiveExport = new();

    /// <summary>设计走查用：把窗口渲染成 PNG；scale 用于放大取证明细。</summary>
    public void CaptureTo(string path, double scale = 1)
    {
        _closeConfirmed = true;
        var pixelSize = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private async void OnImportRoster(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入班级报名表",
                AllowMultiple = true,
                FileTypeFilter = [ExcelFilter],
            });

            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToList();

            if (paths.Count == 0) return;

            var existingAthletes = ViewModel.AllClasses.Sum(c => c.AthleteCount);
            if (ViewModel.IsDemo)
            {
                if (!await ConfirmDialog.ShowAsync(
                        this, "导入报名表？",
                        $"将导入 {paths.Count} 份报名表并替换当前示例数据，示例班级与排程不会保留。",
                        "导入并替换示例", "取消", danger: true))
                    return;
            }
            else
            {
                // 廉价预读各文件的"参赛单位"，把"新增 / 替换 / 保留"算给用户看；预读失败按新增对待。
                // 必须放后台：每份文件都要完整开一次 XLWorkbook，一次选 30 份会在 UI 线程上
                // 白屏几秒（审计 P2-8）。
                var peeked = await Task.Run(() => paths
                    .Select(path => (Path: path, Name: RegistrationWorkbookImporter.PeekClassName(path)))
                    .ToList());
                var existingNames = ViewModel.AllClasses.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
                var replaceNames = peeked
                    .Where(p => p.Name is not null && existingNames.Contains(p.Name))
                    .Select(p => p.Name!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                var newCount = peeked.Count(p => p.Name is null || !existingNames.Contains(p.Name));

                if (existingAthletes > 0 && !await ConfirmDialog.ShowAsync(
                        this, "导入报名表？",
                        $"本次导入 {paths.Count} 份：新增 {newCount} 个班{SummarizeNewClasses(peeked, existingNames)}；"
                        + $"替换 {replaceNames.Count} 个同名班{SummarizeClassNames(replaceNames)}；"
                        + $"其余 {Math.Max(ViewModel.AllClasses.Count - replaceNames.Count, 0)} 个现有班原样保留。"
                        + "同名班级将按新文件整体替换。",
                        "导入报名表", "取消", danger: true))
                    return;
            }

            await ViewModel.ImportRegistrationFilesAsync(paths);
        }
        catch (Exception ex)
        {
            ViewModel.SetStatus(ErrorText.Describe(ex, "导入报名表"), true);
        }
    }

    /// <summary>
    /// 统一导入入口：一份工作簿自带名单就按秩序册整本导入，只有日程 / 分组表就按赛程导入。
    /// 两者对老师来说是同一件事（都是「导入秩序册」），不该让他们先判断该点哪个按钮。
    /// </summary>
    private async void OnImportOrderBook(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入秩序册 / 赛程工作簿",
                AllowMultiple = false,
                FileTypeFilter = [ExcelFilter],
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            // 整份工作簿只打开一次：判类型与后续导入共用同一个实例（60 班秩序册原先要解析三遍）。
            // 打开与判类型都在后台线程；文件以共享读打开，会话存活期间 Excel 仍能同时打开同一份文件。
            // 生命周期由本方法负责（using），ViewModel 只借用、不释放——否则会用到已释放的实例。
            var (session, kind, openError) = await Task.Run(() => OpenAndDetect(path));

            // 打不开与认不出去分两种话术：前者是文件损坏 / 加密，后者是表型不符。
            if (session is null)
            {
                // openError 已含「损坏 / 被密码保护」的完整建议（WorkbookIO），不再重复一遍。
                ViewModel.SetStatus($"这份文件打不开：{openError}", true);
                return;
            }

            if (kind == MeetWorkbookKind.Unknown)
            {
                session.Dispose();
                ViewModel.SetStatus(
                    "这份工作簿里既没有「代表队名单」，也没有「竞赛日程 / 项目分组表」，认不出是什么表。"
                    + "请确认选的是秩序册或往届赛程。", true);
                return;
            }

            using (session)
            {
                if (kind == MeetWorkbookKind.ScheduleOnly)
                {
                    // 只有赛程：保留现有班级，只换赛程与分组条目。
                    if (ViewModel.HasScheduleDays && !await ConfirmDialog.ShowAsync(
                            this, "替换当前赛程？",
                            "这份工作簿里没有代表队名单，将只导入赛程与分组条目，"
                            + "替换当前的赛程表；现有班级名单保持不变。",
                            "替换赛程", "取消", danger: true))
                        return;

                    await ViewModel.ImportScheduleFileAsync(path, session.Workbook);
                    return;
                }

                if (ViewModel.HasAnyData && !await ConfirmDialog.ShowAsync(
                        this, "用秩序册替换当前数据？",
                        "这份工作簿带代表队名单，将整本导入并替换当前的班级名单与赛程"
                        + $"（现有 {ViewModel.AllClasses.Count} 个班）。未保存的报名表修改不会保留。",
                        "导入秩序册", "取消", danger: true))
                    return;

                await ViewModel.ImportOrderBookAsync(path, session.Workbook);
            }
        }
        catch (Exception ex)
        {
            ViewModel.SetStatus(ErrorText.Describe(ex, "导入秩序册"), true);
        }
    }

    /// <summary>
    /// 打开工作簿并判断这是秩序册还是只有赛程。
    ///
    /// 失败时必须把「打不开」与「认不出」分开：损坏 / 加密的文件打不开，
    /// 报「认不出是什么表」会让老师反复核对文件类型而永远不成功（审计 P2-38）。
    /// 失败时已经打开的会话当场收掉，成功时把所有权交给调用方。
    /// </summary>
    private static (WorkbookSession? Session, MeetWorkbookKind Kind, string? Error) OpenAndDetect(string path)
    {
        WorkbookSession? session = null;
        try
        {
            session = WorkbookSession.Open(path);
            return (session, OrderBookImporter.DetectKind(session.Workbook), null);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            // 复用 WorkbookIO 已经写好的中文话术（损坏 / 被密码保护）。
            var message = ex is InvalidDataException ? ex.Message : ErrorText.Describe(ex, "打开工作簿");
            return (null, MeetWorkbookKind.Unknown, message);
        }
    }

    /// <summary>导出参赛手册：先选设置，再选保存位置。</summary>
    private async void OnExportHandBook(object? sender, RoutedEventArgs e) => await OnExportHandBookAsync();

    /// <summary>导出参赛手册（设置 → 保存框 → 写出）。关窗留档也走这条路径。</summary>
    private async Task OnExportHandBookAsync()
    {
        // 本次进入导出：上一次的留档路径立刻作废，只有这次真写出才重新记上。
        _archiveExport.BeginExport();
        try
        {
            if (ViewModel.AllClasses.Count == 0)
            {
                ViewModel.SetStatus("还没有导入任何班级，无法导出。", true);
                return;
            }

            var dialog = new MeetExportOptionsWindow(
                ViewModel.Project, ViewModel.SelectedClass?.Name ?? ViewModel.ScheduleHighlightClass?.Name);
            var request = await dialog.ShowDialog<MeetExportRequest?>(this);
            if (request is null) return;

            var extension = request.Format == MeetExportFormat.Pdf ? "pdf" : "xlsx";
            var typeName = request.Format == MeetExportFormat.Pdf ? "PDF 参赛手册" : "Excel 工作簿";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出参赛手册",
                SuggestedFileName = request.FileName,
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }],
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            if (ViewModel.IsExportPathBlocked(path))
            {
                ViewModel.SetStatus("导出文件不能覆盖导入的原文件，请换一个文件名或位置。", true);
                return;
            }

            var exported = await ViewModel.ExportHandBookAsync(request, path);
            _archiveExport.CompleteExport(exported, path);
        }
        catch (Exception ex)
        {
            ViewModel.SetStatus(ErrorText.Describe(ex, "导出参赛手册"), true);
        }
    }

    private async void OnExportRoster(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "导出报名表到文件夹",
                AllowMultiple = false,
            });

            var directory = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(directory)) return;

            // 计数前先剔除「目标就是导入原件」的班：那份文件不该被写出，也不该
            // 被算进「将覆盖 N 份」——那是在承诺一个不会发生、也不该发生的动作。
            // 上轮 P2-7 声称修过这条、实际没落地（调研 P2-15），这里是正式回补。
            var plan = RegistrationExportPlanner.Plan(
                ViewModel.AllClasses
                    .Where(c => !string.IsNullOrWhiteSpace(c.Source.SourcePath))
                    .Select(c => c.Name),
                directory,
                ViewModel.IsExportPathBlocked,
                File.Exists);

            if (plan.AllBlocked)
            {
                ViewModel.SetStatus("这个文件夹就是原件所在位置，请另选一个文件夹。", true);
                return;
            }

            if (plan.BlockedNames.Count > 0)
            {
                // 分开计数：被拦的那几份既不在「将覆盖」里，也不该被写出去。
                // 覆盖数为 0 时别写「将覆盖 0 份」——那不是一句话。
                var overwritePart = plan.OverwriteCount > 0
                    ? $"将覆盖 {plan.OverwriteCount} 份同名报名表；"
                    : string.Empty;
                if (!await ConfirmDialog.ShowAsync(
                        this, "导出报名表？",
                        overwritePart
                        + $"另有 {plan.SkippedCount} 份的目标就是导入的原件"
                        + $"（{string.Join("、", plan.BlockedNames)}），将跳过不导出。",
                        "继续导出", "取消", danger: true))
                    return;
            }
            else if (plan.OverwriteCount > 0 && !await ConfirmDialog.ShowAsync(
                         this, "覆盖同名报名表？",
                         $"目标文件夹里已有 {plan.OverwriteCount} 份同名报名表，继续导出会直接覆盖它们。",
                         "覆盖导出", "取消", danger: true))
                return;

            await ViewModel.ExportRegistrationFilesAsync(directory, plan.BlockedNames);
        }
        catch (Exception ex)
        {
            ViewModel.SetStatus(ErrorText.Describe(ex, "导出报名表"), true);
        }
    }

    private void OnClearHighlight(object? sender, RoutedEventArgs e) =>
        ViewModel.ClearScheduleHighlight();

    private void OnSelectRegistration(object? sender, RoutedEventArgs e) =>
        ViewModel.SelectSection("registration");

    private void OnSelectSchedule(object? sender, RoutedEventArgs e) =>
        ViewModel.SelectSection("schedule");

    private void OnSelectDay(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleDayItemViewModel day })
            ViewModel.SelectDay(day);
    }

    private void OnSelectCategory(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleCategoryItemViewModel category })
            ViewModel.SelectScheduleCategory(category);
    }

    public async Task LoadRegistrationForCaptureAsync(string path)
    {
        _closeConfirmed = true;
        await ViewModel.ImportRegistrationFilesAsync([path]);
    }

    /// <summary>走查用：直接导入一份秩序册（名单 + 赛程 + 分组表）。</summary>
    public async Task LoadOrderBookForCaptureAsync(string path)
    {
        _closeConfirmed = true;
        await ViewModel.ImportOrderBookAsync(path);
    }

    /// <summary>走查用：再导入一份报名表，核对团体项目队员是否补进赛程。</summary>
    public async Task LoadRegistrationForTeamCaptureAsync(string path)
    {
        _closeConfirmed = true;
        await ViewModel.ImportRegistrationFilesAsync([path]);
    }

    public async Task LoadScheduleForCaptureAsync(string path)
    {
        _closeConfirmed = true;
        await ViewModel.ImportScheduleFileAsync(path);
    }

    /// <summary>走查用：删掉指定姓名的行，模拟“用户改完超限”后的界面。</summary>
    public void SimulateRemoveForCapture(params string[] names)
    {
        var target = new HashSet<string>(names, StringComparer.Ordinal);
        var rows = ViewModel.SelectedClass?.Athletes
            .Where(r => target.Contains(r.Name))
            .ToList() ?? [];
        foreach (var row in rows)
            ViewModel.RemoveAthlete(row);

        if (rows.Count > 0)
            ViewModel.SetStatus($"走查：已移除 {rows.Count} 行");
    }

    public void LoadDemoForCapture()
    {
        _closeConfirmed = true;
        ViewModel.LoadDemoData();
    }

    /// <summary>截图走查用：为当前班级第一行打开「选择项目」弹窗。</summary>
    public EventEditorWindow? OpenEventDialogForCapture(string slotKey = EventEditorViewModel.IndividualGroupKey)
    {
        var row = ViewModel.SelectedClass?.Athletes.FirstOrDefault();
        var meetClass = ViewModel.SelectedClass?.Source;
        if (row is null || meetClass is null) return null;

        return new EventEditorWindow(meetClass, row, slotKey);
    }

    /// <summary>
    /// 走查用：切到赛程页并选日。dayIndex 沿用「1 = 第一天」的旧语义，
    /// 传 0 才选合成的「全部」（这样既有捕获脚本的行为不变）。
    /// </summary>
    public void ShowScheduleForCapture(int dayIndex = 1, string? categoryKey = null, string? highlightClass = null)
    {
        ViewModel.SelectSection("schedule");

        var day = dayIndex <= 0
            ? ViewModel.ScheduleDays.FirstOrDefault(d => d.IsAllDays)
            : ViewModel.ScheduleDays.Where(d => !d.IsAllDays).ElementAtOrDefault(dayIndex - 1);
        if (day is not null) ViewModel.SelectDay(day);

        if (!string.IsNullOrWhiteSpace(categoryKey))
        {
            var category = ViewModel.ScheduleCategories.FirstOrDefault(c => c.Key == categoryKey);
            if (category is not null) ViewModel.SelectScheduleCategory(category);
        }

        if (!string.IsNullOrWhiteSpace(highlightClass))
        {
            ViewModel.ScheduleHighlightClass = string.Equals(highlightClass, "first", StringComparison.OrdinalIgnoreCase)
                ? ViewModel.AllClasses.FirstOrDefault()
                : ViewModel.AllClasses.FirstOrDefault(c => c.Name == highlightClass)
                  ?? ViewModel.AllClasses.FirstOrDefault();
        }
    }

    /// <summary>走查用：勾上「仅显示该班选手」，核对赛程只留一个班的条目。</summary>
    public void ShowOnlyClassForCapture(bool enabled) =>
        ViewModel.OnlyHighlightedClassEntries = enabled;

    /// <summary>走查用：强制亮出忙碌遮罩，核对长任务提示。</summary>
    public void ShowBusyForCapture(string title = "正在读取秩序册…",
        string detail = "整本秩序册要解析名单、日程与分组表，通常几秒；文件只在本机处理。")
    {
        ViewModel.BusyTitle = title;
        ViewModel.BusyDetail = detail;
        ViewModel.ShowBusyOverlay = true;
    }

    /// <summary>走查用：打开导出设置弹窗（不真正导出）。</summary>
    public MeetExportOptionsWindow? OpenExportDialogForCapture(string? layout = null, string? style = null)
    {
        if (ViewModel.AllClasses.Count == 0) return null;

        var dialog = new MeetExportOptionsWindow(
            ViewModel.Project, ViewModel.SelectedClass?.Name ?? ViewModel.ScheduleHighlightClass?.Name);

        // 走查可以直接指定格式 / 版式 / 样式，省得截图脚本去点下拉。
        if (dialog.DataContext is MeetExportOptionsViewModel options)
        {
            // 走查「全部班级」下的原表高亮互斥（审计 P2-17）：默认导出对象是当前班，
            // 不切过去就看不到置灰状态。
            if (string.Equals(style, "original-all", StringComparison.OrdinalIgnoreCase))
            {
                options.SelectedTarget = options.Targets[0];
                options.SelectedStyle = options.Styles.Single(s => s.Style == MeetExcelStyle.OriginalHighlight);
            }
            else if (string.Equals(style, "original", StringComparison.OrdinalIgnoreCase))
            {
                options.SelectedStyle = options.Styles.Single(s => s.Style == MeetExcelStyle.OriginalHighlight);
            }

            if (string.Equals(layout, "card", StringComparison.OrdinalIgnoreCase))
            {
                options.SelectedFormat = options.Formats.Single(f => f.Format == MeetExportFormat.Pdf);
                options.SelectedLayout = options.Layouts.Single(l => l.Layout == MeetPdfLayout.Card);
            }
            else if (string.Equals(layout, "handbook", StringComparison.OrdinalIgnoreCase))
            {
                options.SelectedFormat = options.Formats.Single(f => f.Format == MeetExportFormat.Pdf);
                options.SelectedLayout = options.Layouts.Single(l => l.Layout == MeetPdfLayout.Handbook);
            }
        }

        return dialog;
    }

    /// <summary>导入学生名单：之后「添加运动员」改为从名单挑选。</summary>
    /// <summary>打开文件选择器并导入名单；被顶栏按钮与「添加运动员」引导共用。</summary>
    private async Task<bool> PickAndImportRosterAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入学生名单",
                AllowMultiple = false,
                FileTypeFilter = [RosterFilter],
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return false;

            await ViewModel.ImportRosterFileAsync(path);
            return ViewModel.HasRoster;
        }
        catch (Exception ex)
        {
            ViewModel.SetStatus(ErrorText.Describe(ex, "导入名单"), true);
            return false;
        }
    }

    private async void OnImportRosterList(object? sender, RoutedEventArgs e) =>
        await PickAndImportRosterAsync();

    /// <summary>
    /// 添加运动员：已导入名单时弹出名单挑选窗（与「选择项目」同构，可多选），
    /// 加入后直接把第一位带进「选择项目」；没有名单时先问要不要导入名单。
    /// </summary>
    private async void OnAddAthlete(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedClass is null)
        {
            ViewModel.SetStatus("先导入或选择一个班级，再添加运动员");
            return;
        }

        if (!ViewModel.HasRoster)
        {
            var import = await ConfirmDialog.ShowAsync(
                this, "还没有导入学生名单",
                "从花名册挑人需要先导入名单（Excel：含“姓名”列，或像“学号 + 姓名”这样两列直接排数据）。\n"
                + "也可以直接添加一行空白，手填姓名与项目。",
                "导入名单", "直接添加一行");
            if (import)
            {
                if (!await PickAndImportRosterAsync() || ViewModel.SelectedClass is null) return;
            }
            else
            {
                ViewModel.AddAthlete();
                return;
            }
        }

        var dialog = new RosterPickWindow(new RosterPickViewModel(
            ViewModel.BuildRosterCandidates(), ViewModel.SelectedClass!.Name));
        await dialog.ShowDialog(this);

        if (dialog.Picked.Count == 0) return;
        var added = ViewModel.AddAthletesFromRoster(dialog.Picked);
        if (added.Count == 0) return;

        // 加入后直接进入项目选择（比赛项目1），省掉“先关闭、再点格子”的一步。
        var first = ViewModel.SelectedClass?.Athletes
            .FirstOrDefault(a => string.Equals(a.Name, added[0], StringComparison.Ordinal));
        if (first is not null)
            await EditEventAsync(first, AthleteRowViewModel.SlotIndividual1);

        if (added.Count > 1)
        {
            ViewModel.SetStatus(
                $"已加入 {added.Count} 人；已为 {added[0]} 选项目，其余 {added.Count - 1} 人可点各自的项目格选择");
        }
    }

    private async void OnRemoveAthlete(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AthleteRowViewModel row }) return;

        var hasContent = !string.IsNullOrWhiteSpace(row.Name) ||
                         AthleteRowViewModel.TeamSlots.Any(s => !string.IsNullOrWhiteSpace(row.RawEvent(s))) ||
                         AthleteRowViewModel.IndividualSlots.Any(s => !string.IsNullOrWhiteSpace(row.RawEvent(s)));

        if (hasContent && !await ConfirmDialog.ShowAsync(
                this, "删除这一行？",
                $"将删除「{(string.IsNullOrWhiteSpace(row.Name) ? "未命名运动员" : row.Name)}」及其已报项目，删除后需要重新填写。",
                "删除", "取消", danger: true))
            return;

        ViewModel.RemoveAthlete(row);
    }

    private void OnClearClassSearch(object? sender, RoutedEventArgs e) => ViewModel.ClearClassSearch();

    /// <summary>移除一个班（及其全部报名），先问后果。</summary>
    private async void OnRemoveClass(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClassItemViewModel item }) return;

        if (!await ConfirmDialog.ShowAsync(
                this, "移除这个班？",
                $"将移除「{item.Name}」及其 {item.AthleteCount} 名运动员与全部报名，无法撤销。",
                "移除", "取消", danger: true))
            return;

        ViewModel.RemoveClass(item.Name);
    }

    private void OnDismissImportNotices(object? sender, RoutedEventArgs e) =>
        ViewModel.DismissImportNotices();

    private async void OnSaveRoster(object? sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>Ctrl+S 保存到原文件。</summary>
    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (ViewModel.IsBusy) return;
        if (!ViewModel.CanSave)
        {
            ViewModel.SetStatus(ViewModel.IsDirty
                ? ViewModel.DescribeUnsavableClasses()
                : "没有需要保存的改动");
            return;
        }

        try
        {
            await ViewModel.SaveToSourceAsync();
        }
        catch (Exception ex)
        {
            ViewModel.SetStatus(ErrorText.Describe(ex, "保存报名表"), true);
        }
    }

    /// <summary>性别变了以后，把新性别里不存在的项目摘掉并说明原因。</summary>
    private void OnGenderAdjusted(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: AthleteRowViewModel row }) return;
        if (string.IsNullOrWhiteSpace(row.LastGenderAdjustment)) return;

        ViewModel.SetStatus($"{DisplayName(row)}：{row.LastGenderAdjustment}");
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: AthleteRowViewModel row }) return;
        var name = row.Name;
        if (string.IsNullOrWhiteSpace(name) || name == name.Trim()) return;

        row.Name = name.Trim();
    }

    private async void OnEditEvent(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string slotKey } button)
            return;

        var row = button.Tag as AthleteRowViewModel
                  ?? button.DataContext as AthleteRowViewModel;
        if (row is null) return;

        await EditEventAsync(row, slotKey);
    }

    /// <summary>为某一行打开「选择项目」并写回结果（表格点格子与名单加入后自动进入共用）。</summary>
    private async Task EditEventAsync(AthleteRowViewModel row, string slotKey)
    {
        var meetClass = ViewModel.SelectedClass?.Source;
        if (meetClass is null) return;

        var dialog = new EventEditorWindow(meetClass, row, slotKey);
        await dialog.ShowDialog(this);

        if (dialog.Result is not { } result) return;

        if (slotKey is EventEditorViewModel.IndividualGroupKey or EventEditorViewModel.TeamGroupKey)
        {
            var events = result.Events ?? [];
            if (slotKey == EventEditorViewModel.TeamGroupKey)
                row.SetTeamEvents(events);
            else
                row.SetIndividualEvents(events);

            ViewModel.SetStatus(events.Count == 0
                ? $"已清空 {DisplayName(row)} 的{(slotKey == EventEditorViewModel.TeamGroupKey ? "团体项目" : "比赛项目")}"
                : $"已为 {DisplayName(row)} 选择 {string.Join("、", events)}");
            return;
        }

        if (result.Clear)
        {
            row.SetEvent(result.SlotKey, string.Empty);
            ViewModel.SetStatus($"已清空 {DisplayName(row)} 的项目格");
            return;
        }

        if (string.IsNullOrWhiteSpace(result.EventName)) return;
        row.SetEvent(result.SlotKey, result.EventName);
        ViewModel.SetStatus($"已为 {DisplayName(row)} 选入「{result.EventName}」");
    }

    private static string DisplayName(AthleteRowViewModel row) =>
        string.IsNullOrWhiteSpace(row.Name) ? "未命名运动员" : row.Name;

    /// <summary>班级名列表的折叠展示：最多点名 5 个，其余合并为计数。</summary>
    private static string SummarizeClassNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return string.Empty;
        var shown = string.Join("、", names.Take(5));
        return $"（{shown}{(names.Count > 5 ? $" 等 {names.Count} 个班" : "")}）";
    }

    /// <summary>新增班的预读名单（预读失败的文件不计入点名，只算进数量）。</summary>
    private static string SummarizeNewClasses(
        IReadOnlyList<(string Path, string? Name)> peeked, IReadOnlySet<string> existingNames)
    {
        var names = peeked
            .Where(p => p.Name is not null && !existingNames.Contains(p.Name))
            .Select(p => p.Name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return SummarizeClassNames(names);
    }
}
