using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TeachCraft.Shared;

namespace Homework.App;

/// <summary>
/// 生成填涂模板弹窗。设置直接绑定 <see cref="FillInTemplateViewModel"/>，窗口只负责选路径、
/// 触发渲染 / 预览与打开成品；关闭时回传「本次是否生成过」，供调用方决定是否刷新状态。
/// </summary>
public sealed partial class FillInTemplateWindow : Window
{
    private readonly FillInTemplateViewModel _viewModel;
    private CancellationTokenSource? _previewCts;
    private bool _hasGenerated;

    public FillInTemplateWindow()
        : this(new FillInTemplateViewModel(Homework.Core.Models.HomeworkBatch.Create("模板")))
    {
    }

    public FillInTemplateWindow(FillInTemplateViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;

        Opened += async (_, _) =>
        {
            WindowFit.Clamp(this);
            await _viewModel.LoadPreviewAsync();
        };

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    /// <summary>列数 / 范围 / 格式 / 班级任一变化都重画预览；200ms 防抖，取消上一次未落地的渲染。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FillInTemplateViewModel.Columns)
            or nameof(FillInTemplateViewModel.Scope)
            or nameof(FillInTemplateViewModel.Format)
            or nameof(FillInTemplateViewModel.SelectedClass)))
            return;

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = RefreshPreviewDebouncedAsync(cts.Token);
    }

    private async Task RefreshPreviewDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            token.ThrowIfCancellationRequested();
            await _viewModel.LoadPreviewAsync();
        }
        catch (OperationCanceledException)
        {
            // 被更新的设置取代，丢弃这次渲染。
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Scope == FillInTemplateScope.AllClasses)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择模板输出文件夹",
                AllowMultiple = false,
            });
            var folder = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(folder)) _viewModel.OutputPath = folder;
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择模板输出文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("模板文件") { Patterns = ["*.png", "*.pdf"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) _viewModel.OutputPath = path;
    }

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        await _viewModel.GenerateAsync();
        if (_viewModel.StatusText.StartsWith("已生成", StringComparison.Ordinal))
            _hasGenerated = true;
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        var path = _viewModel.OutputPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // UseShellExecute 在各平台交给系统默认程序 / 文件管理器，不是 Windows 专属调用。
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = ErrorText.Describe(ex, "打开成品");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(_hasGenerated);
}
