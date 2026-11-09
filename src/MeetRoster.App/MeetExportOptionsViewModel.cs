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

/// <summary>PDF 版式选项。</summary>
public sealed record MeetExportLayoutOption(MeetPdfLayout Layout, string Label, string Hint);

/// <summary>Excel 样式选项。</summary>
public sealed record MeetExcelStyleOption(MeetExcelStyle Style, string Label, string Hint);

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
            "A4 打印，可选表格手册或赛程卡两种版式"));
        _selectedFormat = Formats[0];

        Styles.Add(new MeetExcelStyleOption(
            MeetExcelStyle.Workbook, "软件整理",
            "重排成「班级报名 / 单项名单 / 赛程 / 班级汇总」四页，单班整行加底色"));
        Styles.Add(new MeetExcelStyleOption(
            MeetExcelStyle.OriginalHighlight, "原表高亮",
            "直接复制导入的原工作簿，只在名单与分组表里给该班条目涂底色，版面原样保留"));
        _selectedStyle = Styles[0];

        Layouts.Add(new MeetExportLayoutOption(
            MeetPdfLayout.Handbook, "表格手册",
            "概览 → 名单 → 单项 → 日程，长表格跨页自动重复表头，适合归档与打印"));
        Layouts.Add(new MeetExportLayoutOption(
            MeetPdfLayout.Card, "赛程卡",
            "照软件内「仅显示该班选手」的观感：每时段一张卡，彩色分类徽标 + 选手标签"));
        _selectedLayout = Layouts[0];

        UpdateFileName();
    }

    public string Title { get; }
    public string Summary { get; }

    public ObservableCollection<MeetExportTargetOption> Targets { get; } = [];
    public ObservableCollection<MeetExportFormatOption> Formats { get; } = [];
    public ObservableCollection<MeetExportLayoutOption> Layouts { get; } = [];
    public ObservableCollection<MeetExcelStyleOption> Styles { get; } = [];

    [ObservableProperty] private MeetExportTargetOption _selectedTarget;
    [ObservableProperty] private MeetExportFormatOption _selectedFormat;
    [ObservableProperty] private MeetExportLayoutOption _selectedLayout;
    [ObservableProperty] private MeetExcelStyleOption _selectedStyle;

    [ObservableProperty] private bool _includeRoster = true;
    [ObservableProperty] private bool _includeEvents = true;
    [ObservableProperty] private bool _includeSchedule = true;
    [ObservableProperty] private bool _includeClassSummary = true;
    [ObservableProperty] private bool _highlightClass = true;
    [ObservableProperty] private string _error = string.Empty;

    // ---------- 预览 ----------

    /// <summary>预览位图；null 表示还没生成或生成失败。</summary>
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _previewImage;
    [ObservableProperty] private string _previewCaption = string.Empty;
    [ObservableProperty] private string _previewDetail = string.Empty;
    [ObservableProperty] private bool _isPreviewBusy;
    [ObservableProperty] private string _previewEmptyText = "选择设置后自动生成预览";

    public bool HasPreview => PreviewImage is not null;
    public bool ShowPreviewEmpty => PreviewImage is null && !IsPreviewBusy;

    partial void OnPreviewImageChanged(Avalonia.Media.Imaging.Bitmap? value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ShowPreviewEmpty));
    }

    partial void OnIsPreviewBusyChanged(bool value) => OnPropertyChanged(nameof(ShowPreviewEmpty));

    /// <summary>预览请求合并：连续改设置只画最后一次（渲染一张缩略图约几十毫秒）。</summary>
    private bool _previewQueued;

    /// <summary>由窗口在打开时调用一次，并在设置变化时调用。</summary>
    public void RequestPreview()
    {
        if (_previewQueued) return;
        _previewQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _previewQueued = false;
            RefreshPreview();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RefreshPreview()
    {
        if (_project.Classes.Count == 0) return;

        IsPreviewBusy = true;
        try
        {
            var options = BuildOptions();
            var kind = IsExcel ? MeetExportFormatKind.Excel : MeetExportFormatKind.Pdf;
            var preview = MeetExportPreviewRenderer.Render(_project, options, kind);

            using var stream = new MemoryStream(preview.Png);
            PreviewImage = new Avalonia.Media.Imaging.Bitmap(stream);
            PreviewCaption = preview.Caption;
            PreviewDetail = preview.Detail;
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            PreviewEmptyText = "预览生成失败：" + ex.Message;
            PreviewCaption = string.Empty;
            PreviewDetail = string.Empty;
        }
        finally
        {
            IsPreviewBusy = false;
        }
    }

    public bool HasError => Error.Length > 0;

    public bool IsExcel => SelectedFormat.Format == MeetExportFormat.Excel;
    public bool IsPdf => !IsExcel;

    /// <summary>只有 PDF 才谈得上版式选择。</summary>
    public bool ShowLayoutChoice => IsPdf;

    /// <summary>只有 Excel 才谈得上样式选择。</summary>
    public bool ShowStyleChoice => IsExcel;

    /// <summary>原表高亮只复制原工作簿：没有原文件时不能选。</summary>
    public bool CanUseOriginalStyle => !string.IsNullOrWhiteSpace(_project.SourcePath)
                                       && System.IO.File.Exists(_project.SourcePath);

    public string StyleHint => SelectedStyle.Style == MeetExcelStyle.OriginalHighlight
        ? CanUseOriginalStyle
            ? "底色 #FBE4D7 = 该班条目；其余内容、列宽、合并单元格与原表一致，原文件不会被改动。"
            : "当前工程没有导入时的原工作簿（或它已被移动），原表高亮不可用。请重新导入秩序册，或改用「软件整理」。"
        : "四页工作表结构与导出一致；预览是前几行的样式示意。";

    /// <summary>赛程卡版式只画赛程，名单类分页对它不生效，界面上要说明。</summary>
    public bool IsCardLayout => IsPdf && SelectedLayout.Layout == MeetPdfLayout.Card;

    /// <summary>卡片版式下「包含内容」不生效（卡片只画赛程），勾选框置灰并说明。</summary>
    public bool ContentChecksEnabled => !IsCardLayout;

    /// <summary>导出对象是单个班时才谈得上「高亮该班」。</summary>
    public bool IsSingleClass => !SelectedTarget.IsAllClasses;
    public bool CanHighlight => IsSingleClass;

    /// <summary>
    /// 所有「随格式 / 版式 / 对象变化」的派生属性都从这里一次性通知。
    /// 逐处手写曾漏掉 ShowStyleChoice：切到 PDF 时 Excel 样式不消失，
    /// 而且切回 Excel 也回不来——因为没人通知过它。
    /// </summary>
    private void RaiseDerivedProperties()
    {
        OnPropertyChanged(nameof(IsExcel));
        OnPropertyChanged(nameof(IsPdf));
        OnPropertyChanged(nameof(ShowLayoutChoice));
        OnPropertyChanged(nameof(ShowStyleChoice));
        OnPropertyChanged(nameof(CanUseOriginalStyle));
        OnPropertyChanged(nameof(StyleHint));
        OnPropertyChanged(nameof(IsCardLayout));
        OnPropertyChanged(nameof(ContentChecksEnabled));
        OnPropertyChanged(nameof(IsSingleClass));
        OnPropertyChanged(nameof(CanHighlight));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(ExportSummary));
    }

    public string TargetHint => SelectedTarget.IsAllClasses
        ? "导出整份秩序册：每个班的名单与赛程都会列出来，不加底色。"
        : $"只导出 {SelectedTarget.ClassName}：赛程只列该班选手，表格里该班条目加底色。";

    /// <summary>底部摘要：勾了哪几页、发给谁看。</summary>
    public string ExportSummary
    {
        get
        {
            var target = SelectedTarget.IsAllClasses ? "全部班级" : SelectedTarget.ClassName!;
            if (IsCardLayout) return $"{target} · 赛程卡（只含赛程）";

            var pages = new List<string>();
            if (IncludeRoster) pages.Add("报名名单");
            if (IncludeEvents) pages.Add("单项报名");
            if (IncludeSchedule) pages.Add("参赛日程");
            if (IncludeClassSummary) pages.Add("参赛概览");
            return pages.Count == 0
                ? $"{target} · 未选择内容"
                : $"{target} · {string.Join(" / ", pages)}";
        }
    }

    public string FileName { get; private set; } = "运动会秩序册.xlsx";

    public MeetExportOptions BuildOptions() => new()
    {
        ClassName = SelectedTarget.ClassName,
        ExcelStyle = SelectedStyle.Style,
        Layout = SelectedLayout.Layout,
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

        if (IsExcel && SelectedStyle.Style == MeetExcelStyle.OriginalHighlight && !CanUseOriginalStyle)
        {
            error = "「原表高亮」需要导入时的原工作簿，但它已不在原位置。"
                    + "请重新导入秩序册，或把 Excel 样式改回「软件整理」。";
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
        RaiseDerivedProperties();
        UpdateFileName();
        RequestPreview();
    }

    partial void OnSelectedFormatChanged(MeetExportFormatOption value)
    {
        RaiseDerivedProperties();
        UpdateFileName();
        RequestPreview();
    }

    partial void OnSelectedStyleChanged(MeetExcelStyleOption value)
    {
        RaiseDerivedProperties();
        RequestPreview();
    }

    partial void OnSelectedLayoutChanged(MeetExportLayoutOption value)
    {
        RaiseDerivedProperties();
        RequestPreview();
    }

    partial void OnIncludeRosterChanged(bool value) => OnContentChanged();
    partial void OnIncludeEventsChanged(bool value) => OnContentChanged();
    partial void OnIncludeScheduleChanged(bool value) => OnContentChanged();
    partial void OnIncludeClassSummaryChanged(bool value) => OnContentChanged();
    partial void OnHighlightClassChanged(bool value) => RequestPreview();

    private void OnContentChanged()
    {
        OnPropertyChanged(nameof(ExportSummary));
        RequestPreview();
    }

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
