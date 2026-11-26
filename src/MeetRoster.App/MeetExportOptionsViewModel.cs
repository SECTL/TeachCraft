using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetRoster.Core.Export;
using MeetRoster.Core.Models;
using TeachCraft.Shared;

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
public sealed partial class MeetExcelStyleOption : ObservableObject
{
    public MeetExcelStyleOption(MeetExcelStyle style, string label, string hint)
    {
        Style = style;
        Label = label;
        Hint = hint;
    }

    public MeetExcelStyle Style { get; }
    public string Label { get; }

    /// <summary>说明文字。不可用时改写成原因——下拉里那一行本身就是「提示」。</summary>
    [ObservableProperty] private string _hint;

    /// <summary>
    /// 当前设置下这个样式能不能选。原表高亮在「全部班级」下不可用：
    /// 它按班涂底色，导出全部班级等于把整本表涂满，反而看不出是哪班（审计 P2-17）。
    /// </summary>
    [ObservableProperty] private bool _isAvailable = true;

    /// <summary>下拉项的透明度：不可用时压到 0.45，一眼看得出点不动。</summary>
    public double ItemOpacity => IsAvailable ? 1 : 0.45;

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(ItemOpacity));
}

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
            MeetExcelStyle.Workbook, "软件整理", StyleBaseHint(MeetExcelStyle.Workbook)));
        Styles.Add(new MeetExcelStyleOption(
            MeetExcelStyle.OriginalHighlight, "原表高亮", StyleBaseHint(MeetExcelStyle.OriginalHighlight)));
        _selectedStyle = Styles[0];

        Layouts.Add(new MeetExportLayoutOption(
            MeetPdfLayout.Handbook, "表格手册",
            "概览 → 名单 → 单项 → 日程，长表格跨页自动重复表头，适合归档与打印"));
        Layouts.Add(new MeetExportLayoutOption(
            MeetPdfLayout.Card, "赛程卡",
            "照软件内「仅显示该班选手」的观感：每时段一张卡，彩色分类徽标 + 选手标签"));
        _selectedLayout = Layouts[0];

        // 构造时就要算一次：默认导出对象可能是「全部班级」，原表高亮必须一开始就置灰。
        RefreshStyleAvailability();

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

    /// <summary>
    /// 预览位图是整页 A4 缩略图（每张 3–6MB 非托管像素），改设置就会重建一张。
    /// 只留新图、直接丢掉旧引用的话，GC 感知不到这块原生内存，连点几次设置就能把进程推到几百 MB，
    /// 所以换值时立即释放旧图。绑定的 <c>Image.Source</c> 在通知到达前已切到新值，释放是安全的。
    /// </summary>
    partial void OnPreviewImageChanged(Avalonia.Media.Imaging.Bitmap? oldValue, Avalonia.Media.Imaging.Bitmap? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ShowPreviewEmpty));
    }

    partial void OnIsPreviewBusyChanged(bool value) => OnPropertyChanged(nameof(ShowPreviewEmpty));

    /// <summary>
    /// 预览请求合并：连续改设置只画最后一次。
    /// <para>
    /// 光合并派发还不够——真正的渲染原来同步跑在 UI 线程上（一次 60–135ms，
    /// 4 核机更久），连点几下设置就是连续卡顿。现在渲染挪到后台线程，
    /// 并加 200ms 防抖：只有停手后才真的画。
    /// </para>
    /// </summary>
    private bool _previewQueued;
    private CancellationTokenSource? _previewCts;
    private bool _isClosed;

    /// <summary>防抖窗口：连续改设置时只画最后一次。</summary>
    private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>渲染落定后如果又有改动进来，这个标志保证补发一次（P3-22）。</summary>
    private bool _previewDirty;

    /// <summary>由窗口在打开时调用一次，并在设置变化时调用。</summary>
    public void RequestPreview()
    {
        if (_isClosed) return;
        if (_previewQueued)
        {
            // 渲染在途（60–135ms）又改了设置：不能静默丢——等这次渲染落定后要再补一张。
            _previewDirty = true;
            return;
        }
        _previewQueued = true;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _ = RefreshPreviewDebouncedAsync(cts);
    }

    private async Task RefreshPreviewDebouncedAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            // 防抖：停手 200ms 才画。取消时抛 OperationCanceledException，直接走 catch。
            await Task.Delay(PreviewDebounce, token).ConfigureAwait(true);

            if (_project.Classes.Count == 0)
            {
                _previewQueued = false;
                return;
            }

            var options = BuildOptions();
            var kind = IsExcel ? MeetExportFormatKind.Excel : MeetExportFormatKind.Pdf;

            IsPreviewBusy = true;

            // 渲染在后台线程：SkiaSharp 画布 + PNG 编码是纯 CPU 工作，不该占 UI 线程。
            var preview = await Task.Run(
                () => MeetExportPreviewRenderer.Render(_project, options, kind), token)
                .ConfigureAwait(true);

            // 期间可能已经又改过设置：这份结果作废，别覆盖更新的那张。
            if (token.IsCancellationRequested || _isClosed) return;

            using var stream = new MemoryStream(preview.Png);
            PreviewImage = new Avalonia.Media.Imaging.Bitmap(stream);
            PreviewCaption = preview.Caption;
            PreviewDetail = preview.Detail;
        }
        catch (OperationCanceledException)
        {
            // 被更新的一次请求取代，正常路径。
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _isClosed) return;
            PreviewImage = null;
            PreviewEmptyText = ErrorText.Describe(ex, "生成预览");
            PreviewCaption = string.Empty;
            PreviewDetail = string.Empty;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsPreviewBusy = false;
                _previewQueued = false;

                // 渲染期间改过设置的话，这里补发一次（P3-22）：
                // 老的丢弃语义会让最后改的那个设置永远不上预览。
                if (_previewDirty && !_isClosed)
                {
                    _previewDirty = false;
                    RequestPreview();
                }
            }
        }
    }

    /// <summary>窗口关闭时调用：取消在途渲染，之后不再回写界面。</summary>
    public void CancelPendingPreview()
    {
        _isClosed = true;
        var cts = Interlocked.Exchange(ref _previewCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    public bool HasError => Error.Length > 0;

    public bool IsExcel => SelectedFormat.Format == MeetExportFormat.Excel;
    public bool IsPdf => !IsExcel;

    /// <summary>只有 PDF 才谈得上版式选择。</summary>
    public bool ShowLayoutChoice => IsPdf;

    /// <summary>只有 Excel 才谈得上样式选择。</summary>
    public bool ShowStyleChoice => IsExcel;

    /// <summary>
    /// 原表高亮只复制原工作簿、只给「一个班」涂底色：没有原文件不能选，
    /// 导出对象是全部班级时也不能选——那会把整本表涂满，反而看不出是哪班（审计 P2-17）。
    /// 导出器的 null 语义不改：这里只是不给用户组合出这个请求的机会。
    /// </summary>
    public bool CanUseOriginalStyle => HasOriginalWorkbook && IsSingleClass;

    /// <summary>工程有没有可用的原工作簿（与导出对象无关）。</summary>
    public bool HasOriginalWorkbook => !string.IsNullOrWhiteSpace(_project.SourcePath)
                                       && System.IO.File.Exists(_project.SourcePath);

    /// <summary>原表高亮当前不可用的原因；可用时为空串。下拉项与提示区共用这段文案。</summary>
    public string OriginalStyleBlockReason
    {
        get
        {
            if (HasOriginalWorkbook && IsSingleClass) return string.Empty;
            return IsSingleClass
                ? "当前工程没有导入时的原工作簿（或它已被移动），原表高亮不可用。"
                  + "请重新导入秩序册，或改用「软件整理」。"
                : "只给单个班用：全部班级导出会把整本表涂满，看不出是哪班。"
                  + "把导出对象改成某个班后即可选用。";
        }
    }

    public string StyleHint => SelectedStyle.Style != MeetExcelStyle.OriginalHighlight
        ? "四页工作表结构与导出一致；预览是前几行的样式示意。"
        : CanUseOriginalStyle
            ? "底色 #FBE4D7 = 该班条目；其余内容、列宽、合并单元格与原表一致，原文件不会被改动。"
            : OriginalStyleBlockReason;

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
        OnPropertyChanged(nameof(HasOriginalWorkbook));
        OnPropertyChanged(nameof(StyleHint));
        OnPropertyChanged(nameof(IsCardLayout));
        OnPropertyChanged(nameof(ContentChecksEnabled));
        OnPropertyChanged(nameof(IsSingleClass));
        OnPropertyChanged(nameof(CanHighlight));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(ExportSummary));

        // 下拉项本身也要跟着禁用：只在 TryBuild 里拦会让老师选得进去、
        // 点「导出」才被驳回，白填一遍设置（审计 P2-17）。
        RefreshStyleAvailability();
    }

    /// <summary>按「有没有原工作簿 + 导出对象是不是单个班」刷新两个样式项的可选状态。</summary>
    private void RefreshStyleAvailability()
    {
        foreach (var style in Styles)
        {
            var available = style.Style != MeetExcelStyle.OriginalHighlight || CanUseOriginalStyle;
            style.IsAvailable = available;
            // 下拉里那一行自己就是提示：不可用时把说明换成原因，别让老师
            // 看着「直接复制原工作簿」的卖点去猜为什么点不动（审计 P2-17）。
            style.Hint = style.Style == MeetExcelStyle.OriginalHighlight && !available
                ? OriginalStyleBlockReason
                : StyleBaseHint(style.Style);
        }

        // 当前选着原表高亮、而它刚变成不可用（导出对象改成全部班级 / 原文件被移走）：
        // 退回「软件整理」，否则下拉里显示一个选不了的样式，导出时还会被 TryBuild 驳回。
        // 退出条件由 SelectedStyle 自身保证，不会来回弹。
        if (!CanUseOriginalStyle && SelectedStyle.Style == MeetExcelStyle.OriginalHighlight)
            SelectedStyle = Styles[0];
    }

    private static string StyleBaseHint(MeetExcelStyle style) => style == MeetExcelStyle.OriginalHighlight
        ? "直接复制导入的原工作簿，只在名单与分组表里给该班条目涂底色，版面原样保留"
        : "重排成「班级报名 / 单项名单 / 赛程 / 班级汇总」四页，单班整行加底色";

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

    private string _fileName = "运动会秩序册.xlsx";

    /// <summary>
    /// 导出文件名。老师可以在弹窗里直接改——改过之后就不再被自动名顶掉
    /// （旧实现是 get-only + TwoWay 绑定，输入静默失效，切一下导出对象
    /// 打的字就没了；审计 P2-19）。
    /// </summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName == value) return;
            _fileName = value;
            _fileNameEdited = true;
            OnPropertyChanged();
        }
    }

    private bool _fileNameEdited;

    /// <summary>「自动」按钮：放弃手改的名字，重新按导出对象生成。</summary>
    public void ResetFileName()
    {
        _fileNameEdited = false;
        UpdateFileName();
    }

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

        // 卡片版式只画赛程，四页开关对它不生效（界面上也已整体置灰）：
        // 再要求「至少勾选一页」就是一个谁也满足不了的死锁——勾选框点不动，
        // 导出按钮永远驳回（审计 P2-18）。卡片版式直接跳过这条校验。
        if (!IsCardLayout
            && !IncludeRoster && !IncludeEvents && !IncludeSchedule && !IncludeClassSummary)
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
            // 界面已把不可用的样式项置灰并自动退回，这里只是兜底；
            // 两种不可用原因分开说，别让老师对着「原文件不在了」去核对导出对象。
            error = IsSingleClass
                ? "「原表高亮」需要导入时的原工作簿，但它已不在原位置。"
                  + "请重新导入秩序册，或把 Excel 样式改回「软件整理」。"
                : "「原表高亮」只给单个班用，导出对象是全部班级时不可选。"
                  + "请把导出对象改成某个班，或把 Excel 样式改回「软件整理」。";
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
        // 老师手改过就不再覆盖；其余情况按导出对象与格式自动命名。
        if (_fileNameEdited) return;

        var extension = IsExcel ? ".xlsx" : ".pdf";
        _fileName = SelectedTarget.IsAllClasses
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
