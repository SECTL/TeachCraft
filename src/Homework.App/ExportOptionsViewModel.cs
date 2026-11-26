using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core;
using Homework.Core.Export;
using Homework.Core.Models;
using TeachCraft.Shared;

namespace Homework.App;

/// <summary>导出弹窗左侧设置项的一行。</summary>
public sealed record ExportModeOption(HomeworkExportMode Mode, string Title, string Description);

/// <summary>
/// 导出设置弹窗的状态。每次改模式 / 目录 / 覆盖开关都会用最近一次传入的批次与探针
/// 重跑纯规划器，右侧摘要与冲突即时刷新，但绝不落盘。
/// </summary>
public sealed partial class ExportOptionsViewModel : ObservableObject
{
    private HomeworkBatch? _batch;
    private IFileProbe? _probe;
    private bool _hasPreview;
    private HomeworkExportMode _mode = HomeworkExportMode.RenameArchive;
    private string _outputDirectory = string.Empty;
    private bool _overwriteExisting;
    private string _previewText = "请先选择输出目录。";
    private IReadOnlyList<string> _conflicts = [];
    private IReadOnlyList<string> _warnings = [];
    private int _pageCount;
    private int _studentCount;

    public ExportOptionsViewModel()
    {
        Modes =
        [
            new ExportModeOption(HomeworkExportMode.RenameArchive, "逐页重命名", "按班级与姓名逐页重命名归档"),
            new ExportModeOption(HomeworkExportMode.MergePdf, "合并为 PDF", "每位学生已匹配的页合并成一份 PDF"),
            new ExportModeOption(HomeworkExportMode.ArchiveWithIndex, "归档并生成索引", "逐页重命名并额外生成索引工作簿"),
            new ExportModeOption(HomeworkExportMode.CopyToFolders, "复制到文件夹", "按班级与学生分文件夹复制原文件"),
        ];
    }

    public IReadOnlyList<ExportModeOption> Modes { get; }

    public HomeworkExportMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value)) RefreshPreview();
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetProperty(ref _outputDirectory, value)) RefreshPreview();
        }
    }

    public bool OverwriteExisting
    {
        get => _overwriteExisting;
        set
        {
            if (SetProperty(ref _overwriteExisting, value)) RefreshPreview();
        }
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public IReadOnlyList<string> Conflicts
    {
        get => _conflicts;
        private set => SetProperty(ref _conflicts, value);
    }

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set => SetProperty(ref _warnings, value);
    }

    public int PageCount
    {
        get => _pageCount;
        private set => SetProperty(ref _pageCount, value);
    }

    public int StudentCount
    {
        get => _studentCount;
        private set => SetProperty(ref _studentCount, value);
    }

    public bool CanExport => _hasPreview
                             && Conflicts.Count == 0
                             && !string.IsNullOrWhiteSpace(OutputDirectory);

    public string BlockedReason
    {
        get
        {
            if (Conflicts.Count > 0) return $"存在 {Conflicts.Count} 处冲突，无法导出。";
            if (string.IsNullOrWhiteSpace(OutputDirectory)) return "请先选择输出目录。";
            if (!_hasPreview) return "请先生成导出预览。";
            return string.Empty;
        }
    }

    public void UpdatePreview(HomeworkBatch batch, IFileProbe probe)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(probe);

        _batch = batch;
        _probe = probe;
        RefreshPreview();
    }

    public HomeworkExportOptions BuildOptions() =>
        new(OutputDirectory, Mode, OverwriteExisting);

    private void RefreshPreview()
    {
        if (_batch is null || _probe is null) return;

        HomeworkExportPlan plan;
        try
        {
            plan = HomeworkExportPlanner.Build(_batch, BuildOptions(), _probe);
        }
        catch (HomeworkException ex)
        {
            _hasPreview = false;
            var message = ErrorText.Describe(ex, "生成导出预览");
            Conflicts = [message];
            Warnings = [];
            PageCount = 0;
            StudentCount = 0;
            PreviewText = message;
            RaiseExportState();
            return;
        }

        _hasPreview = true;
        Conflicts = plan.Conflicts;
        Warnings = plan.Warnings;
        PageCount = plan.PageCount;
        StudentCount = plan.StudentCount;

        if (plan.Conflicts.Count > 0)
        {
            var first = plan.Conflicts[0];
            PreviewText = plan.Conflicts.Count == 1
                ? $"发现 1 处冲突：{first}"
                : $"发现 {plan.Conflicts.Count} 处冲突：{first}…";
        }
        else
        {
            var studentIds = plan.Entries
                .Where(e => e.StudentId.HasValue)
                .Select(e => e.StudentId!.Value)
                .ToHashSet();
            var classCount = _batch.Students
                .Where(s => studentIds.Contains(s.Id))
                .Select(s => s.ClassId)
                .Distinct()
                .Count();

            var text = $"将写入 {plan.Entries.Count} 个文件 · {classCount} 个班级 · {plan.StudentCount} 位学生";
            if (plan.StudentsWithoutPages.Count > 0)
                text += $"（{plan.StudentsWithoutPages.Count} 位学生无页，将跳过）";
            PreviewText = text;
        }

        RaiseExportState();
    }

    private void RaiseExportState()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(BlockedReason));
    }
}
