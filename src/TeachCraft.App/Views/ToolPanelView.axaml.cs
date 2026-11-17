using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace TeachCraft.App.Views;

public partial class ToolPanelView : UserControl
{
    public event EventHandler? FilePickRequested;
    public event EventHandler? RunRequested;
    public event EventHandler? CloseRequested;

    /// <summary>「取消」：由 MainWindow 持有真正的 CancellationTokenSource。</summary>
    public event EventHandler? CancelRequested;

    public ToolPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookTool();
    }

    /// <summary>
    /// 水印预览：第一张图按 240px 宽缩略渲染，参数一变就重画。
    /// 纯 CPU 工作放后台，避免拖滑块时卡手。
    /// </summary>
    private ToolItemViewModelRef? _hooked;
    private bool _previewQueued;

    private void HookTool()
    {
        if (_hooked is { } old) old.Tool.PropertyChanged -= OnToolPropertyChanged;
        _hooked = null;

        if (DataContext is not ViewModels.ToolItemViewModel tool) return;

        _hooked = new ToolItemViewModelRef(tool);
        tool.PropertyChanged += OnToolPropertyChanged;
        SchedulePreview();
    }

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只关心影响水印外观的几项 + 文件列表变化。
        if (e.PropertyName is nameof(ViewModels.ToolItemViewModel.WatermarkScale)
            or nameof(ViewModels.ToolItemViewModel.WatermarkOpacity)
            or nameof(ViewModels.ToolItemViewModel.SelectedWatermarkPosition)
            or nameof(ViewModels.ToolItemViewModel.WatermarkText)
            or nameof(ViewModels.ToolItemViewModel.Files)
            or nameof(ViewModels.ToolItemViewModel.FileSummary))
        {
            SchedulePreview();
        }
    }

    private void SchedulePreview()
    {
        if (_previewQueued) return;
        _previewQueued = true;

        // 拖滑块会连发几十次变更：合并到下一次调度再渲染，与归位的滑块策略一致。
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            _previewQueued = false;
            await RenderWatermarkPreviewAsync();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task RenderWatermarkPreviewAsync()
    {
        if (_hooked?.Tool is not { } tool) return;
        if (tool.Kind != Core.Tools.ToolKind.ImageWatermark) return;

        var hint = WatermarkPreviewHint;
        if (tool.Files.Count == 0)
        {
            WatermarkPreview.Source = null;
            hint.Text = "选择图片后显示预览";
            hint.IsVisible = true;
            return;
        }

        var path = tool.Files[0];
        var text = tool.WatermarkText;
        var position = tool.SelectedWatermarkPosition;
        var opacity = tool.WatermarkOpacity;
        var scale = tool.WatermarkScale;

        try
        {
            var png = await Task.Run(() => Core.Tools.ImageToolbox.RenderWatermarkPreview(
                path, text, position, opacity, scale, previewWidth: 240));
            if (png is null) return;

            using var memory = new MemoryStream(png);
            var bitmap = new Bitmap(memory);

            (WatermarkPreview.Source as IDisposable)?.Dispose();
            WatermarkPreview.Source = bitmap;
            hint.IsVisible = false;
        }
        catch
        {
            // 预览失败不影响正式处理，保持提示即可。
            hint.Text = "预览暂不可用";
            hint.IsVisible = true;
        }
    }

    /// <summary>弱引用包装：DataContext 换工具时不阻止旧 VM 回收。</summary>
    private sealed class ToolItemViewModelRef
    {
        public ToolItemViewModelRef(ViewModels.ToolItemViewModel tool) => Tool = tool;
        public ViewModels.ToolItemViewModel Tool { get; }
    }

    private void OnPick(object? sender, RoutedEventArgs e) => FilePickRequested?.Invoke(this, EventArgs.Empty);
    private void OnRun(object? sender, RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancelTool(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnClearFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ToolItemViewModel tool) tool.ReplaceFiles(Array.Empty<string>());
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ViewModels.FileEntryViewModel entry &&
            DataContext is ViewModels.ToolItemViewModel tool)
        {
            tool.MoveFile(entry, -1);
        }
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ViewModels.FileEntryViewModel entry &&
            DataContext is ViewModels.ToolItemViewModel tool)
        {
            tool.MoveFile(entry, 1);
        }
    }

    private void OnRemoveFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ViewModels.FileEntryViewModel entry &&
            DataContext is ViewModels.ToolItemViewModel tool)
        {
            tool.RemoveFile(entry);
        }
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.ToolItemViewModel tool || string.IsNullOrEmpty(tool.LastOutputPath))
            return;

        try
        {
            var directory = System.IO.Directory.Exists(tool.LastOutputPath)
                ? tool.LastOutputPath
                : System.IO.Path.GetDirectoryName(tool.LastOutputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // 打不开资源管理器时保持安静，结果文件仍然在磁盘上。
        }
    }
}
