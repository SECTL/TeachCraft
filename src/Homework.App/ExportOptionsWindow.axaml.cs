using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Homework.Core;
using Homework.Core.Export;
using TeachCraft.Shared;

namespace Homework.App;

/// <summary>
/// 导出设置弹窗。左侧设置直接绑定 <see cref="ExportOptionsViewModel"/>，右侧预检与结构示意
/// 由本窗口从 ViewModel 的公开状态派生——规划器已经由调用方跑过，这里绝不重跑、绝不落盘。
/// </summary>
public sealed partial class ExportOptionsWindow : Window, INotifyPropertyChanged
{
    private readonly ExportOptionsViewModel _viewModel;
    private string _modeExplanation = string.Empty;
    private string _treeSketch = string.Empty;
    private IReadOnlyList<string> _diagnosticLines = [];
    private string _dialogError = string.Empty;
    private bool _hasConflicts;
    private bool _hasWarnings;
    private bool _hasDiagnostics;
    private bool _hasTreeSketch;
    private bool _hasDialogError;

    public ExportOptionsWindow() : this(new ExportOptionsViewModel())
    {
    }

    public ExportOptionsWindow(ExportOptionsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ExportOptionsViewModel.Conflicts)
                or nameof(ExportOptionsViewModel.Warnings)
                or nameof(ExportOptionsViewModel.Mode))
                RefreshRightPanel();
        };

        RefreshRightPanel();

        Opened += (_, _) =>
        {
            WindowFit.Clamp(this);
            // 首次打开时把当前模式选上；SelectionChanged 会把同一个值写回 ViewModel（无副作用）。
            if (ModeList.SelectedItem is null)
                ModeList.SelectedItem = _viewModel.Modes.FirstOrDefault(m => m.Mode == _viewModel.Mode);
        };
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public string ModeExplanation
    {
        get => _modeExplanation;
        private set => Set(ref _modeExplanation, value);
    }

    public string TreeSketch
    {
        get => _treeSketch;
        private set => Set(ref _treeSketch, value);
    }

    public IReadOnlyList<string> DiagnosticLines
    {
        get => _diagnosticLines;
        private set => Set(ref _diagnosticLines, value);
    }

    public string DialogError
    {
        get => _dialogError;
        private set
        {
            if (Set(ref _dialogError, value)) HasDialogError = !string.IsNullOrWhiteSpace(value);
        }
    }

    public bool HasConflicts
    {
        get => _hasConflicts;
        private set => Set(ref _hasConflicts, value);
    }

    public bool HasWarnings
    {
        get => _hasWarnings;
        private set => Set(ref _hasWarnings, value);
    }

    public bool HasDiagnostics
    {
        get => _hasDiagnostics;
        private set => Set(ref _hasDiagnostics, value);
    }

    public bool HasTreeSketch
    {
        get => _hasTreeSketch;
        private set => Set(ref _hasTreeSketch, value);
    }

    public bool HasDialogError
    {
        get => _hasDialogError;
        private set => Set(ref _hasDialogError, value);
    }

    private void OnModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModeList.SelectedItem is ExportModeOption option)
            _viewModel.Mode = option.Mode;
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择作业导出文件夹",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            _viewModel.OutputDirectory = path;
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        try
        {
            Close(_viewModel.BuildOptions());
        }
        catch (HomeworkException ex)
        {
            // 规划阶段已经把可读原因放进 Conflicts / PreviewText；这里只兜住 BuildOptions 的意外失败。
            DialogError = ErrorText.Describe(ex, "生成导出设置");
        }
        catch (Exception ex)
        {
            DialogError = ErrorText.Describe(ex, "生成导出设置");
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void RefreshRightPanel()
    {
        ModeExplanation = _viewModel.Mode switch
        {
            HomeworkExportMode.MergePdf =>
                "每位学生已匹配的页合并成一份 PDF，按班级分文件夹存放。",
            HomeworkExportMode.ArchiveWithIndex =>
                "逐页重命名归档，并在输出根目录额外生成一份索引工作簿，方便核对。",
            HomeworkExportMode.CopyToFolders =>
                "按班级与学生分文件夹，保留每页的原始文件名。",
            _ =>
                "按班级与姓名逐页重命名，直接放进对应班级文件夹。",
        };

        var lines = new List<string>();
        lines.AddRange(_viewModel.Conflicts.Select(c => "冲突 · " + c));
        lines.AddRange(_viewModel.Warnings.Select(w => "提示 · " + w));
        DiagnosticLines = lines.Take(6).ToArray();
        HasDiagnostics = DiagnosticLines.Count > 0;

        HasConflicts = _viewModel.Conflicts.Count > 0;
        HasWarnings = _viewModel.Warnings.Count > 0;
        TreeSketch = HasConflicts ? string.Empty : BuildTreeSketch(_viewModel.Mode);
        HasTreeSketch = TreeSketch.Length > 0;
    }

    /// <summary>纯文本目录示意。使用合成姓名，仅说明结构，不读取任何真实名单。</summary>
    private static string BuildTreeSketch(HomeworkExportMode mode) => mode switch
    {
        HomeworkExportMode.MergePdf =>
            "八年级1班/\n" +
            "  八年级1班_01_苏晚晴.pdf\n" +
            "  八年级1班_02_陈屿.pdf\n" +
            "八年级2班/\n" +
            "  八年级2班_01_林知夏.pdf",
        HomeworkExportMode.CopyToFolders =>
            "八年级1班/\n" +
            "  八年级1班_01_苏晚晴/\n" +
            "    第1页.png\n" +
            "    第2页.png\n" +
            "八年级2班/\n" +
            "  八年级2班_01_林知夏/",
        HomeworkExportMode.ArchiveWithIndex =>
            "八年级1班/\n" +
            "  八年级1班_01_苏晚晴_第1页.png\n" +
            "  八年级1班_01_苏晚晴_第2页.png\n" +
            "八年级2班/\n" +
            "  八年级2班_01_林知夏_第1页.png\n" +
            "作业索引_20261122-101500.xlsx",
        _ =>
            "八年级1班/\n" +
            "  八年级1班_01_苏晚晴_第1页.png\n" +
            "  八年级1班_01_苏晚晴_第2页.png\n" +
            "八年级2班/\n" +
            "  八年级2班_01_林知夏_第1页.png",
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
