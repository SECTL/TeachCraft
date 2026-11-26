using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Homework.App.Cloud;
using Homework.App.Scanning;
using Homework.Core;
using Homework.Core.Export;
using Homework.Core.Pages;
using Homework.Core.Roster;
using Homework.Core.Scanning;
using TeachCraft.Shared;

namespace Homework.App;

public partial class HomeworkWindow : Window, IScanImportSink
{
    public void CaptureTo(string path, double scale = 1)
    {
        var pixelSize = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private static readonly FilePickerFileType ImageFilter = new("作业页图片")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"],
    };

    private static readonly FilePickerFileType PdfFilter = new("PDF 文件")
    {
        Patterns = ["*.pdf"],
    };

    private const int PdfPreviewDpi = 150;

    // Avalonia 12.1.4 没有 Grab/Grabbing 光标；按下与拖动用四向移动光标表达「可搬动」。
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    private readonly FileProbe _probe = new();
    private readonly IHomeworkRosterBridge _rosterBridge;
    private readonly List<PageThumbnailCache> _caches = [];
    private ScanTaskWindow? _scanWindow;
    private CancellationTokenSource? _importCts;
    private string _busyBaseText = string.Empty;
    private string? _lastOutputDirectory;
    private bool _closeConfirmed;
    private bool _closingHandled;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;

    // 滚轮缩放弹簧：从当前呈现值出发，可被新滚轮事件随时改目标并继承速度。
    private bool _zoomSpringRunning;
    private double _zoomSpringVelocity;
    private double _zoomSpringTarget;
    private Point _zoomAnchorCursor;
    private double _zoomAnchorRelativeX;
    private double _zoomAnchorRelativeY;
    private Size _zoomAnchorViewport;
    private Stopwatch? _zoomSpringClock;

    public HomeworkWindow() : this(new HomeworkViewModel(), null)
    {
    }

    public HomeworkWindow(HomeworkViewModel viewModel, IHomeworkRosterBridge? bridge = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _rosterBridge = bridge ?? NullHomeworkRosterBridge.Instance;
        Opened += (_, _) => WindowFit.Clamp(this);
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;

        // 滚轮走隧道阶段：先于 ScrollViewer 自身的滚轮滚动，Handled 后只缩放不滚动。
        PreviewScroll.AddHandler(PointerWheelChangedEvent, OnPreviewPointerWheel, RoutingStrategies.Tunnel);
        PreviewScroll.AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        PreviewScroll.AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        PreviewScroll.AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        PreviewScroll.AddHandler(PointerCaptureLostEvent, OnPreviewPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private HomeworkViewModel ViewModel => (HomeworkViewModel)DataContext!;

    private BatchWorkspaceViewModel? Workspace => ViewModel.Workspace;

    // ----------------------------- 忙碌状态 -----------------------------

    private void BeginBusy(string text)
    {
        _busyBaseText = text;
        ViewModel.BeginBusy(text);
        BusyMessageText.Text = text;
    }

    private void EndBusy()
    {
        ViewModel.EndBusy();
        BusyMessageText.Text = string.Empty;
        _busyBaseText = string.Empty;
    }

    private void ReportProgress(int percent) =>
        BusyMessageText.Text = $"{_busyBaseText} {percent}%";

    private void OnCancelBusy(object? sender, RoutedEventArgs e)
    {
        // 真正的中断走 CTS；VM 的 RequestCancel 只负责把「正在取消…」写进状态文案。
        _importCts?.Cancel();
        ViewModel.RequestCancel();
        BusyMessageText.Text = ViewModel.BusyText;
    }

    // ----------------------------- 导航 -----------------------------

    private void OnNewBatch(object? sender, RoutedEventArgs e) => ViewModel.BeginNewBatch();

    private void OnOpenScanTask(object? sender, RoutedEventArgs e)
    {
        if (_scanWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var preferences = new ScanTaskPreferencesStore().Load();
        var viewModel = new ScanTaskViewModel(ScannerServiceFactory.Create(), this, preferences);
        var window = new ScanTaskWindow(viewModel);
        _scanWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_scanWindow, window)) _scanWindow = null;
        };
        window.Show(this);
    }

    private void OnBack(object? sender, RoutedEventArgs e) => ViewModel.BackToHome();

    private void OnOpenBatch(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: BatchSummaryViewModel summary })
            ViewModel.OpenBatch(summary);
    }

    // ----------------------------- 工作区筛选与选择 -----------------------------

    private void OnShowPending(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws) ws.ShowAllPages = false;
    }

    private void OnShowAllPages(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws) ws.ShowAllPages = true;
    }

    private void OnShowUnmatched(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws) ws.ShowOnlyUnmatchedStudents = true;
    }

    private void OnShowMatched(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws) ws.ShowOnlyMatchedStudents = true;
    }

    private void OnShowAllStudents(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws) ws.ShowAllStudents = true;
    }

    private void OnSelectPage(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PageItemViewModel page })
            Workspace?.SelectPage(page);
    }

    private void OnMatchStudent(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StudentRowViewModel student })
            Workspace?.MatchStudent(student);
    }

    private void OnDeletePage(object? sender, RoutedEventArgs e) => Workspace?.DeleteSelectedPage();

    private void OnUnmatchPage(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PageItemViewModel page }) Workspace?.UnmatchPage(page);
    }

    private void OnUnmatchStudent(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { SelectedStudent: { } student } ws) ws.UnmatchStudent(student);
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => Workspace?.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => Workspace?.Redo();

    private void OnZoomIn(object? sender, RoutedEventArgs e) =>
        ZoomKeepingCenter(() => Workspace?.ZoomIn());

    private void OnZoomOut(object? sender, RoutedEventArgs e) =>
        ZoomKeepingCenter(() => Workspace?.ZoomOut());

    private void OnZoomFit(object? sender, RoutedEventArgs e) =>
        ZoomKeepingCenter(() => Workspace?.ZoomToFit());

    /// <summary>
    /// 缩放前后保持视口中心对应的图像位置不变，避免放大时图像从左上角往右下膨胀。
    /// 布局更新后再写 Offset；内容比视口小时钳到 0。
    /// </summary>
    private void ZoomKeepingCenter(Action zoom)
    {
        if (PreviewScroll is not { } scroll)
        {
            zoom();
            return;
        }

        var extent = scroll.Extent;
        var viewport = scroll.Viewport;
        var relativeX = extent.Width > 0
            ? (scroll.Offset.X + viewport.Width / 2) / extent.Width
            : 0.5;
        var relativeY = extent.Height > 0
            ? (scroll.Offset.Y + viewport.Height / 2) / extent.Height
            : 0.5;

        zoom();

        Dispatcher.UIThread.Post(() =>
        {
            var newExtent = scroll.Extent;
            var x = relativeX * newExtent.Width - viewport.Width / 2;
            var y = relativeY * newExtent.Height - viewport.Height / 2;
            scroll.Offset = new Vector(
                Math.Clamp(x, 0, Math.Max(0, newExtent.Width - viewport.Width)),
                Math.Clamp(y, 0, Math.Max(0, newExtent.Height - viewport.Height)));
        }, DispatcherPriority.Loaded);
    }

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Workspace is not { } ws) return;
        ws.PreviewViewportWidth = e.NewSize.Width;
        ws.PreviewViewportHeight = e.NewSize.Height;
    }

    // ----------------------------- 预览平移与滚轮缩放 -----------------------------

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Workspace is not { IsSelectingFillInRegion: false }) return;
        if (!e.GetCurrentPoint(PreviewScroll).Properties.IsLeftButtonPressed) return;

        // 开始拖动即停掉缩放弹簧，避免逐帧的偏移校正与 1:1 平移互相打架。
        _zoomSpringRunning = false;
        _isPanning = true;
        _panStart = e.GetPosition(PreviewScroll);
        _panStartOffset = PreviewScroll.Offset;
        e.Pointer.Capture(PreviewScroll);
        PreviewScroll.Cursor = PanCursor;
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var delta = e.GetPosition(PreviewScroll) - _panStart;
        // 同步写 Offset：拖动方向与内容移动一致，不排队到 Dispatcher，避免每帧卡顿。
        PreviewScroll.Offset = new Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y);
        e.Handled = true;
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        EndPreviewPan();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        EndPreviewPan();

    private void EndPreviewPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        PreviewScroll.Cursor = ArrowCursor;
    }

    private void OnPreviewPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Workspace is not { IsSelectingFillInRegion: false } ws) return;
        if (e.Delta.Y == 0) return;

        var scroll = PreviewScroll;
        var extent = scroll.Extent;
        var viewport = scroll.Viewport;
        if (extent.Width <= 0 || extent.Height <= 0) return;

        var cursor = e.GetPosition(scroll);
        // 记录光标下的内容相对位置，缩放后按同一比例还原，使该点仍停在光标下。
        var relativeX = (scroll.Offset.X + cursor.X) / extent.Width;
        var relativeY = (scroll.Offset.Y + cursor.Y) / extent.Height;

        const double factor = 1.1;
        var target = ZoomMath.Clamp(ws.ZoomPercent * (e.Delta.Y > 0 ? factor : 1.0 / factor));

        if (MotionPreference.ReduceMotion)
        {
            ws.SetZoomPercent(target);
            Dispatcher.UIThread.Post(
                () => CorrectZoomOffset(scroll, cursor, relativeX, relativeY, viewport),
                DispatcherPriority.Loaded);
        }
        else
        {
            StartZoomSpring(ws, scroll, target, cursor, relativeX, relativeY, viewport);
        }

        e.Handled = true;
    }

    private void StartZoomSpring(
        BatchWorkspaceViewModel ws, ScrollViewer scroll, double target,
        Point cursor, double relativeX, double relativeY, Size viewport)
    {
        // 新滚轮事件只换目标与锚点；当前呈现值从 ws.ZoomPercent 读，速度继承，故可随时打断重定向。
        _zoomSpringTarget = target;
        _zoomAnchorCursor = cursor;
        _zoomAnchorRelativeX = relativeX;
        _zoomAnchorRelativeY = relativeY;
        _zoomAnchorViewport = viewport;
        if (_zoomSpringRunning) return;

        _zoomSpringRunning = true;
        _zoomSpringVelocity = 0;
        _zoomSpringClock = Stopwatch.StartNew();
        Dispatcher.UIThread.Post(() => TickZoomSpring(ws, scroll), DispatcherPriority.Render);
    }

    private void TickZoomSpring(BatchWorkspaceViewModel ws, ScrollViewer scroll)
    {
        if (!_zoomSpringRunning || _zoomSpringClock is null) return;

        var dt = Math.Clamp(_zoomSpringClock.Elapsed.TotalSeconds, 1.0 / 60, 1.0 / 30);
        _zoomSpringClock.Restart();

        // 临界阻尼弹簧：omega=2/response，无回弹；从当前值积分，速度随目标变化连续继承。
        const double response = 0.3;
        var omega = 2 / response;
        var x = omega * dt;
        var exp = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
        var change = ws.ZoomPercent - _zoomSpringTarget;
        var temp = (_zoomSpringVelocity + omega * change) * dt;
        _zoomSpringVelocity = (_zoomSpringVelocity - omega * temp) * exp;
        var next = ZoomMath.Clamp(_zoomSpringTarget + (change + temp) * exp);

        var settled = Math.Abs(next - _zoomSpringTarget) < 0.05
                      && Math.Abs(_zoomSpringVelocity) < 0.05;
        if (settled)
        {
            next = _zoomSpringTarget;
            _zoomSpringVelocity = 0;
        }

        ws.SetZoomPercent(next);

        var cursor = _zoomAnchorCursor;
        var relativeX = _zoomAnchorRelativeX;
        var relativeY = _zoomAnchorRelativeY;
        var viewport = _zoomAnchorViewport;
        Dispatcher.UIThread.Post(
            () => CorrectZoomOffset(scroll, cursor, relativeX, relativeY, viewport),
            DispatcherPriority.Loaded);

        if (settled)
        {
            _zoomSpringRunning = false;
            _zoomSpringClock.Stop();
        }
        else
        {
            Dispatcher.UIThread.Post(() => TickZoomSpring(ws, scroll), DispatcherPriority.Render);
        }
    }

    private static void CorrectZoomOffset(
        ScrollViewer scroll, Point cursor, double relativeX, double relativeY, Size viewport)
    {
        var newExtent = scroll.Extent;
        var x = relativeX * newExtent.Width - cursor.X;
        var y = relativeY * newExtent.Height - cursor.Y;
        scroll.Offset = new Vector(
            Math.Clamp(x, 0, Math.Max(0, newExtent.Width - viewport.Width)),
            Math.Clamp(y, 0, Math.Max(0, newExtent.Height - viewport.Height)));
    }

    // ----------------------------- 填涂识别与框选 -----------------------------

    private async void OnRunFillIn(object? sender, RoutedEventArgs e) => await RunFillInAsync();

    private async Task RunFillInAsync()
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        using var cts = new CancellationTokenSource();
        _importCts = cts;
        BeginBusy("正在识别填涂…");
        try
        {
            var progress = new Progress<int>(ReportProgress);
            await ws.RunFillInRecognitionAsync(progress, cts.Token);
        }
        catch (Exception ex)
        {
            ws.SetStatus(ErrorText.Describe(ex, "识别填涂"));
        }
        finally
        {
            _importCts = null;
            EndBusy();
        }
    }

    private void OnStartRegionSelection(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        Workspace?.StartFillInRegionSelection();
    }

    private void OnApplyRegion(object? sender, RoutedEventArgs e)
    {
        if (Workspace is not { } ws) return;
        ws.ApplyFillInRegion(ws.ApplyRegionPerPage);
    }

    private void OnCancelRegion(object? sender, RoutedEventArgs e) => Workspace?.CancelFillInRegion();

    private void OnClearRegion(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        Workspace?.ClearFillInRegion();
    }

    private async void OnOpenTemplateDialog(object? sender, RoutedEventArgs e)
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;
        var dialog = new FillInTemplateWindow(new FillInTemplateViewModel(ws.Batch));
        await dialog.ShowDialog<bool>(this);
    }

    // ----------------------------- 导入 -----------------------------

    private async void OnImportImages(object? sender, RoutedEventArgs e) => await ImportImagesAsync();

    private async Task ImportImagesAsync()
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择作业页图片（可多选）",
            AllowMultiple = true,
            FileTypeFilter = [ImageFilter],
        });
        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;

        var cache = new PageThumbnailCache();
        _caches.Add(cache);
        var cts = new CancellationTokenSource();
        _importCts = cts;
        BeginBusy("正在导入作业页…");
        try
        {
            var progress = new Progress<int>(ReportProgress);
            await Task.Run(() => ws.ImportImages(paths, cache, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            ws.SetStatus("已取消导入作业页。");
        }
        catch (Exception ex)
        {
            ws.SetStatus(ErrorText.Describe(ex, "导入作业页"));
        }
        finally
        {
            cts.Dispose();
            _importCts = null;
            EndBusy();
        }
    }

    private async void OnImportPdf(object? sender, RoutedEventArgs e) => await ImportPdfAsync();

    private async Task ImportPdfAsync()
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 PDF 作业（单份）",
            AllowMultiple = false,
            FileTypeFilter = [PdfFilter],
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var cache = new PageThumbnailCache();
        _caches.Add(cache);
        var cts = new CancellationTokenSource();
        _importCts = cts;
        BeginBusy("正在导入 PDF…");
        try
        {
            var progress = new Progress<int>(ReportProgress);
            await Task.Run(() => ws.ImportPdf(path, cache, PdfPreviewDpi, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
            ws.SetStatus("已取消导入 PDF。");
        }
        catch (Exception ex)
        {
            ws.SetStatus(ErrorText.Describe(ex, "导入 PDF"));
        }
        finally
        {
            cts.Dispose();
            _importCts = null;
            EndBusy();
        }
    }

    // ----------------------------- 扫描任务导入 -----------------------------

    public bool HasCurrentBatch => Workspace is not null;

    public async Task<ScanImportOutcome> ImportAsync(
        IReadOnlyList<string> paths,
        bool createNewBatch,
        CancellationToken token)
    {
        if (paths.Count == 0)
            return ScanImportOutcome.Failed("没有可导入的扫描件。");

        if (createNewBatch || Workspace is null)
            ViewModel.BeginNewBatch();

        var workspace = Workspace;
        if (workspace is null)
            return ScanImportOutcome.Failed("无法创建作业批次。");

        var cache = new PageThumbnailCache();
        _caches.Add(cache);
        var before = workspace.Batch.Pages.Count;

        try
        {
            await Task.Run(() =>
            {
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    var kind = ScanFileClassifier.KindOf(path);
                    if (kind == ScanFileKind.Pdf)
                    {
                        if (!workspace.ImportPdf(path, cache, PdfPreviewDpi, null, token))
                            throw new HomeworkException(workspace.StatusText);
                    }
                    else if (kind == ScanFileKind.Image)
                    {
                        if (!workspace.ImportImages([path], cache, null, token))
                            throw new HomeworkException(workspace.StatusText);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return ScanImportOutcome.Failed("已取消导入扫描件。");
        }
        catch (Exception ex)
        {
            return ScanImportOutcome.Failed(ErrorText.Describe(ex, "导入扫描件"));
        }

        var added = workspace.Batch.Pages.Count - before;
        if (added == 0)
            return ScanImportOutcome.Failed(workspace.StatusText);

        return ScanImportOutcome.Ok(added, workspace.Batch.Name);
    }

    private async void OnImportRoster(object? sender, RoutedEventArgs e) => await ImportRosterAsync();

    private async Task ImportRosterAsync()
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        var dialog = new RosterImportWindow();
        if (!await dialog.ShowDialog<bool>(this)) return;

        if (dialog.ImportedResult is { } imported)
            ws.ApplyRoster(imported);
        else if (dialog.PasteResult is { } pasted)
            ws.ApplyPaste(pasted);
    }

    private async void OnPullSharedRoster(object? sender, RoutedEventArgs e) => await PullSharedRosterAsync();

    /// <summary>
    /// 从共享库拉取班级名单。辑册只有读的能力：拉到的结果经既有 ApplyRoster
    /// 落进当前批次，不臆造性别等可选字段，也不改动共享库里的原件。
    /// </summary>
    private async Task PullSharedRosterAsync()
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        // 始终打开选择器：未登录 / 未开同步时由它自己的引导与登录入口接管，
        // 取消则原样返回，批次不会被改动。
        var pickerViewModel = new HomeworkRosterPickerViewModel(_rosterBridge);
        var picker = new SharedRosterPickerWindow(pickerViewModel, refreshOnOpen: true)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (!await picker.ShowDialog<bool>(this)) return;
        var result = picker.PulledResult;
        if (result is null) return;

        if (ws.Batch.Students.Count > 0)
        {
            var replace = await ConfirmDialog.ShowAsync(
                this, "替换当前名单？",
                "当前名单与已做的匹配会被替换，未导出的改动将丢失。仍要导入吗？",
                "替换", "取消", danger: true);
            if (!replace) return;
        }

        ws.ApplyRoster(result);
    }

    // ----------------------------- 提交 / 放弃 -----------------------------

    private async void OnCommit(object? sender, RoutedEventArgs e) => await CommitAsync();

    private async Task CommitAsync()
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        if (!ws.CanCommit)
        {
            ws.SetStatus(ws.CommitBlockedReason);
            return;
        }

        if (ws.IncompleteStudentsCount > 0)
        {
            var proceed = await ConfirmDialog.ShowAsync(
                this, "有学生未交齐",
                $"名单里有 {ws.IncompleteStudentsCount} 位学生没有交齐（共缺 {ws.MissingPageCount} 页），导出时只按现有页生成。仍要提交吗？",
                "仍要提交", "返回检查", danger: false);
            if (!proceed) return;
        }

        var optionsVm = new ExportOptionsViewModel
        {
            OutputDirectory = _lastOutputDirectory ?? DefaultOutputDirectory(),
        };
        optionsVm.UpdatePreview(ws.Batch, _probe);

        var dialog = new ExportOptionsWindow(optionsVm);
        var options = await dialog.ShowDialog<HomeworkExportOptions?>(this);
        if (options is null) return;

        _lastOutputDirectory = options.OutputDirectory;

        using var exportCts = new CancellationTokenSource();
        _importCts = exportCts;
        try
        {
            // 弹窗里已经预览过一次；这里按最终选项重建计划，导出器只认这份计划。
            var plan = ws.BuildExportPlan(options, _probe);
            BeginBusy("正在导出…");
            var progress = new Progress<int>(ReportProgress);
            var result = await Task.Run(() =>
                HomeworkExporter.Execute(ws.Batch, options, plan, progress, exportCts.Token));

            ws.MarkCommitted();
            var warning = result.Warnings.Count > 0
                ? " 提示：" + string.Join(" ", result.Warnings)
                : string.Empty;
            ws.SetStatus($"导出完成：写出 {result.FilesWritten} 个文件，涉及 {result.StudentsWritten} 位学生。{warning}");
        }
        catch (HomeworkException ex)
        {
            var message = ErrorText.Describe(ex, "导出");
            ws.SetStatus(message);
            await ConfirmDialog.ShowAsync(this, "导出失败", message, "知道了", "关闭", danger: false);
        }
        catch (OperationCanceledException)
        {
            ws.SetStatus("已取消导出。");
        }
        catch (Exception ex)
        {
            var message = ErrorText.Describe(ex, "导出");
            ws.SetStatus(message);
            await ConfirmDialog.ShowAsync(this, "导出失败", message, "知道了", "关闭", danger: false);
        }
        finally
        {
            _importCts = null;
            EndBusy();
        }
    }

    private async void OnAbandon(object? sender, RoutedEventArgs e)
    {
        var ws = Workspace;
        if (ws is null || ViewModel.IsBusy) return;

        if (!await ConfirmDialog.ShowAsync(
                this, "放弃批次？",
                "已匹配与导入的页都会丢弃，原始文件不会被删除。",
                "放弃", "取消", danger: true))
            return;

        ws.Abandon();
    }

    private static string DefaultOutputDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var root = !string.IsNullOrWhiteSpace(documents) ? documents : desktop;
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), "TeachCraft", "Homework", "导出")
            : Path.Combine(root, "TeachCraft", "辑册导出");
    }

    // ----------------------------- 键盘 -----------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // 文本框里的 Ctrl+Z / Delete 交给文本框自己处理。
        if (e.Source is TextBox) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z)
            {
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Workspace?.Redo();
                else Workspace?.Undo();
                return;
            }
            if (e.Key == Key.Y)
            {
                e.Handled = true;
                Workspace?.Redo();
                return;
            }
        }

        if (ViewModel.IsBusy) return;

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            Workspace?.DeleteSelectedPage();
        }
        else if (e.Key == Key.PageUp)
        {
            e.Handled = true;
            MoveSelection(-1);
        }
        else if (e.Key == Key.PageDown)
        {
            e.Handled = true;
            MoveSelection(1);
        }
    }

    private void MoveSelection(int delta)
    {
        var ws = Workspace;
        if (ws is null) return;
        var pages = ws.VisiblePages;
        if (pages.Count == 0) return;

        var current = ws.SelectedPage;
        var index = current is null ? -1 : pages.IndexOf(current);
        var next = Math.Clamp(index + delta, 0, pages.Count - 1);
        ws.SelectPage(pages[next]);
    }

    // ----------------------------- 关窗守卫 -----------------------------

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        if (!ViewModel.ShouldConfirmClose()) return;

        e.Cancel = true;
        if (_closingHandled) return;
        _closingHandled = true;

        Dispatcher.UIThread.Post(async () =>
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                this, "关闭辑册？",
                "当前批次还有未提交的作业页，关闭后本次匹配与导入不会保留。原始文件不会被删除。",
                "放弃并关闭", "继续编辑", danger: true);
            if (confirmed)
            {
                _closeConfirmed = true;
                Close();
            }
            else
            {
                _closingHandled = false;
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanWindow?.Close();
        _scanWindow = null;

        // 缩略图临时夹只在窗口关闭时回收：预览与缩略图在整个会话里都可能被引用。
        foreach (var cache in _caches) cache.Dispose();
        _caches.Clear();
        base.OnClosed(e);
    }
}
