using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Homework.Core;
using Homework.Core.Export;
using Homework.Core.Matching;
using Homework.Core.Models;
using Homework.Core.Pages;
using Homework.Core.Recognition;
using Homework.Core.Roster;
using TeachCraft.Shared;

namespace Homework.App;

/// <summary>
/// 单个批次的编辑工作区：左侧页、右侧名单、匹配 / 撤销 / 筛选 / 导入 / 导出预览的单一入口。
/// <para>
/// 所有会改动批次的动作都走 <see cref="RecordChange"/>：先拍 before、执行、再拍 after，
/// 内容相同不入栈。名单替换是唯一例外——Core 的 LoadRoster 会换掉全部学生 Id，
/// 旧快照里的学生 Id 已不存在、Restore 必然拒绝，因此导入名单后清空撤销栈而不是记一条假撤销。
/// </para>
/// </summary>
public sealed class BatchWorkspaceViewModel : ObservableObject
{
    private const double FitViewportWidth = 1000;
    private const double FitViewportHeight = 700;

    private readonly HomeworkUndoStack _undo = new();
    private readonly IFillInEngine _fillInEngine;
    private List<PageItemViewModel> _allPages = [];
    private List<StudentRowViewModel> _allStudents = [];
    private PageThumbnailCache? _cache;
    private PageItemViewModel? _selectedPage;
    private StudentRowViewModel? _selectedStudent;
    private ClassFilterItemViewModel? _selectedClassFilter;
    private bool _showAllPages;
    private StudentFilter _studentFilter = StudentFilter.Unmatched;
    private string _searchText = string.Empty;

    private enum StudentFilter
    {
        Unmatched,
        Matched,
        All,
    }
    private string _previewImagePath = string.Empty;
    private CancellationTokenSource? _previewCts;
    private int _previewEdge;
    private double _zoomPercent = 100;
    private int _previewPixelWidth;
    private int _previewPixelHeight;
    private double _previewViewportWidth = FitViewportWidth;
    private double _previewViewportHeight = FitViewportHeight;
    private string _statusText = string.Empty;
    private NormalizedRect? _draftFillInRegion;
    private bool _isSelectingFillInRegion;
    private bool _applyRegionPerPage;
    private bool _autoFitPending = true;

    public BatchWorkspaceViewModel(HomeworkBatch batch, IFillInEngine? fillInEngine = null)
    {
        Batch = batch;
        _fillInEngine = fillInEngine ?? new SkiaFillInEngine();
        Refresh();
    }

    public HomeworkBatch Batch { get; }

    public ObservableCollection<PageItemViewModel> PendingPages { get; } = [];
    public ObservableCollection<PageItemViewModel> MatchedPages { get; } = [];
    public ObservableCollection<PageItemViewModel> VisiblePages { get; } = [];
    public ObservableCollection<StudentRowViewModel> Students { get; } = [];
    public ObservableCollection<ClassFilterItemViewModel> ClassFilters { get; } = [];
    public ObservableCollection<PageItemViewModel> StudentPages { get; } = [];

    public string Title => $"{Batch.Name} · 共 {Batch.Pages.Count} 张";
    public string LeftHeader => $"待处理 共{PendingPages.Count}条";

    public bool HasPages => Batch.Pages.Count > 0;

    public bool ShowAllPages
    {
        get => _showAllPages;
        set
        {
            if (SetProperty(ref _showAllPages, value)) ApplyPageFilter();
        }
    }

    public PageItemViewModel? SelectedPage
    {
        get => _selectedPage;
        set => SetSelectedPage(value);
    }

    public StudentRowViewModel? SelectedStudent
    {
        get => _selectedStudent;
        set
        {
            if (!SetProperty(ref _selectedStudent, value)) return;
            OnPropertyChanged(nameof(HasSelectedStudent));
            OnPropertyChanged(nameof(SelectedStudentPagesTitle));
            RefreshStudentPages();
        }
    }

    public bool HasSelectedStudent => _selectedStudent is not null;

    public string SelectedStudentPagesTitle =>
        _selectedStudent is null ? string.Empty : $"{_selectedStudent.Name} 的作业页";

    /// <summary>每人应交页数；改它只影响判断与筛选，不改动已匹配的页。</summary>
    public decimal ExpectedPagesPerStudent
    {
        get => Batch.ExpectedPagesPerStudent;
        set
        {
            var pages = (int)decimal.Round(value);
            if (pages == Batch.ExpectedPagesPerStudent) return;
            try
            {
                Batch.SetExpectedPages(pages);
            }
            catch (HomeworkException ex)
            {
                SetStatus(ErrorText.Describe(ex, "设置应交页数"));
                OnPropertyChanged();
                return;
            }
            OnPropertyChanged();
            Refresh();
            SetStatus($"每人应交页数已设为 {Batch.ExpectedPagesPerStudent} 页。");
        }
    }

    public ClassFilterItemViewModel? SelectedClassFilter
    {
        get => _selectedClassFilter;
        set
        {
            if (SetProperty(ref _selectedClassFilter, value)) ApplyStudentFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyStudentFilter();
        }
    }

    public bool ShowOnlyUnmatchedStudents
    {
        get => _studentFilter == StudentFilter.Unmatched;
        set { if (value) SetStudentFilter(StudentFilter.Unmatched); }
    }

    public bool ShowOnlyMatchedStudents
    {
        get => _studentFilter == StudentFilter.Matched;
        set { if (value) SetStudentFilter(StudentFilter.Matched); }
    }

    public bool ShowAllStudents
    {
        get => _studentFilter == StudentFilter.All;
        set { if (value) SetStudentFilter(StudentFilter.All); }
    }

    private void SetStudentFilter(StudentFilter filter)
    {
        if (_studentFilter == filter) return;
        _studentFilter = filter;
        OnPropertyChanged(nameof(ShowOnlyUnmatchedStudents));
        OnPropertyChanged(nameof(ShowOnlyMatchedStudents));
        OnPropertyChanged(nameof(ShowAllStudents));
        ApplyStudentFilter();
    }

    public string PreviewImagePath
    {
        get => _previewImagePath;
        private set => SetProperty(ref _previewImagePath, value);
    }

    public double ZoomPercent
    {
        get => _zoomPercent;
        set
        {
            var clamped = ZoomMath.Clamp(value);
            if (SetProperty(ref _zoomPercent, clamped))
            {
                OnPropertyChanged(nameof(ZoomText));
                OnPropertyChanged(nameof(PreviewDisplayWidth));
                OnPropertyChanged(nameof(PreviewDisplayHeight));
                RequestHighResPreview();
            }
        }
    }

    public string ZoomText => ZoomMath.Format(ZoomPercent);

    /// <summary>选中页的原始像素尺寸。PDF 页缺尺寸时回落到预览图（缩略图 PNG）的解码尺寸。</summary>
    public int PreviewPixelWidth
    {
        get => _previewPixelWidth;
        private set => SetProperty(ref _previewPixelWidth, value);
    }

    public int PreviewPixelHeight
    {
        get => _previewPixelHeight;
        private set => SetProperty(ref _previewPixelHeight, value);
    }

    /// <summary>预览图在界面上的显示尺寸（DIP）= 原始像素 × 缩放比例，四舍五入。</summary>
    public double PreviewDisplayWidth =>
        Math.Round(PreviewPixelWidth * ZoomPercent / 100.0, MidpointRounding.AwayFromZero);

    public double PreviewDisplayHeight =>
        Math.Round(PreviewPixelHeight * ZoomPercent / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>预览视口尺寸，由窗口在尺寸变化时写入，供「适应窗口」计算缩放。</summary>
    public double PreviewViewportWidth
    {
        get => _previewViewportWidth;
        set
        {
            SetProperty(ref _previewViewportWidth, value);
            TryAutoFit();
        }
    }

    public double PreviewViewportHeight
    {
        get => _previewViewportHeight;
        set
        {
            SetProperty(ref _previewViewportHeight, value);
            TryAutoFit();
        }
    }

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;
    public string? UndoLabel => _undo.UndoLabel;
    public string? RedoLabel => _undo.RedoLabel;

    public int PendingCount => PendingPages.Count;
    public int MatchedCount => MatchedPages.Count;
    public int StudentsWithoutPagesCount => _allStudents.Count(s => s.IsUnmatched);
    public int IncompleteStudentsCount => _allStudents.Count(s => !s.IsComplete);
    public int MissingPageCount => _allStudents.Sum(s => s.MissingPages);

    public bool CanCommit => Batch.Pages.Count > 0
                             && Batch.UnmatchedCount == 0
                             && Batch.State == HomeworkBatchState.Editing;

    public string CommitBlockedReason
    {
        get
        {
            if (Batch.State != HomeworkBatchState.Editing)
                return "当前批次已结束编辑，不能提交。";
            if (Batch.Pages.Count == 0)
                return "还没有加入任何页。";
            if (Batch.UnmatchedCount > 0)
                return $"还有 {Batch.UnmatchedCount} 张未匹配，请匹配或删除后再提交。";
            return string.Empty;
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ----------------------------- 填涂区与框选 -----------------------------

    /// <summary>批次级填涂区代理；写入走 Core 的 SetFillInRegion，越界 / 非编辑态只报状态。</summary>
    public NormalizedRect? FillInRegion
    {
        get => Batch.FillInRegion;
        set
        {
            try
            {
                Batch.SetFillInRegion(value);
            }
            catch (HomeworkException ex)
            {
                SetStatus(ErrorText.Describe(ex, "设置填涂区"));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(FillInRegionSummary));
        }
    }

    /// <summary>填涂列数代理；未显式设置时回落到名单自动推断值，写入校验 1–12。</summary>
    public int FillInColumns
    {
        get => Batch.FillInColumns ?? Batch.AutoColumns();
        set
        {
            try
            {
                Batch.SetFillInColumns(value);
            }
            catch (HomeworkException ex)
            {
                SetStatus(ErrorText.Describe(ex, "设置填涂列数"));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(FillInRegionSummary));
        }
    }

    public string FillInRegionSummary =>
        Batch.FillInRegion is null ? "填涂区：未设置" : $"填涂区：已设置 · {FillInColumns} 列";

    public bool IsSelectingFillInRegion
    {
        get => _isSelectingFillInRegion;
        set => SetProperty(ref _isSelectingFillInRegion, value);
    }

    /// <summary>框选控件双向绑定的草稿矩形；只有点「应用」才写回批次或单页。</summary>
    public NormalizedRect? DraftFillInRegion
    {
        get => _draftFillInRegion;
        set => SetProperty(ref _draftFillInRegion, value);
    }

    /// <summary>「仅本页」勾选状态；未选页时界面禁用，故 CanApplyPerPage 为 false。</summary>
    public bool ApplyRegionPerPage
    {
        get => _applyRegionPerPage;
        set => SetProperty(ref _applyRegionPerPage, value);
    }

    public bool CanApplyPerPage => SelectedPage is not null;

    public event EventHandler? AbandonRequested;

    // ----------------------------- 选择与预览 -----------------------------

    public void SelectPage(PageItemViewModel page) => SetSelectedPage(page);

    private void SetSelectedPage(PageItemViewModel? page)
    {
        var resolved = page is null ? null : _allPages.FirstOrDefault(p => p.Id == page.Id) ?? page;
        if (!ReferenceEquals(_selectedPage, resolved))
        {
            foreach (var item in _allPages) item.IsSelected = false;
            _selectedPage = resolved;
            if (resolved is not null) resolved.IsSelected = true;
            OnPropertyChanged(nameof(SelectedPage));
            OnPropertyChanged(nameof(CanApplyPerPage));
        }
        PreviewImagePath = _selectedPage?.ThumbnailPath ?? string.Empty;
        RefreshPreviewPixelDimensions();
        TryAutoFit();
        RequestHighResPreview();
    }

    private void RequestHighResPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        var page = _selectedPage?.Page;
        var cache = _cache;
        if (page is null || cache is null) return;

        var edge = (int)Math.Clamp(
            Math.Max(PreviewPixelWidth, PreviewDisplayWidth) * 2,
            2000, 6000);
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _previewEdge = edge;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token);
                var cached = cache.FindPreview(page, edge);
                var path = string.IsNullOrEmpty(cached)
                    ? PageImporter.RenderPreview(page, cache, edge, cts.Token)
                    : cached;
                cts.Token.ThrowIfCancellationRequested();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested) PreviewImagePath = path;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // 高清渲染失败时继续显示已有缩略图。
            }
        }, cts.Token);
    }

    private void RefreshPreviewPixelDimensions()
    {
        var page = _selectedPage?.Page;
        int width = 0;
        int height = 0;
        if (page is not null)
        {
            width = page.PixelWidth;
            height = page.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                var (previewWidth, previewHeight) = ReadPngSize(_selectedPage!.ThumbnailPath);
                if (width <= 0) width = previewWidth;
                if (height <= 0) height = previewHeight;
            }
        }

        PreviewPixelWidth = width;
        PreviewPixelHeight = height;
        OnPropertyChanged(nameof(PreviewDisplayWidth));
        OnPropertyChanged(nameof(PreviewDisplayHeight));
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (0, 0);
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < 24) return (0, 0);
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
                return (0, 0);
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
            return (width > 0 ? width : 0, height > 0 ? height : 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void ZoomIn() => ZoomPercent = ZoomMath.Step(ZoomPercent, 1);
    public void ZoomOut() => ZoomPercent = ZoomMath.Step(ZoomPercent, -1);

    /// <summary>滚轮缩放：设置任意百分比（仍经 Clamp），不走预设阶梯，也不触发自动适应。</summary>
    public void SetZoomPercent(double percent) => ZoomPercent = ZoomMath.Clamp(percent);

    /// <summary>
    /// 首次选中页且拿到有效视口时，若页面在 100% 下宽于视口就自动适应一次。
    /// 之后（换页、改窗口大小）不再自动干预，避免覆盖老师手动调好的缩放。
    /// </summary>
    private void TryAutoFit()
    {
        if (!_autoFitPending) return;
        if (SelectedPage is null || PreviewPixelWidth <= 0 || PreviewViewportWidth <= 0) return;
        if (PreviewDisplayWidth > PreviewViewportWidth) ZoomToFit();
        _autoFitPending = false;
    }

    public void ZoomToFit() =>
        ZoomPercent = ZoomMath.Fit(PreviewPixelWidth, PreviewPixelHeight,
            PreviewViewportWidth, PreviewViewportHeight);

    // ----------------------------- 编辑（统一入口） -----------------------------

    private void RecordChange(string label, Action action)
    {
        if (Batch.State != HomeworkBatchState.Editing)
        {
            SetStatus("当前批次已结束编辑，不能修改。");
            return;
        }

        var before = Batch.Capture();
        try
        {
            action();
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "修改批次"));
            return;
        }

        var after = Batch.Capture();
        if (before.Equals(after)) return; // 内容没变不入栈（滑块 / 重复点击连发）。

        _undo.Record(label, before, after);
        Refresh();
        RaiseUndoRedo();
    }

    public void MatchStudent(StudentRowViewModel student)
    {
        if (student.IsComplete)
        {
            // 已交齐的学生不再接受新页；点它是为了查看并撤回。
            SelectedStudent = student;
            ShowAllPages = true;
            SetStatus($"「{student.Name}」已交齐 {student.PageCount} 页，可在下方撤回某一页。");
            return;
        }

        if (SelectedPage is null)
        {
            SetStatus("请先选择一页作业。");
            return;
        }

        var pageId = SelectedPage.Id;
        var currentIndex = SelectedPage.Page.Index;
        var studentId = student.Id;
        var studentName = student.Name;

        RecordChange($"匹配 {studentName}", () => Batch.Match(pageId, studentId));
        if (Batch.State != HomeworkBatchState.Editing) return;

        // 自动前进到下一张未匹配的页：先找 Index 更大的，没有就回到开头。
        var pending = _allPages.Where(p => p.Page.StudentId is null).OrderBy(p => p.Page.Index).ToList();
        var next = pending.FirstOrDefault(p => p.Page.Index > currentIndex) ?? pending.FirstOrDefault();
        SetSelectedPage(next);
    }

    public void UnmatchSelected()
    {
        if (SelectedPage is null)
        {
            SetStatus("请先选择一页作业。");
            return;
        }
        var pageId = SelectedPage.Id;
        RecordChange("取消匹配", () => Batch.Unmatch(pageId));
    }

    /// <summary>撤回某一页的匹配；该页回到待处理，学生退回「未匹配」。</summary>
    public void UnmatchPage(PageItemViewModel page)
    {
        var studentId = page.Page.StudentId;
        RecordChange("撤回匹配", () => Batch.Unmatch(page.Id));
        if (studentId.HasValue)
            SelectedStudent = _allStudents.FirstOrDefault(s => s.Id == studentId.Value);
    }

    /// <summary>撤回某学生名下全部页的匹配。</summary>
    public void UnmatchStudent(StudentRowViewModel student)
    {
        var pages = Batch.PagesOf(student.Id).ToList();
        if (pages.Count == 0)
        {
            SetStatus($"「{student.Name}」还没有匹配的页。");
            return;
        }

        RecordChange($"撤回 {student.Name} 的全部页", () =>
        {
            foreach (var page in pages) Batch.Unmatch(page.Id);
        });
        SelectedStudent = _allStudents.FirstOrDefault(s => s.Id == student.Id);
        SetStatus($"已撤回「{student.Name}」的 {pages.Count} 页匹配。");
    }

    public void DeleteSelectedPage()
    {
        if (SelectedPage is null)
        {
            SetStatus("请先选择一页作业。");
            return;
        }
        var pageId = SelectedPage.Id;
        var index = SelectedPage.Page.Index;
        RecordChange("删除页", () => Batch.DeletePage(pageId));
        if (Batch.State != HomeworkBatchState.Editing) return;

        // 选中补上来的那一页；删的是末页则退到新的末页。
        var next = _allPages.FirstOrDefault(p => p.Page.Index == index) ?? _allPages.LastOrDefault();
        SetSelectedPage(next);
    }

    public void MoveSelectedPage(int delta)
    {
        if (SelectedPage is null)
        {
            SetStatus("请先选择一页作业。");
            return;
        }
        var pageId = SelectedPage.Id;
        RecordChange("移动页", () => Batch.MovePage(pageId, delta));
    }

    public void Undo()
    {
        if (!_undo.CanUndo) return;
        var target = _undo.Undo(Batch.Capture());
        if (target is null) return;
        try
        {
            Batch.Restore(target);
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "撤销"));
            return;
        }
        Refresh();
        RaiseUndoRedo();
    }

    public void Redo()
    {
        if (!_undo.CanRedo) return;
        var target = _undo.Redo(Batch.Capture());
        if (target is null) return;
        try
        {
            Batch.Restore(target);
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "重做"));
            return;
        }
        Refresh();
        RaiseUndoRedo();
    }

    // ----------------------------- 名单 -----------------------------

    public void ApplyRoster(RosterImportResult result)
    {
        if (Batch.State != HomeworkBatchState.Editing)
        {
            SetStatus("当前批次已结束编辑，不能修改。");
            return;
        }
        try
        {
            Batch.LoadRoster(result.Classes, result.Students);
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "导入名单"));
            return;
        }

        // 学生 Id 全部换新，旧快照无法 Restore——清空撤销栈，不记假撤销。
        _undo.Clear();
        Refresh();
        RaiseUndoRedo();
        SetStatus(RosterSummary(result.Students.Count, result.Classes.Count, result.Warnings, result.SkippedRows)
                  + " " + RosterReloadNotice());
    }

    public void ApplyPaste(RosterPasteResult result)
    {
        if (Batch.State != HomeworkBatchState.Editing)
        {
            SetStatus("当前批次已结束编辑，不能修改。");
            return;
        }
        try
        {
            Batch.LoadRoster(result.Classes, result.Students);
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "导入名单"));
            return;
        }

        _undo.Clear();
        Refresh();
        RaiseUndoRedo();
        SetStatus(PasteSummary(result.Students.Count, result.Classes.Count, result.Errors)
                  + " " + RosterReloadNotice());
    }

    // ----------------------------- 导入页 -----------------------------

    public bool ImportImages(
        IReadOnlyList<string> paths,
        PageThumbnailCache cache,
        IProgress<int>? progress,
        CancellationToken token)
    {
        try
        {
            var added = PageImporter.ImportImages(Batch, paths, cache, progress, token, out var warnings);
            _cache = cache;
            Refresh();
            SetStatus(ImportSummary(added.Count, warnings));
            return true;
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "导入图片"));
            return false;
        }
    }

    public bool ImportPdf(
        string path,
        PageThumbnailCache cache,
        int previewDpi,
        IProgress<int>? progress,
        CancellationToken token)
    {
        try
        {
            var added = PageImporter.ImportPdf(Batch, path, cache, previewDpi, progress, token, out var warnings);
            _cache = cache;
            Refresh();
            SetStatus(ImportSummary(added.Count, warnings));
            return true;
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "导入 PDF"));
            return false;
        }
    }

    // ----------------------------- 填涂识别 -----------------------------

    /// <summary>进入框选模式：草稿先填入当前页的有效区域，方便在旧区域上微调。</summary>
    public void StartFillInRegionSelection()
    {
        if (Batch.State != HomeworkBatchState.Editing)
        {
            SetStatus("当前批次已结束编辑，不能修改填涂区。");
            return;
        }

        DraftFillInRegion = SelectedPage is null ? null : Batch.EffectiveFillInRegion(SelectedPage.Page);
        ApplyRegionPerPage = false;
        IsSelectingFillInRegion = true;
        SetStatus("在预览上拖动框选填涂数字格，完成后点「应用」。");
    }

    /// <summary>把草稿写回：勾选「仅本页」时只写当前页覆盖，否则写批次级区域。</summary>
    public void ApplyFillInRegion(bool perPage)
    {
        if (Batch.State != HomeworkBatchState.Editing)
        {
            SetStatus("当前批次已结束编辑，不能修改填涂区。");
            return;
        }

        if (DraftFillInRegion is null || !DraftFillInRegion.IsUsable)
        {
            SetStatus("请先框选一个有效的填涂区域。");
            return;
        }

        var region = DraftFillInRegion.Clamp();
        if (perPage && SelectedPage is not null)
        {
            SelectedPage.Page.FillInRegionOverride = region;
            SetStatus("填涂区已应用到本页。");
        }
        else
        {
            try
            {
                Batch.SetFillInRegion(region);
            }
            catch (HomeworkException ex)
            {
                SetStatus(ErrorText.Describe(ex, "应用填涂区"));
                return;
            }
            SetStatus("填涂区已应用到全部页。");
        }

        IsSelectingFillInRegion = false;
        OnPropertyChanged(nameof(FillInRegion));
        OnPropertyChanged(nameof(FillInRegionSummary));
        Refresh();
    }

    public void CancelFillInRegion()
    {
        DraftFillInRegion = null;
        IsSelectingFillInRegion = false;
        SetStatus("已取消框选。");
    }

    /// <summary>清除批次区域与所有单页覆盖；区域不进撤销快照，故不记撤销步。</summary>
    public void ClearFillInRegion()
    {
        try
        {
            Batch.SetFillInRegion(null);
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "清除填涂区"));
            return;
        }
        foreach (var page in Batch.Pages) page.FillInRegionOverride = null;
        DraftFillInRegion = null;
        IsSelectingFillInRegion = false;
        OnPropertyChanged(nameof(FillInRegion));
        OnPropertyChanged(nameof(FillInRegionSummary));
        Refresh();
        SetStatus("已清除填涂区（含所有单页覆盖）。");
    }

    /// <summary>
    /// 逐页识别填涂并按学号唯一匹配。先算后写：全部页处理完且未取消才一次性写入，
    /// 整次运行只记一步撤销。检测失败 / 歧义 / 无匹配 / 已交齐的页只写识别原因，不写匹配。
    /// </summary>
    public async Task RunFillInRecognitionAsync(
        IProgress<int>? progress = null, CancellationToken token = default)
    {
        if (Batch.State != HomeworkBatchState.Editing)
        {
            SetStatus("当前批次已结束编辑，不能识别。");
            return;
        }

        var targets = Batch.Pages
            .Where(p => p.StudentId is null)
            .OrderBy(p => p.Index)
            .ToList();
        if (targets.Count == 0)
        {
            SetStatus("没有待识别的页。");
            return;
        }

        var columns = Batch.FillInColumns ?? Batch.AutoColumns();
        if (columns < FillInOptions.MinColumns || columns > FillInOptions.MaxColumns)
        {
            SetStatus($"填涂列数 {columns} 无效，请重新设置。");
            return;
        }

        var classNames = Batch.Classes.ToDictionary(c => c.Id, c => c.Name);
        var expected = Batch.ExpectedPagesPerStudent;
        var assigned = new Dictionary<Guid, int>();
        var results = new List<(Guid PageId, PageRecognition Recognition, Guid? StudentId)>();

        await Task.Run(() =>
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var page = targets[i];
                var now = DateTimeOffset.Now;
                var region = Batch.EffectiveFillInRegion(page);

                if (region is null || !region.IsUsable)
                {
                    results.Add((page.Id, new PageRecognition(
                        RecognitionMethod.FillIn, string.Empty, 0, "未设置填涂区", now), null));
                    progress?.Report((i + 1) * 100 / targets.Count);
                    continue;
                }

                FillInDetectionResult detection;
                try
                {
                    using var decoded = PageImageDecoder.DecodePage(
                        page, FillInOptions.RecognitionLongEdge, FillInOptions.PdfRecognitionDpi);
                    detection = _fillInEngine.Detect(decoded.Bitmap, region, columns);
                }
                catch (Exception ex)
                {
                    results.Add((page.Id, new PageRecognition(
                        RecognitionMethod.FillIn, string.Empty, 0, ErrorText.Describe(ex, "识别"), now), null));
                    progress?.Report((i + 1) * 100 / targets.Count);
                    continue;
                }

                var digits = string.Concat(detection.Digits.Select(d => d.ToString()));
                if (!detection.Succeeded)
                {
                    results.Add((page.Id, new PageRecognition(
                        RecognitionMethod.FillIn, digits, 0, detection.FailureReason, now), null));
                    progress?.Report((i + 1) * 100 / targets.Count);
                    continue;
                }

                var resolution = FillInNumberResolver.Resolve(digits, Batch.Students, classNames);
                Guid? matchedStudent = null;
                string? matchFailure = null;
                if (resolution.Status == FillInNumberStatus.Matched && resolution.StudentId.HasValue)
                {
                    var studentId = resolution.StudentId.Value;
                    var already = Batch.PagesOf(studentId).Count
                                  + (assigned.TryGetValue(studentId, out var n) ? n : 0);
                    if (already < expected)
                    {
                        matchedStudent = studentId;
                        assigned[studentId] = (assigned.TryGetValue(studentId, out var m) ? m : 0) + 1;
                    }
                    else
                    {
                        matchFailure = $"该生已交齐 {expected} 页";
                    }
                }
                else
                {
                    matchFailure = resolution.FailureReason;
                }

                results.Add((page.Id, new PageRecognition(
                    RecognitionMethod.FillIn, digits, 1, null, now, matchFailure), matchedStudent));
                progress?.Report((i + 1) * 100 / targets.Count);
            }
        });

        if (token.IsCancellationRequested)
        {
            SetStatus("已取消，未改动。");
            return;
        }

        var matched = 0;
        RecordChange("识别填涂", () =>
        {
            foreach (var (pageId, recognition, studentId) in results)
            {
                Batch.SetRecognition(pageId, recognition);
                if (!studentId.HasValue) continue;
                // 双保险：绝不覆盖已有匹配，也不给已交齐的学生加页。
                var page = Batch.Pages.FirstOrDefault(p => p.Id == pageId);
                if (page is null || page.StudentId is not null) continue;
                if (Batch.PagesOf(studentId.Value).Count >= expected) continue;
                Batch.Match(pageId, studentId.Value);
                matched++;
            }
        });

        SetStatus($"识别完成：{matched} 页匹配，{results.Count - matched} 页待处理。");
    }

    // ----------------------------- 提交 / 放弃 / 导出 -----------------------------

    public void MarkCommitted()
    {
        try
        {
            Batch.Commit();
            Refresh();
            RaiseUndoRedo();
            SetStatus("批次已提交，可以导出了。");
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "提交批次"));
        }
    }

    public void Abandon()
    {
        if (Batch.State == HomeworkBatchState.Editing)
        {
            try
            {
                Batch.Abandon();
            }
            catch (HomeworkException ex)
            {
                SetStatus(ErrorText.Describe(ex, "放弃批次"));
                return;
            }
        }

        _undo.Clear();
        _allPages.Clear();
        _allStudents.Clear();
        PendingPages.Clear();
        MatchedPages.Clear();
        VisiblePages.Clear();
        Students.Clear();
        StudentPages.Clear();
        ClassFilters.Clear();
        _selectedPage = null;
        _selectedStudent = null;
        _selectedClassFilter = null;
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(SelectedStudent));
        OnPropertyChanged(nameof(SelectedClassFilter));
        PreviewImagePath = string.Empty;
        RefreshPreviewPixelDimensions();
        RaiseDerived();
        RaiseUndoRedo();
        AbandonRequested?.Invoke(this, EventArgs.Empty);
    }

    public HomeworkExportPlan BuildExportPlan(
        HomeworkExportOptions options,
        IFileProbe probe,
        DateTime? now = null)
    {
        try
        {
            return HomeworkExportPlanner.Build(Batch, options, probe, now);
        }
        catch (HomeworkException ex)
        {
            SetStatus(ErrorText.Describe(ex, "生成导出计划"));
            throw;
        }
    }

    public void SetStatus(string text) => RunOnUi(() => StatusText = text);

    // ----------------------------- 刷新与筛选 -----------------------------

    private static void RunOnUi(Action action)
    {
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(action);
        else
            action();
    }

    private void Refresh()
    {
        RunOnUi(RefreshCore);
    }

    private void RefreshCore()
    {
        var oldPageId = _selectedPage?.Id;
        var oldStudentId = _selectedStudent?.Id;
        var oldFilterId = _selectedClassFilter?.ClassId;

        var studentById = Batch.Students.ToDictionary(s => s.Id);
        var classNameById = Batch.Classes.ToDictionary(c => c.Id, c => c.Name);
        var classOrder = Batch.Classes.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var duplicateNames = Batch.Students
            .GroupBy(s => (s.ClassId, s.Name))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        _allPages = Batch.Pages
            .OrderBy(p => p.Index)
            .Select(p => new PageItemViewModel(
                p,
                p.StudentId.HasValue && studentById.TryGetValue(p.StudentId.Value, out var s) ? s : null,
                FindThumbnail(_cache, p)))
            .ToList();

        _allStudents = Batch.Students
            .Select((s, rosterIndex) => (s, rosterIndex))
            .OrderBy(x => classOrder.TryGetValue(x.s.ClassId, out var ci) ? ci : int.MaxValue)
            .ThenBy(x => x.rosterIndex)
            .Select(x => new StudentRowViewModel(
                x.s,
                classNameById.TryGetValue(x.s.ClassId, out var cn) ? cn : string.Empty,
                Batch.Pages.Count(p => p.StudentId == x.s.Id),
                Batch.ExpectedPagesPerStudent,
                duplicateNames.Contains((x.s.ClassId, x.s.Name))))
            .ToList();

        PendingPages.Clear();
        foreach (var page in _allPages.Where(p => p.Page.StudentId is null)) PendingPages.Add(page);

        MatchedPages.Clear();
        foreach (var page in _allPages.Where(p => p.Page.StudentId is not null)) MatchedPages.Add(page);

        ClassFilters.Clear();
        ClassFilters.Add(new ClassFilterItemViewModel(null));
        foreach (var cls in Batch.Classes) ClassFilters.Add(new ClassFilterItemViewModel(cls));
        _selectedClassFilter = ClassFilters.FirstOrDefault(f => f.ClassId == oldFilterId) ?? ClassFilters[0];
        OnPropertyChanged(nameof(SelectedClassFilter));

        ApplyPageFilter();
        ApplyStudentFilter();

        SetSelectedPage(oldPageId is null ? null : _allPages.FirstOrDefault(p => p.Id == oldPageId));
        _selectedStudent = oldStudentId is null ? null : _allStudents.FirstOrDefault(s => s.Id == oldStudentId);
        OnPropertyChanged(nameof(SelectedStudent));
        OnPropertyChanged(nameof(HasSelectedStudent));
        OnPropertyChanged(nameof(SelectedStudentPagesTitle));
        RefreshStudentPages();

        RaiseDerived();
    }

    private void RefreshStudentPages()
    {
        StudentPages.Clear();
        if (_selectedStudent is null) return;
        foreach (var page in _allPages.Where(p => p.Page.StudentId == _selectedStudent.Id).OrderBy(p => p.Page.Index))
            StudentPages.Add(page);
    }

    private void ApplyPageFilter()
    {
        VisiblePages.Clear();
        var source = ShowAllPages ? _allPages : _allPages.Where(p => p.Page.StudentId is null);
        foreach (var page in source.DistinctBy(p => p.Page.Id)) VisiblePages.Add(page);
    }

    private void ApplyStudentFilter()
    {
        Students.Clear();
        IEnumerable<StudentRowViewModel> query = _allStudents;

        if (_selectedClassFilter?.ClassId is Guid classId)
            query = query.Where(s => s.Student.ClassId == classId);
        query = _studentFilter switch
        {
            StudentFilter.Unmatched => query.Where(s => !s.IsComplete),
            StudentFilter.Matched => query.Where(s => s.IsComplete),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            query = query.Where(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Number.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.ClassName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var student in query) Students.Add(student);
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(LeftHeader));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(MatchedCount));
        OnPropertyChanged(nameof(StudentsWithoutPagesCount));
        OnPropertyChanged(nameof(IncompleteStudentsCount));
        OnPropertyChanged(nameof(MissingPageCount));
        OnPropertyChanged(nameof(ExpectedPagesPerStudent));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CommitBlockedReason));
        OnPropertyChanged(nameof(FillInRegion));
        OnPropertyChanged(nameof(FillInColumns));
        OnPropertyChanged(nameof(FillInRegionSummary));
    }

    private void RaiseUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }

    /// <summary>
    /// Core 的缩略图写入只返回路径给内部回滚表，不回传给调用方；这里按缓存的命名规则
    /// 「{文件名}_p{页码}_*.png」反查。找不到就留空，界面显示占位，不影响功能。
    /// </summary>
    private static string FindThumbnail(PageThumbnailCache? cache, HomeworkPage page)
    {
        if (cache is null) return string.Empty;
        try
        {
            var stem = Path.GetFileNameWithoutExtension(page.SourcePath);
            var invalid = Path.GetInvalidFileNameChars();
            stem = new string(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            if (string.IsNullOrEmpty(stem)) stem = "page";
            var prefix = $"{stem}_p{Math.Max(1, page.SourcePageNumber)}_";
            return Directory.EnumerateFiles(cache.SessionDirectory, prefix + "*.png").FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ----------------------------- 文案 -----------------------------

    private static string ImportSummary(int added, IReadOnlyList<string> warnings)
    {
        var head = $"已加入 {added} 页。";
        return warnings.Count == 0 ? head : head + " " + string.Join(" ", warnings);
    }

    private static string RosterSummary(int students, int classes, IReadOnlyList<string> warnings, int skipped)
    {
        var text = $"已导入 {classes} 个班级、{students} 名学生。";
        var reasons = SharedRosterImport.CountSkipReasons(warnings);
        if (reasons.Total > 0)
        {
            if (reasons.EmptyNameRows > 0) text += $" 空姓名 {reasons.EmptyNameRows} 行";
            if (reasons.DuplicateRows > 0) text += $" 重复学号或姓名 {reasons.DuplicateRows} 行";
            if (reasons.MissingRosters > 0) text += $" 名单缺失 {reasons.MissingRosters} 份";
            text += "。";
        }
        else if (skipped > 0)
        {
            // 普通名单导入的空姓名行不带告警，沿用原有话术。
            text += $" 跳过 {skipped} 行空姓名。";
        }

        if (warnings.Count > 0) text += " " + string.Join(" ", warnings);
        return text;
    }

    private static string PasteSummary(int students, int classes, IReadOnlyList<string> errors)
    {
        var text = $"已导入 {classes} 个班级、{students} 名学生。";
        return errors.Count == 0 ? text : text + " " + string.Join(" ", errors);
    }

    /// <summary>名单重导后的提示：旧识别结果失效需重新识别；显式列数与新名单不符时追加确认提示。</summary>
    private string RosterReloadNotice()
    {
        var text = "名单已更换，请重新识别。";
        if (Batch.FillInColumns is int columns)
        {
            var auto = Batch.AutoColumns();
            if (columns != auto)
                text += $" 当前列数 {columns} 与名单最长学号 {auto} 位不同，请确认模板列数。";
        }
        return text;
    }
}
