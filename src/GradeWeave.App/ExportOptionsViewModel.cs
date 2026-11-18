using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GradeWeave.Core.Export;
using GradeWeave.Core.Models;

namespace GradeWeave.App;

public enum WeaveExportFormat
{
    Excel,
    Png,
    Pdf,
}

public sealed record ExportFormatOption(WeaveExportFormat Format, string Label, string Hint);

/// <summary>Excel 样式项；参考校样式的显示名称可在本机修改。</summary>
public partial class ExportStyleOption : ObservableObject
{
    public ExportStyleOption(WeaveExportStyle style, string label, string hint)
    {
        Style = style;
        _label = label;
        _hint = hint;
    }

    public WeaveExportStyle Style { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string _hint;
}

/// <summary>导出弹窗返回的请求：格式、工作簿内容 / 样式，以及 PNG / PDF 的目标。</summary>
public sealed record WeaveExportRequest(
    WeaveExportFormat Format,
    ExportOptions Options,
    string? ClassId,
    string? TeacherId,
    string TargetLabel);

/// <summary>
/// 04 步统一导出设置：格式、Excel 样式、分页内容；PNG / PDF 导出当前选中的班级或教师。
/// 与竞名导出弹窗同构——左侧设置、右侧实时预览，改设置即刷新且不落盘。
/// </summary>
public partial class ExportOptionsViewModel : ObservableObject
{
    private readonly WeaveProject _project;
    private readonly SchoolClass? _selectedClass;
    private readonly Teacher? _selectedTeacher;
    private readonly bool _teacherView;

    public ExportOptionsViewModel(
        WeaveProject project,
        SchoolClass? selectedClass,
        Teacher? selectedTeacher,
        bool teacherView)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        _selectedClass = selectedClass;
        _selectedTeacher = selectedTeacher;
        _teacherView = teacherView;

        ProjectTitle = string.IsNullOrWhiteSpace(project.SchoolName)
            ? project.GradeName
            : $"{project.SchoolName} · {project.GradeName}";
        Summary = $"{project.Classes.Count} 个班 · {project.Teachers.Count} 位教师 · {project.Subjects.Count} 个学科";

        Formats.Add(new ExportFormatOption(
            WeaveExportFormat.Excel, "Excel 工作簿", "多分页，可勾选总表、分工表、职位表、班级与教师课表"));
        Formats.Add(new ExportFormatOption(
            WeaveExportFormat.Png, "PNG 图片", "导出当前班级或教师的课表图片"));
        Formats.Add(new ExportFormatOption(
            WeaveExportFormat.Pdf, "PDF 打印", "导出当前班级或教师的 A4 打印课表"));

        Styles.Add(new ExportStyleOption(
            WeaveExportStyle.Software, "软件自带样式", "纸墨色板、雅黑字体、浅色细边"));
        Styles.Add(new ExportStyleOption(
            WeaveExportStyle.Reference, ReferenceStyleName, "宋体 / 楷体、黑色细框、横向课程总表"));
        Styles.Add(new ExportStyleOption(
            WeaveExportStyle.Seewo, "希沃全校课表", "填进希沃官方模板，可直接上传希沃平台"));

        _selectedFormat = Formats[0];
        _selectedStyle = Styles[0];

        // 导出对象下拉：PNG / PDF 只导一个班或一位教师，让老师在弹窗里直接选，
        // 而不是「关弹窗 → 回主窗口选 → 再开弹窗」（审计 P2-18）。
        // 主窗口当前选择只作初始值。
        ClassTargets.Add(new ExportTargetOption(null, "未选择班级"));
        foreach (var schoolClass in project.Classes.OrderBy(c => c.Name, StringComparer.Ordinal))
            ClassTargets.Add(new ExportTargetOption(schoolClass.Id, schoolClass.Name));
        TeacherTargets.Add(new ExportTargetOption(null, "未选择教师"));
        foreach (var teacher in project.Teachers.OrderBy(t => t.Name, StringComparer.Ordinal))
            TeacherTargets.Add(new ExportTargetOption(teacher.Id, teacher.Name));

        _selectedClassTarget = ClassTargets.FirstOrDefault(o => o.Id == selectedClass?.Id) ?? ClassTargets[0];
        _selectedTeacherTarget =
            TeacherTargets.FirstOrDefault(o => o.Id == selectedTeacher?.Id) ?? TeacherTargets[0];
        // 初始值写入完成；此后只有用户（或测试）改下拉才触发互斥重置。
        _initializing = false;
    }

    public ObservableCollection<ExportTargetOption> ClassTargets { get; } = new();
    public ObservableCollection<ExportTargetOption> TeacherTargets { get; } = new();

    [ObservableProperty] private ExportTargetOption _selectedClassTarget;
    [ObservableProperty] private ExportTargetOption _selectedTeacherTarget;

    /// <summary>构造写入初始值期间为真：互斥重置只认用户改动，初始化路径一律豁免。</summary>
    private bool _initializing = true;

    /// <summary>PNG / PDF 下才需要选对象；Excel 是整本工作簿。</summary>
    public bool ShowTargetPickers => !IsExcel;

    partial void OnSelectedClassTargetChanged(ExportTargetOption value)
    {
        // 班级 / 教师二选一互斥（审计 P2-9）：用户在一侧选定对象时把另一侧重置为「未选择」，
        // 不再让初始班级把教师选择静默压住。重置写入的是「未选择」项（Id 为 null），
        // 不满足「选定了对象」这个条件，因此不会级联回来把用户刚选的一侧也清掉。
        if (!_initializing && value.Id is not null && SelectedTeacherTarget.Id is not null)
            SelectedTeacherTarget = TeacherTargets.First(o => o.Id is null);
        RaiseDerivedProperties();
        RequestPreview();
    }

    partial void OnSelectedTeacherTargetChanged(ExportTargetOption value)
    {
        // 反向同理：用户选定教师时清掉班级侧。
        if (!_initializing && value.Id is not null && SelectedClassTarget.Id is not null)
            SelectedClassTarget = ClassTargets.First(o => o.Id is null);
        RaiseDerivedProperties();
        RequestPreview();
    }

    public string ProjectTitle { get; }
    public string Summary { get; }

    public ObservableCollection<ExportFormatOption> Formats { get; } = new();
    public ObservableCollection<ExportStyleOption> Styles { get; } = new();

    [ObservableProperty] private ExportFormatOption _selectedFormat;
    [ObservableProperty] private ExportStyleOption _selectedStyle;
    [ObservableProperty] private string _referenceStyleName = ExportStylePreferences.LoadName();
    [ObservableProperty] private bool _includeMaster = true;
    [ObservableProperty] private bool _includeAssignment = true;
    [ObservableProperty] private bool _includeTeacherPosition = true;
    [ObservableProperty] private bool _includeClassSheets = true;
    [ObservableProperty] private bool _includeTeacherSheets = true;
    [ObservableProperty] private bool _includeConflict = true;
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
    /// 预览位图是整页缩略图（每张几 MB 非托管像素），改设置就会重建一张。
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

    private bool _previewQueued;
    private CancellationTokenSource? _previewCts;
    private bool _isClosed;

    /// <summary>防抖窗口：连续改设置时只画最后一次。</summary>
    private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>由窗口在打开时调用一次，并在设置变化时调用。</summary>
    public void RequestPreview()
    {
        if (_isClosed) return;
        if (_previewQueued) return;
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

            IsPreviewBusy = true;

            // 渲染在后台线程：SkiaSharp 画布 + PNG 编码是纯 CPU 工作，不该占 UI 线程。
            var style = SelectedStyle.Style;
            var isWorkbook = IsExcel;
            var target = ResolveTarget();
            var preview = await Task.Run(
                () => isWorkbook
                    ? WeaveExportPreviewRenderer.Render(_project, style)
                    : WeaveExportPreviewRenderer.RenderSingle(_project, target.ClassId, target.TeacherId),
                token)
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
            PreviewEmptyText = "预览生成失败：" + ex.Message;
            PreviewCaption = string.Empty;
            PreviewDetail = string.Empty;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsPreviewBusy = false;
                _previewQueued = false;
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

    public bool IsExcel => SelectedFormat.Format == WeaveExportFormat.Excel;
    public bool IsImageOrPdf => !IsExcel;
    public bool IsReferenceStyle => SelectedStyle.Style == WeaveExportStyle.Reference;
    public bool IsSeewoStyle => SelectedStyle.Style == WeaveExportStyle.Seewo;

    /// <summary>希沃模板只有一张表，分页勾选对它不生效，界面上要说明。</summary>
    public bool ShowSheetChoices => IsExcel && !IsSeewoStyle;

    /// <summary>参考校样式才谈得上本机样式名称。</summary>
    public bool ShowReferenceName => IsReferenceStyle && IsExcel;

    public string StyleHint => SelectedStyle.Style switch
    {
        WeaveExportStyle.Seewo =>
            "把课表填进希沃官方模板：标题、须知、表头、批注与合并单元格原样保留，只替换示例行，可直接上传希沃平台。",
        WeaveExportStyle.Reference =>
            "按参考工作簿生成表一分工表、表二横向课程总表和表三教师职位表。",
        _ =>
            "纸墨版式：总表、分工表、职位表、班级与教师课表，均带纸墨配色与细边。",
    };

    public string TargetLabel => ResolveTarget().Label;

    public string TargetText
    {
        get
        {
            var target = ResolveTarget();
            if (target.Label == "未选择") return "未选择班级或教师";
            return target.TeacherId is not null ? $"教师 · {target.Label}" : $"班级 · {target.Label}";
        }
    }

    public string TargetHint => "在下方选择导出对象，班级与教师二选一。";

    /// <summary>底部摘要：当前要导出什么。</summary>
    public string ExportSummary
    {
        get
        {
            if (IsImageOrPdf)
            {
                var target = ResolveTarget();
                if (target.Label == "未选择") return "未选择班级或教师";
                return SelectedFormat.Format == WeaveExportFormat.Png
                    ? $"{target.Label} · PNG 图片"
                    : $"{target.Label} · PDF 打印";
            }

            if (IsSeewoStyle) return "全年级 · 希沃全校课表（单页）";

            var pages = new List<string>();
            if (IncludeMaster) pages.Add("总表");
            if (IncludeAssignment) pages.Add("分工表");
            if (IncludeTeacherPosition) pages.Add("职位表");
            if (IncludeClassSheets) pages.Add("班级课表");
            if (IncludeTeacherSheets) pages.Add("教师课表");
            if (IncludeConflict) pages.Add("冲突页");
            return pages.Count == 0 ? "未选择分页" : "全年级 · " + string.Join(" / ", pages);
        }
    }

    private (string? ClassId, string? TeacherId, string Label) ResolveTarget()
    {
        // 弹窗内选择优先；没选时回落到主窗口当前选择。
        if (SelectedClassTarget.Id is { } classId)
            return (classId, null, SelectedClassTarget.Label);
        if (SelectedTeacherTarget.Id is { } teacherId)
            return (null, teacherId, SelectedTeacherTarget.Label);

        if (_teacherView && _selectedTeacher is not null)
            return (null, _selectedTeacher.Id, _selectedTeacher.Name);
        if (_selectedClass is not null)
            return (_selectedClass.Id, null, _selectedClass.Name);
        if (_selectedTeacher is not null)
            return (null, _selectedTeacher.Id, _selectedTeacher.Name);
        return (null, null, "未选择");
    }

    partial void OnSelectedFormatChanged(ExportFormatOption value)
    {
        RaiseDerivedProperties();
        Error = string.Empty;
        RequestPreview();
    }

    partial void OnSelectedStyleChanged(ExportStyleOption value)
    {
        RaiseDerivedProperties();
        RequestPreview();
    }

    partial void OnReferenceStyleNameChanged(string value)
    {
        _styleNameEdited = true;
        if (Styles.Count < 2) return;
        var name = string.IsNullOrWhiteSpace(value) ? ExportStylePreferences.DefaultName : value.Trim();
        Styles[1].Label = name;
    }

    /// <summary>只在用户真的改过名称时才落本机配置，避免每次导出都覆盖已有名称（也避免测试污染）。</summary>
    private bool _styleNameEdited;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnIncludeMasterChanged(bool value) => OnContentChanged();
    partial void OnIncludeAssignmentChanged(bool value) => OnContentChanged();
    partial void OnIncludeTeacherPositionChanged(bool value) => OnContentChanged();
    partial void OnIncludeClassSheetsChanged(bool value) => OnContentChanged();
    partial void OnIncludeTeacherSheetsChanged(bool value) => OnContentChanged();
    partial void OnIncludeConflictChanged(bool value) => OnContentChanged();

    private void OnContentChanged() => OnPropertyChanged(nameof(ExportSummary));

    /// <summary>
    /// 所有「随格式 / 样式变化」的派生属性都从这里一次性通知。
    /// 逐处手写曾漏掉 ShowStyleChoice：切到 PDF 时 Excel 样式不消失，
    /// 而且切回 Excel 也回不来——因为没人通知过它。
    /// </summary>
    private void RaiseDerivedProperties()
    {
        OnPropertyChanged(nameof(IsExcel));
        OnPropertyChanged(nameof(IsImageOrPdf));
        OnPropertyChanged(nameof(IsReferenceStyle));
        OnPropertyChanged(nameof(IsSeewoStyle));
        OnPropertyChanged(nameof(ShowSheetChoices));
        OnPropertyChanged(nameof(ShowReferenceName));
        OnPropertyChanged(nameof(StyleHint));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(ExportSummary));
    }

    public bool TryBuild(out WeaveExportRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        Error = string.Empty;

        var format = SelectedFormat.Format;
        if (format == WeaveExportFormat.Excel)
        {
            // 希沃预设只有一张表，不受分页勾选约束；其余 Excel 样式至少要勾一页。
            if (!IsSeewoStyle &&
                !IncludeMaster && !IncludeAssignment && !IncludeTeacherPosition &&
                !IncludeClassSheets && !IncludeTeacherSheets && !IncludeConflict)
            {
                error = "请至少勾选一个分页。";
                Error = error;
                return false;
            }

            var options = new ExportOptions
            {
                IncludeMasterSheet = IncludeMaster,
                IncludeAssignmentSheet = IncludeAssignment,
                IncludeTeacherPositionSheet = IncludeTeacherPosition,
                IncludeClassSheets = IncludeClassSheets,
                IncludeTeacherSheets = IncludeTeacherSheets,
                IncludeConflictSheet = IncludeConflict,
                Style = SelectedStyle.Style,
            };
            if (options.Style == WeaveExportStyle.Reference && _styleNameEdited)
                ExportStylePreferences.SaveName(ReferenceStyleName);
            request = new WeaveExportRequest(format, options, null, null, "全年级");
            return true;
        }

        var target = ResolveTarget();
        if (target.ClassId is null && target.TeacherId is null)
        {
            error = "请选择要导出的班级或教师。";
            Error = error;
            return false;
        }

        request = new WeaveExportRequest(
            format, new ExportOptions { Style = WeaveExportStyle.Software },
            target.ClassId, target.TeacherId, target.Label);
        return true;
    }
}

/// <summary>导出对象的一项：班级或教师；Id 为 null 表示未选择。</summary>
public sealed class ExportTargetOption
{
    public ExportTargetOption(string? id, string label)
    {
        Id = id;
        Label = label;
    }

    public string? Id { get; }
    public string Label { get; }
    public override string ToString() => Label;
}
