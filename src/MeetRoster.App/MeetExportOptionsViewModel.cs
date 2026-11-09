using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetRoster.Core.Export;
using MeetRoster.Core.Models;

namespace MeetRoster.App;

public enum MeetExportFormat
{
    Excel,
    Pdf,
}

public sealed record MeetExportFormatOption(MeetExportFormat Format, string Label, string Hint);

/// <summary>导出设置弹窗返回的请求。</summary>
public sealed record MeetExportRequest(
    MeetExportFormat Format,
    MeetExportOptions Options,
    string FileName,
    string TargetLabel);

/// <summary>
/// 竞名统一导出设置：导出对象（全部 / 某个班）、格式（Excel / PDF）与分页内容。
/// 与织序的导出弹窗同构——一处选完，主窗口只负责挑保存位置。
/// </summary>
public sealed partial class MeetExportOptionsViewModel : ObservableObject
{
    private readonly MeetProject _project;

    public MeetExportOptionsViewModel(MeetProject project, string? currentClass)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;

        Title = string.IsNullOrWhiteSpace(project.Title) ? "运动会" : project.Title;
        Summary = $"{project.Classes.Count} 个班 · {project.Classes.Sum(c => c.Athletes.Count(a => !string.IsNullOrWhiteSpace(a.Name)))} 名运动员 · "
                  + $"{project.Days.Count} 个比赛日";

        Targets.Add(new MeetExportTargetOption(null, "全部班级", "整份秩序册，含每个班的名单与赛程"));
        foreach (var meetClass in project.Classes)
        {
            var athletes = meetClass.Athletes.Count(a => !string.IsNullOrWhiteSpace(a.Name));
            Targets.Add(new MeetExportTargetOption(
                meetClass.Name, meetClass.Name,
                athletes > 0 ? $"{athletes} 名运动员 · {meetClass.TeamEvents.Count} 个团体项目" : "没有名单"));
        }

        _selectedTarget = Targets.FirstOrDefault(t => t.ClassName == currentClass) ?? Targets[0];

        Formats.Add(new MeetExportFormatOption(
            MeetExportFormat.Excel, "Excel 工作簿",
            "分「班级报名 / 单项名单 / 赛程 / 班级汇总」四页，该班整行加底色"));
        Formats.Add(new MeetExportFormatOption(
            MeetExportFormat.Pdf, "PDF 参赛手册",
            "纸墨版式、A4 打印，按班分节排版，适合直接发群或打印"));
        _selectedFormat = Formats[0];

        UpdateFileName();
    }

    public string Title { get; }
    public string Summary { get; }

    public ObservableCollection<MeetExportTargetOption> Targets { get; } = [];
    public ObservableCollection<MeetExportFormatOption> Formats { get; } = [];

    [ObservableProperty] private MeetExportTargetOption _selectedTarget;
    [ObservableProperty] private MeetExportFormatOption _selectedFormat;

    [ObservableProperty] private bool _includeRoster = true;
    [ObservableProperty] private bool _includeEvents = true;
    [ObservableProperty] private bool _includeSchedule = true;
    [ObservableProperty] private bool _includeClassSummary = true;
    [ObservableProperty] private bool _highlightClass = true;
    [ObservableProperty] private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    public bool IsExcel => SelectedFormat.Format == MeetExportFormat.Excel;
    public bool IsPdf => !IsExcel;

    /// <summary>导出对象是单个班时才谈得上「高亮该班」。</summary>
    public bool IsSingleClass => !SelectedTarget.IsAllClasses;
    public bool CanHighlight => IsSingleClass;

    public string TargetHint => SelectedTarget.IsAllClasses
        ? "导出整份秩序册：每个班的名单与赛程都会列出来，不加底色。"
        : $"只导出 {SelectedTarget.ClassName}：赛程只列该班选手，表格里该班条目加底色。";

    /// <summary>底部摘要：勾了哪几页、发给谁看。</summary>
    public string ExportSummary
    {
        get
        {
            var pages = new List<string>();
            if (IncludeRoster) pages.Add("报名名单");
            if (IncludeEvents) pages.Add("单项报名");
            if (IncludeSchedule) pages.Add("参赛日程");
            if (IncludeClassSummary) pages.Add("参赛概览");
            var target = SelectedTarget.IsAllClasses ? "全部班级" : SelectedTarget.ClassName!;
            return pages.Count == 0
                ? $"{target} · 未选择内容"
                : $"{target} · {string.Join(" / ", pages)}";
        }
    }

    public string FileName { get; private set; } = "运动会秩序册.xlsx";

    public MeetExportOptions BuildOptions() => new()
    {
        ClassName = SelectedTarget.ClassName,
        OnlyClassEntries = IsSingleClass,
        HighlightClass = HighlightClass && IsSingleClass,
        IncludeRoster = IncludeRoster,
        IncludeEvents = IncludeEvents,
        IncludeSchedule = IncludeSchedule,
        IncludeClassSummary = IncludeClassSummary,
    };

    /// <summary>校验并给出导出请求；不通过时返回 false 并写 Error。</summary>
    public bool TryBuild(out MeetExportRequest request, out string error)
    {
        request = default!;
        error = string.Empty;

        if (!IncludeRoster && !IncludeEvents && !IncludeSchedule && !IncludeClassSummary)
        {
            error = "至少勾选一页内容。";
            return false;
        }

        if (_project.Classes.Count == 0)
        {
            error = "还没有导入任何班级，无法导出。";
            return false;
        }

        var extension = IsExcel ? ".xlsx" : ".pdf";
        var stem = FileName.Trim();
        if (stem.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            stem = System.IO.Path.GetFileNameWithoutExtension(stem);

        if (stem.Length == 0)
        {
            error = "文件名不能为空。";
            return false;
        }

        var label = SelectedTarget.IsAllClasses
            ? $"全部班级 · {_project.Classes.Count} 个班"
            : $"班级 · {SelectedTarget.ClassName}";

        request = new MeetExportRequest(
            SelectedFormat.Format,
            BuildOptions(),
            MeetExportNaming.Safe(stem) + extension,
            label);
        return true;
    }

    partial void OnSelectedTargetChanged(MeetExportTargetOption value)
    {
        OnPropertyChanged(nameof(IsSingleClass));
        OnPropertyChanged(nameof(CanHighlight));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(ExportSummary));
        UpdateFileName();
    }

    partial void OnSelectedFormatChanged(MeetExportFormatOption value)
    {
        OnPropertyChanged(nameof(IsExcel));
        OnPropertyChanged(nameof(IsPdf));
        UpdateFileName();
    }

    partial void OnIncludeRosterChanged(bool value) => OnPropertyChanged(nameof(ExportSummary));
    partial void OnIncludeEventsChanged(bool value) => OnPropertyChanged(nameof(ExportSummary));
    partial void OnIncludeScheduleChanged(bool value) => OnPropertyChanged(nameof(ExportSummary));
    partial void OnIncludeClassSummaryChanged(bool value) => OnPropertyChanged(nameof(ExportSummary));

    private void UpdateFileName()
    {
        var extension = IsExcel ? ".xlsx" : ".pdf";
        FileName = SelectedTarget.IsAllClasses
            ? $"{MeetExportNaming.Safe(Title)}·全部班级{extension}"
            : $"{MeetExportNaming.Safe(SelectedTarget.ClassName!)}参赛手册{extension}";
        OnPropertyChanged(nameof(FileName));
    }
}

/// <summary>导出对象：全部班级（ClassName 为 null）或某个班。</summary>
public sealed class MeetExportTargetOption
{
    public MeetExportTargetOption(string? className, string label, string hint)
    {
        ClassName = className;
        Label = label;
        Hint = hint;
    }

    public string? ClassName { get; }
    public string Label { get; }
    public string Hint { get; }
    public bool IsAllClasses => ClassName is null;
}
