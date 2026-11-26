using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Tools;
using TeachCraft.Shared;

namespace TeachCraft.App.Views;

public partial class ExamPaperLayoutPanelView : UserControl
{
    public event EventHandler? FilePickRequested;
    public event EventHandler? RunRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? CancelRequested;

    private ToolItemViewModel? _viewModel;

    public ExamPaperLayoutPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        DetachedFromVisualTree += (_, _) => CancelPreview();
        AttachedToVisualTree += (_, _) => RequestPreview();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
            {
                if (IsVisible) RequestPreview();
                else CancelPreview();
            }
        };
    }

    private void HookViewModel()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ToolItemViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RequestPreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolItemViewModel.Files)
            or nameof(ToolItemViewModel.FileSummary)
            or nameof(ToolItemViewModel.SelectedExamPaperLayoutMode))
            RequestPreview();
        else if (e.PropertyName == nameof(ToolItemViewModel.SelectedExamPaperDpi))
        {
            if (_viewModel is { } tool)
                tool.ExamPaperPreviewCaption = $"{tool.SelectedExamPaperLayoutMode} · 最终 {tool.SelectedExamPaperDpi}";
        }
    }

    private void RequestPreview()
    {
        if (_viewModel is not { IsExamPaperLayout: true } tool || tool.Files.Count != 1)
        {
            CancelPreview();
            if (_viewModel is { } emptyTool)
            {
                emptyTool.ExamPaperPreviewImage = null;
                emptyTool.ExamPaperPreviewCaption = string.Empty;
                emptyTool.ExamPaperPreviewDetail = string.Empty;
                emptyTool.ExamPaperPreviewEmptyText = "选择 PDF 后预览排版效果";
            }
            return;
        }

        var request = new PreviewRequest(tool.Files[0], tool.SelectedExamPaperLayoutMode,
            tool.ToRequest(string.Empty).ExamPaperDpi);
        if (_lastRequest is { } previousRequest && previousRequest.Path == request.Path && previousRequest.Mode == request.Mode)
        {
            _lastRequest = request;
            tool.ExamPaperPreviewCaption =
                $"{request.Mode} · 最终 {request.Dpi} DPI（预览 {ExamPaperLayoutPreviewRenderer.PreviewDpi} DPI）";
            return;
        }
        if (request == _lastRequest) return;
        _lastRequest = request;
        tool.ExamPaperPreviewCaption = $"{request.Mode} · 最终 {request.Dpi} DPI";
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCancellation, cancellation);
        previous?.Cancel();
        _ = RenderPreviewDebouncedAsync(tool, request, cancellation);
    }

    private PreviewRequest? _lastRequest;
    private CancellationTokenSource? _previewCancellation;

    private async Task RenderPreviewDebouncedAsync(
        ToolItemViewModel tool, PreviewRequest request, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(200, token).ConfigureAwait(true);
            tool.IsExamPaperPreviewBusy = true;
            var mode = request.Mode == "小册子" ? ExamPaperLayoutMode.Booklet : ExamPaperLayoutMode.TwoUp;
            var preview = await Task.Run(() => ExamPaperLayoutPreviewRenderer.Render(request.Path, mode, request.Dpi), token)
                .ConfigureAwait(true);
            if (token.IsCancellationRequested || !ReferenceEquals(_viewModel, tool)) return;

            using var stream = new MemoryStream(preview.Png);
            tool.ExamPaperPreviewImage = new Avalonia.Media.Imaging.Bitmap(stream);
            tool.ExamPaperPreviewCaption = preview.Caption;
            tool.ExamPaperPreviewDetail = preview.Detail;
            tool.ExamPaperPreviewEmptyText = "预览完成";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !ReferenceEquals(_viewModel, tool)) return;
            tool.ExamPaperPreviewImage = null;
            tool.ExamPaperPreviewCaption = string.Empty;
            tool.ExamPaperPreviewDetail = string.Empty;
            tool.ExamPaperPreviewEmptyText = ErrorText.Describe(ex, "生成预览");
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _previewCancellation), cancellation))
            {
                tool.IsExamPaperPreviewBusy = false;
                Interlocked.CompareExchange(ref _previewCancellation, null, cancellation);
            }
            cancellation.Dispose();
        }
    }

    private void CancelPreview()
    {
        var cts = Interlocked.Exchange(ref _previewCancellation, null);
        cts?.Cancel();
        _lastRequest = null;
        if (_viewModel is { } tool) tool.IsExamPaperPreviewBusy = false;
    }

    private void OnPick(object? sender, RoutedEventArgs e) => FilePickRequested?.Invoke(this, EventArgs.Empty);
    private void OnRun(object? sender, RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancelTool(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnClearFiles(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ReplaceFiles(Array.Empty<string>());
    }

    private sealed record PreviewRequest(string Path, string Mode, int Dpi);
}
