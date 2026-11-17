using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SeatGrid.Core.Export;
using SeatGrid.Core.Exam;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;
using SeatGrid.Core.Roster;
using SeatGrid.Core.Seating;

namespace SeatGrid.App;

public partial class SeatGridViewModel : ObservableObject
{
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#5A7A4B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#C6613F"));

    private readonly List<StudentViewModel> _students = new();
    private readonly Dictionary<SeatPosition, StudentViewModel> _assignments = new();
    private readonly Dictionary<SeatPosition, SeatCellViewModel> _seats = new();
    private bool _suppressRebuild;
    private bool _interactiveLayoutEdit;
    private bool _structureDirty;
    private bool _scaleDirty;
    private bool _layoutRefreshQueued;

    // 画刷是绑定高频读取的属性，按颜色缓存，颜色变化时统一失效。
    private IBrush? _seatBackgroundBrush;
    private IBrush? _emptySeatBrush;
    private IBrush? _seatTextBrush;
    private IBrush? _seatBorderBrush;
    private IBrush? _podiumBackgroundBrush;
    private IBrush? _podiumTextBrush;
    private IBrush? _groupLabelBrush;
    private IBrush? _femaleMarkBrush;

    /// <summary>用列表整体替换，避免 3000 条逐项 Add 触发等量集合通知。</summary>
    [ObservableProperty] private IReadOnlyList<StudentViewModel> _filteredStudents = Array.Empty<StudentViewModel>();

    public ObservableCollection<GroupViewModel> Groups { get; } = new();

    /// <summary>排座约束（锁定 / 互斥 / 结对 / 上期同桌 / 区域）与排座说明。</summary>
    public SeatConstraintsViewModel Constraints { get; private set; }

    public IReadOnlyList<string> ArrangeModes { get; } = new[]
    {
        "名单顺序", "男女交替", "同性同桌", "随机", "按身高", "按视力",
        "成绩均衡", "成绩分层",
    };

    public IReadOnlyList<string> GenderOptions { get; } = new[] { "未知", "男", "女" };

    public SeatGridViewModel()
    {
        Constraints = new SeatConstraintsViewModel(_students);
        BuildGrid();
        SelectedStudent = null;
    }

    // ----------------------------- 撤销 / 重做 -----------------------------

    private readonly SeatUndoStack _undo = new();
    private bool _restoring;

    /// <summary>
    /// 挂起登记。导入名单 / 套用配置时内部的 AutoArrange 不该各记一步——
    /// 「换了一份数据」整体是一个起点，不是一串可撤回的编辑。
    /// </summary>
    private bool _suspendUndo;

    public bool CanUndo => _undo.CanUndo && !IsBusy;
    public bool CanRedo => _undo.CanRedo && !IsBusy;

    /// <summary>状态栏 / 按钮悬浮提示用：下一步撤销会退回哪一步。</summary>
    public string UndoLabel => _undo.NextUndoLabel is { } label ? $"撤销：{label}" : "撤销";
    public string RedoLabel => _undo.NextRedoLabel is { } label ? $"重做：{label}" : "重做";

    /// <summary>
    /// 把一次变更包成可撤销的一步。
    ///
    /// **先拍照再改**：<paramref name="change"/> 里任何一处失败都不影响已拍下的快照，
    /// 而拍快照本身只是拷数据、不会失败。恢复期间 <see cref="_restoring"/> 为真，
    /// 直接执行 change、不登记（撤销 / 重做自身不能入栈）。
    /// </summary>
    private void RecordChange(string label, Action change)
    {
        if (_restoring || _suspendUndo)
        {
            change();
            return;
        }

        var before = CaptureState(label);
        change();
        _undo.Push(before, label);
        NotifyUndoState();
    }

    /// <summary>拍一份当前状态。学生按 Id 克隆，座位用坐标，撤销时整体重建 ViewModel。</summary>
    private SeatStateSnapshot CaptureState(string label) => new()
    {
        Layout = CurrentLayout,
        Canvas = Canvas,
        Students = _students.Select(s => s.Model.CopyKeepingId()).ToList(),
        Assignments = _assignments.ToDictionary(kv => kv.Key, kv => kv.Value.Id),
        Pins = new Dictionary<Guid, SeatPosition>(Constraints.Pins),
        Mutex = Constraints.Mutex.Select(p => new SeatPairState(p.A, p.B)).ToList(),
        Deskmates = Constraints.Deskmates.Select(p => new SeatPairState(p.A, p.B)).ToList(),
        AvoidDeskmates = Constraints.AvoidDeskmates.Select(p => new SeatPairState(p.A, p.B)).ToList(),
        Zones = Constraints.Zones.ToDictionary(
            kv => kv.Key,
            kv => new SeatZoneState { Allowed = kv.Value.Allowed.ToList(), Denied = kv.Value.Denied.ToList() }),
        IsExam = IsExamMode && _examPlan is not null,
        Exam = IsExamMode && _examPlan is { } exam
            ? new ExamPlanSnapshot
            {
                Layout = exam.Layout,
                Assignments = exam.Assignments.ToDictionary(kv => kv.Key, kv => kv.Value.Id),
                SeatNumbers = new Dictionary<SeatPosition, int>(exam.SeatNumbers),
                Notes = exam.Notes.ToList(),
            }
            : null,
        Label = label,
    };

    public void Undo()
    {
        if (!_undo.CanUndo || IsBusy) return;

        var target = _undo.Undo(CaptureState("撤销"));
        if (target is null) return;

        RestoreState(target);
        SetStatus($"已撤销：{target.Label}");
        NotifyUndoState();
    }

    public void Redo()
    {
        if (!_undo.CanRedo || IsBusy) return;

        var target = _undo.Redo(CaptureState("重做"));
        if (target is null) return;

        RestoreState(target);
        SetStatus($"已重做：{target.Label}");
        NotifyUndoState();
    }

    /// <summary>
    /// 整体还原一份快照：名单、座位、约束、画布、布局。选中状态与搜索无关紧要，保留不动。
    ///
    /// 学生对象按 Id 复用已有 ViewModel 而不是新建：新造会让「已选中学生」「右侧编辑框」
    /// 与刚撤销回来的数据脱钩。
    /// </summary>
    private void RestoreState(SeatStateSnapshot snapshot)
    {
        _restoring = true;
        try
        {
            _suppressRebuild = true;
            GroupCount = snapshot.Layout.Groups;
            RowCount = snapshot.Layout.Rows;
            ColumnCount = snapshot.Layout.Columns;
            Canvas = snapshot.Canvas;
            _suppressRebuild = false;

            // 名单：按 Id 找现有实例，缺的才新建，多的删掉。顺序按快照。
            var existing = _students.ToDictionary(s => s.Id);
            _students.Clear();
            foreach (var model in snapshot.Students)
            {
                if (existing.TryGetValue(model.Id, out var student))
                {
                    CopyInto(student.Model, model);
                    student.Refresh();
                    _students.Add(student);
                }
                else
                {
                    _students.Add(new StudentViewModel(model));
                }
            }

            // 座位：先按快照重建坐标，再换成当前的 ViewModel 实例。
            _assignments.Clear();
            var byId = _students.ToDictionary(s => s.Id);
            foreach (var (position, id) in snapshot.Assignments)
                if (byId.TryGetValue(id, out var student)) _assignments[position] = student;

            // 考场状态：撤销「进入考场模式」必须把标志与考场安排一起退回去，
            // 否则座位已经是日常的，界面却还在按考场画。
            _dailySnapshot = null;
            _examPlan = null;
            if (snapshot.IsExam && snapshot.Exam is { } examSnapshot)
            {
                _examPlan = examSnapshot.ToExamPlan(byId.ToDictionary(kv => kv.Key, kv => kv.Value.Model));
                IsExamMode = true;
            }
            else
            {
                IsExamMode = false;
            }

            // 约束：整体替换。约束查看模型持的是学生列表的副本，名单变了要重建。
            Constraints = new SeatConstraintsViewModel(_students);
            Constraints.Apply(new ArrangeConstraints
            {
                Pins = new Dictionary<Guid, SeatPosition>(snapshot.Pins),
                Mutex = snapshot.Mutex.Select(p => StudentPair.Create(p.A, p.B)).ToList(),
                Deskmates = snapshot.Deskmates.Select(p => StudentPair.Create(p.A, p.B)).ToList(),
                AvoidDeskmates = snapshot.AvoidDeskmates.Select(p => StudentPair.Create(p.A, p.B)).ToList(),
                Zones = snapshot.Zones.ToDictionary(kv => kv.Key, kv => kv.Value.ToZone()),
            });
            OnPropertyChanged(nameof(Constraints));

            BuildGrid();
            RefreshStudents();
            NotifyCounts();
        }
        finally
        {
            _restoring = false;
        }
    }

    private static void CopyInto(Student target, Student source)
    {
        target.Name = source.Name;
        target.Gender = source.Gender;
        target.Number = source.Number;
        target.Height = source.Height;
        target.Vision = source.Vision;
        target.Score = source.Score;
        target.Notes = source.Notes;
        target.OriginalGroup = source.OriginalGroup;
    }

    private void NotifyUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }

    /// <summary>
    /// 供窗口层包住「约束弹窗 / 画布编辑器」这类跨层操作。
    /// 约束与画布的实际改动发生在窗口代码里，但撤销栈只有 VM 有一份——
    /// 让窗口直接操作 Constraints 而不经过这里，那些改动就撤不回去。
    /// </summary>
    public void RecordWindowChange(string label, Action change) => RecordChange(label, change);

    /// <summary>导入新名单这类「本就该从头开始」的操作会把撤销栈清空。</summary>
    private void ClearUndo()
    {
        _undo.Clear();
        NotifyUndoState();
    }

    // ----------------------------- 座位表设置 -----------------------------

    [ObservableProperty] private string _title = "班级座位表";
    [ObservableProperty] private bool _boldTitle = true;
    [ObservableProperty] private string _headTeacher = string.Empty;
    [ObservableProperty] private string _monitor = string.Empty;

    [ObservableProperty] private bool _isStudentView;
    [ObservableProperty] private double _groupCount = 4;
    [ObservableProperty] private double _rowCount = 6;
    [ObservableProperty] private double _columnCount = 2;
    [ObservableProperty] private double _cellScale = 85;
    [ObservableProperty] private double _podiumGap = 16;
    [ObservableProperty] private double _nameFontSize = 14;

    [ObservableProperty] private bool _boldNames;
    [ObservableProperty] private bool _hideEmptySeats;
    [ObservableProperty] private bool _markFemales = true;
    [ObservableProperty] private bool _spaceTwoCharNames = true;
    [ObservableProperty] private bool _showGroupRowHints = true;

    [ObservableProperty] private Color _seatBackgroundColor = Color.Parse("#FFFFFF");
    [ObservableProperty] private Color _emptySeatColor = Color.Parse("#FAF9F5");
    [ObservableProperty] private Color _seatTextColor = Color.Parse("#141413");
    [ObservableProperty] private Color _seatBorderColor = Color.Parse("#E7E3D8");
    [ObservableProperty] private Color _podiumBackgroundColor = Color.Parse("#F4F2EC");
    [ObservableProperty] private Color _podiumTextColor = Color.Parse("#85806F");
    [ObservableProperty] private Color _groupLabelColor = Color.Parse("#C6613F");
    [ObservableProperty] private Color _rowLabelColor = Color.Parse("#85806F");
    [ObservableProperty] private Color _femaleMarkColor = Color.Parse("#C6613F");

    [ObservableProperty] private int _arrangeModeIndex;

    /// <summary>教室画布：过道格、走廊宽度、门窗。不可变对象，替换即通知。</summary>
    [ObservableProperty] private SeatChartCanvas _canvas = SeatChartCanvas.Empty;

    [ObservableProperty] private double _rowGapPercent = 7;
    [ObservableProperty] private double _columnGapPercent = 7;
    [ObservableProperty] private double _groupGapPercent = 28;
    [ObservableProperty] private double _tierRowGrowthPercent;
    [ObservableProperty] private bool _showDoors = true;
    [ObservableProperty] private string _podiumLabel = "讲　台";
    [ObservableProperty] private double _podiumWidthPercent = 55;
    [ObservableProperty] private double _podiumHeightValue = 40;

    /// <summary>小组导师制：组内成绩第一任组长，组员环绕。</summary>
    [ObservableProperty] private bool _mentorEnabled;

    /// <summary>组长座位：组中心 / 第一排。</summary>
    [ObservableProperty] private int _mentorSeatIndex;

    public string MentorSeatLabel => MentorSeatIndex == 1 ? "第一排" : "组中心";

    /// <summary>座位表内容宽度（含行首把手列），门窗定位用。</summary>
    public double ChartContentWidth    {
        get
        {
            var offsets = SeatChartGeometry.Measure(CurrentLayout, ToSettings(), CurrentView);
            return offsets.GridWidth + 44;
        }
    }

    public string CanvasSummary
    {
        get
        {
            var layout = CurrentLayout;
            var aisles = layout.CellCount - layout.UsableSeatCount;
            return aisles == 0
                ? $"{layout.CellCount} 格 · {layout.UsableSeatCount} 座"
                : $"{layout.CellCount} 格 · {layout.UsableSeatCount} 座 · {aisles} 处过道";
        }
    }

    public string RowGapLabel => $"{(int)Math.Round(RowGapPercent)}%";
    public string ColumnGapLabel => $"{(int)Math.Round(ColumnGapPercent)}%";
    public string GroupGapLabel => $"{(int)Math.Round(GroupGapPercent)}%";
    public string TierRowGrowthLabel => $"{(int)Math.Round(TierRowGrowthPercent)}%";
    public string PodiumWidthLabel => $"{(int)Math.Round(PodiumWidthPercent)}%";
    public string PodiumHeightLabel => $"{(int)Math.Round(PodiumHeightValue)}px";

    partial void OnRowGapPercentChanged(double value)
    {
        OnPropertyChanged(nameof(RowGapLabel));
        RefreshSeatVisuals();
    }

    partial void OnColumnGapPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ColumnGapLabel));
        RefreshSeatVisuals();
    }

    partial void OnGroupGapPercentChanged(double value)
    {
        OnPropertyChanged(nameof(GroupGapLabel));
        RefreshSeatVisuals();
    }

    partial void OnTierRowGrowthPercentChanged(double value)
    {
        OnPropertyChanged(nameof(TierRowGrowthLabel));
        RefreshSeatVisuals();
    }

    partial void OnPodiumWidthPercentChanged(double value)
    {
        OnPropertyChanged(nameof(PodiumWidthLabel));
        UpdatePodium();
    }

    partial void OnPodiumHeightValueChanged(double value)
    {
        OnPropertyChanged(nameof(PodiumHeightLabel));
        UpdatePodium();
    }

    partial void OnCanvasChanged(SeatChartCanvas value) => OnPropertyChanged(nameof(CanvasSummary));

    [ObservableProperty] private double _podiumWidth = 320;
    [ObservableProperty] private Thickness _podiumTopMargin = new(0);
    [ObservableProperty] private Thickness _podiumBottomMargin = new(0, 16, 0, 0);

    // ----------------------------- 名单、座位状态 -----------------------------

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private StudentViewModel? _selectedStudent;
    [ObservableProperty] private SeatCellViewModel? _selectedSeat;

    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editNumber = string.Empty;
    [ObservableProperty] private int _editGenderIndex;
    [ObservableProperty] private string _editHeight = string.Empty;
    [ObservableProperty] private string _editVision = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;

    [ObservableProperty] private string _statusText = "导入名单后即可自动排座。";
    [ObservableProperty] private IBrush _statusBrush = SuccessBrush;
    [ObservableProperty] private bool _isBusy;

    // ---- 忙碌遮罩 ----
    // 与竞名 / 织序同一套：先占住 IsBusy，400ms 后仍未结束才把遮罩亮出来。
    // 400ms 是刻意的：小文件（几十人的名单）瞬间完成，一闪而过的遮罩比不动更晃眼。
    private static readonly TimeSpan BusyOverlayDelay = TimeSpan.FromMilliseconds(400);
    private CancellationTokenSource? _busyCts;

    [ObservableProperty] private bool _showBusyOverlay;
    [ObservableProperty] private string _busyTitle = string.Empty;
    [ObservableProperty] private string _busyDetail = string.Empty;

    /// <summary>开始一段可能较长的操作；务必在 finally 里配 <see cref="EndBusy"/>。</summary>
    public void BeginBusy(string title, string detail)
    {
        BusyTitle = title;
        BusyDetail = detail;
        IsBusy = true;

        _busyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _busyCts = cts;

        _ = Task.Delay(BusyOverlayDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested) ShowBusyOverlay = true;
            });
        }, TaskScheduler.Default);
    }

    public void EndBusy()
    {
        _busyCts?.Cancel();
        _busyCts = null;
        ShowBusyOverlay = false;
        IsBusy = false;
    }

    [ObservableProperty] private string _rosterHint = string.Empty;
    [ObservableProperty] private string _importSource = "尚未导入名单";

    // ----------------------------- 计算属性 -----------------------------

    public bool IsIdle => !IsBusy;
    /// <summary>「导出配置」是否可用。考场是快照不是日常布局，导回去只会被夹坏，所以直接禁用。</summary>
    public bool CanExportConfig => IsIdle && !IsExamMode;
    public bool IsTeacherView => !IsStudentView;
    public bool PodiumOnTop => IsStudentView;
    public bool PodiumOnBottom => !IsStudentView;
    public bool HasStudents => _students.Count > 0;
    public bool CanArrange => HasStudents && !IsBusy;
    public bool CanExport => HasStudents && !IsBusy;
    public bool IsChartEmpty => _students.Count == 0;
    public bool HasRosterHint => RosterHint.Length > 0;
    public bool IsRosterEmpty => FilteredStudents.Count == 0;
    public bool ShowGenderLegend => MarkFemales;
    public string RosterEmptyText => _students.Count == 0 ? "还没有名单" : "没有匹配的学生";

    public int StudentCount => _students.Count;
    public int MaleCount => _students.Count(s => s.Model.Gender == Gender.Male);
    public int FemaleCount => _students.Count(s => s.Model.Gender == Gender.Female);
    public int AssignedCount => _assignments.Count;
    public int UnassignedCount => _students.Count(s => !s.IsAssigned);
    public int SeatCount => CurrentLayout.SeatCount;

    public string RosterStats => $"男 {MaleCount} · 女 {FemaleCount} · 未排 {UnassignedCount}";
    public string SeatStats => $"已排 {AssignedCount} / {SeatCount} 座";

    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(HeadTeacher)) parts.Add($"班主任：{HeadTeacher.Trim()}");
            parts.Add($"人数：{StudentCount}");
            if (!string.IsNullOrWhiteSpace(Monitor)) parts.Add($"班长：{Monitor.Trim()}");
            return string.Join("　　", parts);
        }
    }

    public string GroupCountLabel => $"{Math.Clamp((int)Math.Round(GroupCount), 1, 8)}";
    public string RowCountLabel => $"{Math.Clamp((int)Math.Round(RowCount), 1, 10)}";
    public string ColumnCountLabel => $"{Math.Clamp((int)Math.Round(ColumnCount), 1, 4)}";
    public string CellSizeLabel => $"{CellSizeValue} × {CellSizeValue}px";
    public string PodiumGapLabel => $"{(int)Math.Round(PodiumGap)}px";
    public string NameFontSizeLabel => $"{(int)Math.Round(NameFontSize)}px";

    public FontWeight TitleWeight => BoldTitle ? FontWeight.Bold : FontWeight.Normal;

    public string SelectedSeatLabel =>
        SelectedSeat is null ? "未选座位" : $"已选：{SelectedSeat.PositionLabel}";

    public bool CanAssign => SelectedStudent is not null && SelectedSeat is not null;
    public bool CanClearSeat => SelectedSeat is not null;

    public IBrush SeatBackgroundBrush => _seatBackgroundBrush ??= new SolidColorBrush(SeatBackgroundColor);
    public IBrush EmptySeatBrush => _emptySeatBrush ??= new SolidColorBrush(EmptySeatColor);
    public IBrush SeatTextBrush => _seatTextBrush ??= new SolidColorBrush(SeatTextColor);
    public IBrush SeatBorderBrush => _seatBorderBrush ??= new SolidColorBrush(SeatBorderColor);
    public IBrush PodiumBackgroundBrush => _podiumBackgroundBrush ??= new SolidColorBrush(PodiumBackgroundColor);
    public IBrush PodiumTextBrush => _podiumTextBrush ??= new SolidColorBrush(PodiumTextColor);
    public IBrush GroupLabelBrush => _groupLabelBrush ??= new SolidColorBrush(GroupLabelColor);
    public IBrush FemaleMarkBrush => _femaleMarkBrush ??= new SolidColorBrush(FemaleMarkColor);

    private SeatChartLayout CurrentLayout => IsExamMode && _examPlan is not null
        ? _examPlan.Layout
        : new SeatChartLayout(
            Math.Clamp((int)Math.Round(GroupCount), 1, 8),
            Math.Clamp((int)Math.Round(RowCount), 1, 10),
            Math.Clamp((int)Math.Round(ColumnCount), 1, 4),
            Canvas);

    private ViewMode CurrentView => IsStudentView ? ViewMode.Student : ViewMode.Teacher;

    private int CellSizeValue => Math.Max(40, (int)Math.Round(SeatChartSettings.BaseCellSize * (CellScale / 100.0)));

    private int FontSizeValue => Math.Clamp((int)Math.Round(NameFontSize), 10, 36);

    // ----------------------------- 设置变更 -----------------------------

    /// <summary>
    /// 滑块按下时进入交互编辑。压测（P1-C，2026-10-27）实测 130 人名单 / 96 座（96 人入座）：
    /// 尺寸刷新 p50 2.2ms / p95 3.1ms，BuildGrid p50 2.2ms / p95 2.8ms，
    /// 远低于 8ms 阈值——拖动中实时跟随的代价可以接受，不再悬置到松手。
    /// 中间刷新按输入批合并（同批多次改动只刷一次），收尾时再做一次确定性刷新。
    /// </summary>
    public void BeginInteractiveLayoutChange()
    {
        _interactiveLayoutEdit = true;
        _structureDirty = false;
        _scaleDirty = false;
        _layoutRefreshQueued = false;
    }

    public void EndInteractiveLayoutChange()
    {
        if (!_interactiveLayoutEdit) return;
        _interactiveLayoutEdit = false;
        _layoutRefreshQueued = false; // 待执行的合并刷新由下面的收尾刷新覆盖

        if (_structureDirty)
        {
            _structureDirty = false;
            _scaleDirty = false;
            BuildGrid();
            NotifyCounts();
            return;
        }

        if (_scaleDirty)
        {
            _scaleDirty = false;
            RefreshSeatVisuals();
            UpdatePodium();
        }
    }

    /// <summary>拖动中的合并刷新：同一输入批内的多次改动只排一次后台优先级刷新。</summary>
    private void QueueInteractiveRefresh(bool structure)
    {
        if (!_interactiveLayoutEdit) return;
        if (structure) _structureDirty = true;
        else _scaleDirty = true;
        if (_layoutRefreshQueued) return;

        _layoutRefreshQueued = true;
        Dispatcher.UIThread.Post(ApplyInteractiveRefresh, DispatcherPriority.Background);
    }

    private void ApplyInteractiveRefresh()
    {
        _layoutRefreshQueued = false;
        if (!_interactiveLayoutEdit) return; // 松手后的收尾刷新已覆盖

        if (_structureDirty)
        {
            _structureDirty = false;
            _scaleDirty = false;
            BuildGrid();
            NotifyCounts();
            return;
        }

        if (_scaleDirty)
        {
            _scaleDirty = false;
            RefreshSeatVisuals();
            UpdatePodium();
        }
    }

    // 派生属性的通知放在 _suppressRebuild 判断之前：导入配置时只挂起重建，
    // 视角按钮与滑块旁的数值仍要刷新，否则界面会停在导入前的旧值。
    partial void OnGroupCountChanged(double value)
    {
        OnPropertyChanged(nameof(GroupCountLabel));
        if (_suppressRebuild) return;
        if (_interactiveLayoutEdit)
        {
            QueueInteractiveRefresh(structure: true);
            return;
        }
        BuildGrid();
        NotifyCounts();
    }

    partial void OnRowCountChanged(double value)
    {
        OnPropertyChanged(nameof(RowCountLabel));
        if (_suppressRebuild) return;
        if (_interactiveLayoutEdit)
        {
            QueueInteractiveRefresh(structure: true);
            return;
        }
        BuildGrid();
        NotifyCounts();
    }

    partial void OnColumnCountChanged(double value)
    {
        OnPropertyChanged(nameof(ColumnCountLabel));
        if (_suppressRebuild) return;
        if (_interactiveLayoutEdit)
        {
            QueueInteractiveRefresh(structure: true);
            return;
        }
        BuildGrid();
        NotifyCounts();
    }

    partial void OnIsStudentViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTeacherView));
        OnPropertyChanged(nameof(PodiumOnTop));
        OnPropertyChanged(nameof(PodiumOnBottom));
        if (_suppressRebuild) return;
        BuildGrid();
    }

    partial void OnCellScaleChanged(double value)
    {
        OnPropertyChanged(nameof(CellSizeLabel));
        if (_suppressRebuild) return;
        if (_interactiveLayoutEdit)
        {
            QueueInteractiveRefresh(structure: false);
            return;
        }
        RefreshSeatVisuals();
        UpdatePodium();
    }

    partial void OnPodiumGapChanged(double value)
    {
        OnPropertyChanged(nameof(PodiumGapLabel));
        UpdatePodium();
    }

    partial void OnNameFontSizeChanged(double value)
    {
        OnPropertyChanged(nameof(NameFontSizeLabel));
        RefreshSeatVisuals();
    }

    partial void OnBoldNamesChanged(bool value) => RefreshSeatVisuals();

    partial void OnHideEmptySeatsChanged(bool value) => RefreshSeatVisuals();

    partial void OnMarkFemalesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGenderLegend));
        RefreshSeatVisuals();
    }

    partial void OnSpaceTwoCharNamesChanged(bool value) => RefreshSeatVisuals();

    partial void OnShowGroupRowHintsChanged(bool value)
    {
        if (_suppressRebuild) return;
        BuildGrid();
        UpdatePodium();
    }

    partial void OnBoldTitleChanged(bool value) => OnPropertyChanged(nameof(TitleWeight));

    partial void OnHeadTeacherChanged(string value) => OnPropertyChanged(nameof(InfoLine));

    partial void OnMonitorChanged(string value) => OnPropertyChanged(nameof(InfoLine));

    partial void OnSeatBackgroundColorChanged(Color value) => ColorChanged();

    partial void OnEmptySeatColorChanged(Color value) => ColorChanged();

    partial void OnSeatTextColorChanged(Color value) => ColorChanged();

    partial void OnSeatBorderColorChanged(Color value) => ColorChanged();

    partial void OnPodiumBackgroundColorChanged(Color value) => ColorChanged();

    partial void OnPodiumTextColorChanged(Color value) => ColorChanged();

    partial void OnGroupLabelColorChanged(Color value) => ColorChanged();

    partial void OnRowLabelColorChanged(Color value) => ColorChanged();

    partial void OnFemaleMarkColorChanged(Color value) => ColorChanged();

    partial void OnSearchTextChanged(string value) => RefreshStudents();

    partial void OnSelectedStudentChanged(StudentViewModel? value)
    {
        if (value is null)
        {
            EditName = string.Empty;
            EditNumber = string.Empty;
            EditGenderIndex = 0;
            EditHeight = string.Empty;
            EditVision = string.Empty;
            EditNotes = string.Empty;
        }
        else
        {
            EditName = value.Model.Name;
            EditNumber = value.Model.Number;
            EditGenderIndex = value.Model.Gender switch
            {
                Gender.Male => 1,
                Gender.Female => 2,
                _ => 0,
            };
            EditHeight = value.Model.Height?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty;
            EditVision = value.Model.Vision?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
            EditNotes = value.Model.Notes;
        }

        OnPropertyChanged(nameof(CanAssign));
        OnPropertyChanged(nameof(HasSelectedStudent));
        OnPropertyChanged(nameof(EditPanelTitle));
    }

    partial void OnSelectedSeatChanged(SeatCellViewModel? value)
    {
        foreach (var seat in _seats.Values) seat.SetSelected(seat == value);
        OnPropertyChanged(nameof(SelectedSeatLabel));
        OnPropertyChanged(nameof(CanAssign));
        OnPropertyChanged(nameof(CanClearSeat));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanArrange));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanExportConfig));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    partial void OnRosterHintChanged(string value) => OnPropertyChanged(nameof(HasRosterHint));

    private void ColorChanged()
    {
        _seatBackgroundBrush = null;
        _emptySeatBrush = null;
        _seatTextBrush = null;
        _seatBorderBrush = null;
        _podiumBackgroundBrush = null;
        _podiumTextBrush = null;
        _groupLabelBrush = null;
        _femaleMarkBrush = null;

        OnPropertyChanged(nameof(SeatBackgroundBrush));
        OnPropertyChanged(nameof(EmptySeatBrush));
        OnPropertyChanged(nameof(SeatTextBrush));
        OnPropertyChanged(nameof(SeatBorderBrush));
        OnPropertyChanged(nameof(PodiumBackgroundBrush));
        OnPropertyChanged(nameof(PodiumTextBrush));
        OnPropertyChanged(nameof(GroupLabelBrush));
        OnPropertyChanged(nameof(FemaleMarkBrush));
        RefreshSeatVisuals();
    }

    // ----------------------------- 构建网格 -----------------------------

    private void BuildGrid()
    {
        SelectedSeat = null;

        var layout = CurrentLayout;
        var previous = new Dictionary<SeatPosition, StudentViewModel>(_assignments);
        var dropped = 0;

        _assignments.Clear();
        foreach (var (position, student) in previous)
        {
            if (layout.Contains(position)) _assignments[position] = student;
            else dropped++;
        }

        // 布局缩小会让越界座位上的学生退回未排：必须报数，不能静默丢人。
        // 拖滑块时状态栏会被刷屏，所以只在非交互编辑时报。
        if (dropped > 0 && !_interactiveLayoutEdit)
            SetStatus($"布局缩小后有 {dropped} 名学生退回未排。", true);

        _seats.Clear();
        Groups.Clear();

        // 摆放顺序与导出共用 SeatChartComposer：学生视角是教师视角整体旋转 180°，组内列序也要反转。
        var columnNumbers = SeatChartComposer.ColumnOrder(layout, CurrentView);
        var rowNumbers = SeatChartComposer.RowOrder(layout, CurrentView);

        foreach (var group in SeatChartComposer.GroupOrder(layout, CurrentView))
        {
            var rows = new List<RowViewModel>();

            foreach (var row in rowNumbers)
            {
                var seats = new List<SeatCellViewModel>();
                foreach (var column in columnNumbers)
                {
                    var seat = new SeatCellViewModel(new SeatPosition(group, row, column));
                    _seats[seat.Position] = seat;
                    seats.Add(seat);
                }

                rows.Add(new RowViewModel(row + 1, seats));
            }

            var columnHandles = new List<ColumnHandleViewModel>();
            foreach (var column in columnNumbers)
                columnHandles.Add(new ColumnHandleViewModel(group, column));

            Groups.Add(new GroupViewModel(group + 1, $"第{group + 1}组", rows, columnHandles));
        }

        OnPropertyChanged(nameof(SeatCount));
        OnPropertyChanged(nameof(PodiumOnTop));
        OnPropertyChanged(nameof(PodiumOnBottom));
        RefreshSeatVisuals();
        UpdatePodium();
        NotifyCounts();
    }

    private void RefreshSeatVisuals()
    {
        var size = CellSizeValue;
        var fontSize = (double)FontSizeValue;
        var seatBackground = SeatBackgroundBrush;
        var emptyBackground = EmptySeatBrush;
        var nameColor = SeatTextBrush;
        var femaleColor = MarkFemales ? FemaleMarkBrush : SeatTextBrush;
        var border = SeatBorderBrush;
        var layout = CurrentLayout;
        var offsets = SeatChartGeometry.Measure(layout, ToSettings(), CurrentView);

        foreach (var seat in _seats.Values)
        {
            _assignments.TryGetValue(seat.Position, out var student);
            var name = student is null ? string.Empty : SeatChartComposer.DisplayName(student.Model.Name, SpaceTwoCharNames);
            var aisle = layout.Surface.IsDisabled(seat.Position);

            seat.Update(
                name,
                student is null,
                student?.Model.Gender == Gender.Female,
                seat == SelectedSeat,
                student is not null && Constraints.IsPinned(student.Id),
                aisle,
                size,
                fontSize,
                BoldNames,
                seatBackground,
                emptyBackground,
                nameColor,
                femaleColor,
                border,
                HideEmptySeats);

            // 列距由 SeatChartGeometry 统一给出（含逐边界过道），所以用 Margin 而不是 StackPanel.Spacing。
            var columnIndex = DisplayIndex(offsets.Column, seat.Position.Column);
            seat.Margin = new Thickness(0, 0,
                columnIndex >= 0 ? offsets.Column[columnIndex + 1] - offsets.Column[columnIndex] - size : 0, 0);
        }

        ApplyGroupAndRowSpacing(offsets);

        var reverse = new Dictionary<Guid, SeatPosition>();
        foreach (var (position, student) in _assignments) reverse[student.Id] = position;

        foreach (var student in _students)
        {
            student.SetSeat(reverse.TryGetValue(student.Id, out var position) ? position : null);
        }

        var groupIndex = 0;
        foreach (var group in Groups)
        {
            group.LabelBrush = GroupLabelBrush;
            group.ShowHints = ShowGroupRowHints;
            group.LabelMargin = ShowGroupRowHints && groupIndex == 0
                ? new Thickness(44, 0, 0, 5)
                : new Thickness(0, 0, 0, 5);
            group.ShowRowSpacer = groupIndex == 0;
            foreach (var row in group.Rows)
                row.ShowHandle = groupIndex == 0;
            foreach (var handle in group.ColumnHandles)
                handle.Width = size;

            groupIndex++;
        }

        NotifyCounts();
    }

    private void UpdatePodium()
    {
        var offsets = SeatChartGeometry.Measure(CurrentLayout, ToSettings(), CurrentView);
        var contentWidth = offsets.GridWidth + 44; // 行首把手列固定占位

        PodiumWidth = Math.Min(contentWidth * ToSettings().PodiumWidthPercent / 100.0, 360);
        PodiumTopMargin = new Thickness(0, 0, 0, PodiumGap);
        PodiumBottomMargin = new Thickness(0, PodiumGap, 0, 0);
        OnPropertyChanged(nameof(PodiumOnTop));
        OnPropertyChanged(nameof(ChartContentWidth));
    }

    // ----------------------------- 名单 -----------------------------

    public void LoadRoster(RosterImportResult result)
    {
        // 导入新名单是「重来」而不是「编辑」：撤销回上一份名单没有意义，
        // 而且快照里存的学生 Id 与当前约束全都对不上了。整段挂起登记、末尾清栈，
        // 比留一堆跨名单的步骤更安全——内部的 AutoArrange 也不该单独成为一步。
        _suspendUndo = true;
        _suppressRebuild = true;
        _students.Clear();
        _assignments.Clear();
        SelectedStudent = null;
        SelectedSeat = null;
        _suppressRebuild = false;

        foreach (var student in result.Students)
            _students.Add(new StudentViewModel(student));

        // 名单整体换掉：旧约束指向的人已经不在了，一并清空。
        Constraints = new SeatConstraintsViewModel(_students);
        OnPropertyChanged(nameof(Constraints));

        ImportSource = result.SourceName;
        RosterHint = string.Join("；", result.Warnings);

        if (string.IsNullOrWhiteSpace(Title) || Title == "班级座位表")
            Title = DeriveTitle(result.SourceName);

        BuildGrid();
        AutoArrange();
        RefreshStudents();
        NotifyCounts();

        SetStatus(RosterHint.Length > 0
            ? $"已识别 {_students.Count} 人 · {RosterHint}"
            : $"已识别 {_students.Count} 人 · 男 {MaleCount} / 女 {FemaleCount}");

        _suspendUndo = false;
        ClearUndo();
    }

    private static string DeriveTitle(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (var suffix in new[] { "学生名单", "花名册", "名单", "座位表" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                name = name[..^suffix.Length];
        }

        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "班级座位表" : $"{name}座位表";
    }

    private void RefreshStudents()
    {
        var selected = SelectedStudent;
        var keyword = SearchText.Trim().ToLowerInvariant();

        var matches = new List<StudentViewModel>(_students.Count);
        foreach (var student in _students)
        {
            if (keyword.Length == 0 || student.SearchKey.Contains(keyword))
                matches.Add(student);
        }

        FilteredStudents = matches;

        if (selected is not null)
            SelectedStudent = matches.Contains(selected) ? selected : null;

        OnPropertyChanged(nameof(IsRosterEmpty));
        OnPropertyChanged(nameof(RosterEmptyText));
    }

    public void AutoArrange() => RecordChange("自动排座", AutoArrangeCore);

    private void AutoArrangeCore()
    {
        if (_students.Count == 0)
        {
            SetStatus("先导入一份名单，再自动排座。", true);
            return;
        }

        var mode = (ArrangeMode)Math.Clamp(ArrangeModeIndex, 0, ArrangeModes.Count - 1);
        var models = _students.Select(s => s.Model).ToList();
        var layout = CurrentLayout;

        // 布局可能刚缩过：先把指向不存在座位的锁清掉，否则引擎会拿幽灵锁去排。
        Constraints.DropInvalidPins(layout, _students.Select(s => s.Id).ToHashSet());

        var built = Constraints.Build(layout);
        var constraints = new ArrangeConstraints
        {
            Pins = built.Pins,
            Mutex = built.Mutex,
            Deskmates = built.Deskmates,
            AvoidDeskmates = built.AvoidDeskmates,
            Zones = built.Zones,
            MentorEnabled = MentorEnabled,
            MentorSeat = MentorSeatIndex == 1 ? MentorSeat.FrontRow : MentorSeat.Center,
        };
        var result = SeatingEngine.Arrange(models, layout, mode, constraints);
        var byId = _students.ToDictionary(s => s.Model.Id);

        _assignments.Clear();
        foreach (var (position, model) in result.Assignments)
        {
            if (byId.TryGetValue(model.Id, out var student))
                _assignments[position] = student;
        }

        Constraints.SetIssues(result.Issues.Select(i => i.Message));
        RefreshSeatVisuals();

        var summary = $"已按「{ArrangeModes[ArrangeModeIndex]}」排座：{AssignedCount} 人 / {SeatCount} 座";
        SetStatus(result.Issues.Count == 0
            ? summary
            : $"{summary} · {result.Issues[0].Message}" +
              (result.Issues.Count > 1 ? $"（另有 {result.Issues.Count - 1} 条）" : string.Empty),
            error: result.Issues.Count > 0);
    }

    /// <summary>
    /// 显示序里某个物理列/排/组的下标。偏移数组按显示序生成，取值前要先换回来
    /// （学生视角下三者都是倒的）。
    /// </summary>
    private int DisplayIndex(float[] offsets, int physical)
    {
        var count = offsets.Length - 1;
        if (physical < 0 || physical >= count) return -1;
        return CurrentView == ViewMode.Student ? count - 1 - physical : physical;
    }

    /// <summary>把组距与排距写进 GroupViewModel / RowViewModel 的 Margin。</summary>
    private void ApplyGroupAndRowSpacing(SeatChartGeometry.Offsets offsets)
    {
        var groupOrder = SeatChartComposer.GroupOrder(CurrentLayout, CurrentView);
        var rowOrder = SeatChartComposer.RowOrder(CurrentLayout, CurrentView);

        for (var i = 0; i < Groups.Count; i++)
        {
            var gap = i + 1 < offsets.Group.Length
                ? offsets.Group[i + 1] - offsets.Group[i] - offsets.GroupWidth
                : 0;
            Groups[i].Margin = new Thickness(0, 0, gap, 0);

            var rows = Groups[i].Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                var rowGap = r + 1 < offsets.Row.Length
                    ? offsets.Row[r + 1] - offsets.Row[r] - offsets.Cell
                    : 0;
                rows[r].Margin = new Thickness(0, 0, 0, rowGap);
            }
        }
    }

    /// <summary>切换某格是否为过道。若该格上有人，学生退回未排并报数。</summary>
    public void ToggleAisle(SeatPosition position)    {
        var canvas = Canvas.Toggle(position);
        ApplyCanvas(canvas, $"已把 {position} 设为{(canvas.IsDisabled(position) ? "过道" : "座位")}。");
    }

    /// <summary>应用一份新画布：裁掉越界格、把受影响的学生退回未排、重建网格。</summary>
    public void ApplyCanvas(SeatChartCanvas canvas, string message) =>
        RecordChange("教室画布", () => ApplyCanvasCore(canvas, message));

    private void ApplyCanvasCore(SeatChartCanvas canvas, string message)
    {
        var layout = CurrentLayout with { Canvas = canvas };
        Canvas = canvas.Normalize(layout);

        var unseated = 0;
        foreach (var (position, student) in _assignments.ToList())
        {
            if (CurrentLayout.IsUsable(position)) continue;
            _assignments.Remove(position);
            unseated++;
        }

        BuildGrid();
        var suffix = unseated > 0 ? $" {unseated} 名学生退回未排。" : string.Empty;
        SetStatus(message + suffix, unseated > 0);
        OnPropertyChanged(nameof(CanvasSummary));
    }

    public void ClearCanvas() => ApplyCanvas(SeatChartCanvas.Empty, "已清空画布。");

    // ----------------------------- 末尾增删 -----------------------------

    public void AppendRow() => ResizeLayout(CurrentLayout.AppendRow(), "已在末尾追加一排。");

    public void AppendColumn() => ResizeLayout(CurrentLayout.AppendColumn(), "已在末尾追加一列。");

    public void AppendGroup() => ResizeLayout(CurrentLayout.AppendGroup(), "已在末尾追加一组。");

    public void RemoveLastRow() => RemoveTail(CurrentLayout.RemoveLastRow(), "末排");

    public void RemoveLastColumn() => RemoveTail(CurrentLayout.RemoveLastColumn(), "末列");

    public void RemoveLastGroup() => RemoveTail(CurrentLayout.RemoveLastGroup(), "末组");

    /// <summary>删末尾前先算清楚会挤掉几个人，供确认弹窗显示。</summary>
    public int CountSeatsInTail(string kind)
    {
        var layout = CurrentLayout;
        return kind switch
        {
            "row" => _assignments.Keys.Count(p => p.Row == layout.Rows - 1),
            "column" => _assignments.Keys.Count(p => p.Column == layout.Columns - 1),
            "group" => _assignments.Keys.Count(p => p.Group == layout.Groups - 1),
            _ => 0,
        };
    }

    private void RemoveTail((SeatChartLayout Layout, IReadOnlyList<SeatPosition> Removed) result, string label)
    {
        if (result.Removed.Count == 0)
        {
            SetStatus($"只剩一{label}，不能再删。", true);
            return;
        }

        var unseated = 0;
        foreach (var position in result.Removed)
        {
            if (!_assignments.Remove(position)) continue;
            unseated++;
        }

        ResizeLayout(result.Layout, unseated > 0
            ? $"已删除{label}，{unseated} 名学生退回未排。"
            : $"已删除{label}。", unseated > 0);
    }

    private void ResizeLayout(SeatChartLayout layout, string message, bool warn = false) =>
        RecordChange("调整行列", () => ResizeLayoutCore(layout, message, warn));

    private void ResizeLayoutCore(SeatChartLayout layout, string message, bool warn = false)
    {
        _suppressRebuild = true;
        GroupCount = layout.Groups;
        RowCount = layout.Rows;
        ColumnCount = layout.Columns;
        Canvas = layout.Surface;
        _suppressRebuild = false;

        BuildGrid();
        NotifyCounts();
        SetStatus(message, warn);
    }

    // ----------------------------- 镜像 -----------------------------

    public void MirrorColumns()
    {
        if (_assignments.Count == 0)
        {
            SetStatus("还没有已安排的座位。", true);
            return;
        }

        if (RefuseIfPinned(_assignments.Values, "左右镜像")) return;

        RecordChange("左右镜像", () =>
        {
            var mirrored = SeatChartComposer.MirrorColumns(_assignments, CurrentLayout, out var blocked);
            _assignments.Clear();
            foreach (var (position, student) in mirrored) _assignments[position] = student;

            RefreshSeatVisuals();
            var note = blocked > 0 ? $"（{blocked} 人因目标格是过道未移动）" : string.Empty;
            SetStatus($"已左右镜像{note}");
        });
    }

    public void MirrorGroups()
    {
        if (_assignments.Count == 0)
        {
            SetStatus("还没有已安排的座位。", true);
            return;
        }

        if (RefuseIfPinned(_assignments.Values, "组序镜像")) return;

        RecordChange("组序镜像", () =>
        {
            var mirrored = SeatChartComposer.MirrorGroups(_assignments, CurrentLayout, out var blocked);
            _assignments.Clear();
            foreach (var (position, student) in mirrored) _assignments[position] = student;

            RefreshSeatVisuals();
            var note = blocked > 0 ? $"（{blocked} 人因目标格是过道未移动）" : string.Empty;
            SetStatus($"已组序镜像{note}");
        });
    }

    // ----------------------------- 异形教室预设 -----------------------------

    public bool CanApplyUShape => CurrentLayout.Groups >= 3;
    public bool CanApplyIslands => CurrentLayout.Groups >= 4;
    public bool CanApplyStaggered => CurrentLayout.Columns >= 2;
    public bool CanApplyFrontFilled => CurrentLayout.Rows > 2;

    public void ApplyUShape(bool openTowardPodium)
    {
        var canvas = SeatCanvasPresets.UShape(CurrentLayout, openTowardPodium);
        if (canvas is null)
        {
            SetStatus("U 型至少需要 3 组。", true);
            return;
        }

        ApplyCanvas(canvas, "已应用 U 型布局。");
    }

    public void ApplyIslands()
    {
        var canvas = SeatCanvasPresets.Islands(CurrentLayout);
        if (canvas is null)
        {
            SetStatus("多岛布局至少需要 4 组。", true);
            return;
        }

        ApplyCanvas(canvas, "已应用多岛布局。");
    }

    public void ApplyStaggered()
    {
        var canvas = SeatCanvasPresets.StaggeredRows(CurrentLayout);
        if (canvas is null)
        {
            SetStatus("长短排至少需要 2 列。", true);
            return;
        }

        ApplyCanvas(canvas, "已应用长短排布局。");
    }

    public void ApplyFrontFilled()
    {
        var canvas = SeatCanvasPresets.FrontFilled(CurrentLayout);
        if (canvas is null)
        {
            SetStatus("报告厅布局至少需要 3 排。", true);
            return;
        }

        ApplyCanvas(canvas, "已应用报告厅布局（后两排不坐人）。");
    }

    // ----------------------------- 区域限制 -----------------------------

    /// <summary>给当前选中的学生设置区域限制（允许 / 禁止的排区间）。</summary>
    public void SetSelectedStudentZone(int? frontRows, int? avoidBackRows)
    {
        if (SelectedStudent is null)
        {
            SetStatus("先在名单里选中一名学生。", true);
            return;
        }

        var layout = CurrentLayout;
        var allowed = new List<SeatRect>();
        var denied = new List<SeatRect>();

        if (frontRows is int n && n > 0)
            allowed.Add(SeatRect.RowBand(layout, 0, Math.Min(n - 1, layout.Rows - 1)));

        if (avoidBackRows is int m && m > 0)
            denied.Add(SeatRect.RowBand(layout, Math.Max(0, layout.Rows - m), layout.Rows - 1));

        var zone = new StudentZone(allowed, denied);
        var student = SelectedStudent;
        RecordChange("区域限制", () =>
        {
            Constraints.SetZone(student, zone);
            RefreshSeatVisuals();

            SetStatus(zone.IsEmpty
                ? $"已清除 {student.Name} 的区域限制。"
                : $"已设置 {student.Name} 的区域限制。");
        });
    }

    /// <summary>把区域限制写进排座引擎：`Constraints.Build` 已经带 Zones，这里只做提示。</summary>
    public bool HasZones => Constraints.Zones.Count > 0;

    // ----------------------------- 搬运量 -----------------------------

    /// <summary>当前布局 → 考场布局的桌椅搬运计划，供考场弹窗展示。</summary>
    public DeskMovingPlan BuildMovingPlan(ExamLayoutOptions options)
    {
        var examPlan = ExamPlanner.Build(_students.Select(s => s.Model).ToList(), options);
        var dailyIsDoubleDesk = CurrentLayout.Columns >= 2;
        return DeskMovingPlanCalculator.Build(CurrentLayout, examPlan.Layout, dailyIsDoubleDesk);
    }

    public void SetRowCorridor(int row, double cells) =>
        ApplyCanvas(Canvas.WithRowCorridor(row, (float)cells),
            cells <= 0 ? $"已去掉第 {row + 1} 排之后的过道。" : $"第 {row + 1} 排之后加了过道。");

    public void SetColumnCorridor(int column, double cells) =>
        ApplyCanvas(Canvas.WithColumnCorridor(column, (float)cells),
            cells <= 0 ? $"已去掉第 {column + 1} 列之后的过道。" : $"第 {column + 1} 列之后加了过道。");

    public void SetGroupCorridor(int group, double cells) =>
        ApplyCanvas(Canvas.WithGroupCorridor(group, (float)cells),
            cells <= 0 ? $"已去掉第 {group + 1} 组之后的过道。" : $"第 {group + 1} 组之后加了过道。");

    /// <summary>按姓名或拼音找人；返回最多若干条候选，供快速换座浮窗使用。</summary>
    public IReadOnlyList<StudentViewModel> FindStudents(string keyword, int limit = 8)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0) return Array.Empty<StudentViewModel>();

        var lowered = text.ToLowerInvariant();
        return _students
            .Where(s => s.SearchKey.Contains(lowered) || s.MatchesPinyin(text))
            .Take(limit)
            .ToList();
    }

    /// <summary>把某座位上的学生与按关键字找到的学生对调。找不到或锁定则拒绝。</summary>
    public void QuickSwapByName(SeatCellViewModel source, string keyword)
    {
        if (!_assignments.TryGetValue(source.Position, out var current))
        {
            SetStatus("这个座位是空的，双击有人的座位可以快速换人。", true);
            return;
        }

        var candidates = FindStudents(keyword);
        if (candidates.Count == 0)
        {
            SetStatus($"没有找到「{keyword}」。", true);
            return;
        }

        if (candidates.Count > 1)
        {
            SetStatus($"找到 {candidates.Count} 人，请再输入几个字缩小范围。", true);
            return;
        }

        var target = candidates[0];
        if (target.Id == current.Id)
        {
            SetStatus($"{current.Name} 已经坐在这里了。");
            return;
        }

        var targetSeat = _seats.Values.FirstOrDefault(s =>
            _assignments.TryGetValue(s.Position, out var occupant) && occupant.Id == target.Id);

        if (targetSeat is null)
        {
            SetStatus($"{target.Name} 还没有座位，可以先用「放入所选学生」。", true);
            return;
        }

        if (Constraints.IsPinned(current.Id) || Constraints.IsPinned(target.Id))
        {
            SetStatus("其中一位的座位已锁定，先解除锁定再换。", true);
            return;
        }

        MoveSeatStudent(source, targetSeat);
    }

    /// <summary>切换当前选中座位的锁定状态。锁的是「学生-座位绑定」。</summary>
    public void ToggleSelectedSeatPin()
    {
        if (SelectedSeat is null)
        {
            SetStatus("先选中一个座位，再锁定。", true);
            return;
        }

        if (!_assignments.TryGetValue(SelectedSeat.Position, out var student))
        {
            SetStatus("空座位不用锁定；锁定的是学生，重新排座时这位同学不动。", true);
            return;
        }

        var pinned = Constraints.IsPinned(student.Id);
        var position = SelectedSeat.Position;
        RecordChange(pinned ? "解除锁定" : "锁定座位", () =>
        {
            Constraints.TogglePin(student, position);
            RefreshSeatVisuals();
            SetStatus(pinned
                ? $"已解除 {student.Name} 的座位锁定。"
                : $"已锁定 {student.Name} 的座位，重新排座时不会移动。");
        });
    }

    /// <summary>整班平移前检查：任一被移动的学生处于锁定状态就整体拒绝。</summary>
    private bool RefuseIfPinned(IEnumerable<StudentViewModel> students, string action)
    {
        var pinned = students.FirstOrDefault(s => Constraints.IsPinned(s.Id));
        if (pinned is null) return false;

        SetStatus($"{pinned.Name} 的座位已锁定，先解除锁定再{action}。", true);
        return true;
    }

    public void ClearSeats() => RecordChange("清空座位", ClearSeatsCore);

    private void ClearSeatsCore()
    {
        var pinned = Constraints.Pins.Count;
        var removed = 0;
        foreach (var (position, student) in _assignments.ToList())
        {
            if (Constraints.IsPinned(student.Id)) continue;
            _assignments.Remove(position);
            removed++;
        }

        RefreshSeatVisuals();
        SetStatus(pinned == 0
            ? $"已清空 {removed} 个座位，可以重新排座或手动放置。"
            : $"已清空 {removed} 个未锁定座位，保留 {pinned} 个锁定座位。");
    }

    public void SelectSeat(SeatCellViewModel seat)
    {
        SelectedSeat = SelectedSeat == seat ? null : seat;
    }

    public void AssignSelectedStudent()
    {
        if (SelectedStudent is null || SelectedSeat is null)
        {
            SetStatus("先选中一个座位，再从左侧名单选中学生。", true);
            return;
        }

        var student = SelectedStudent;
        var target = SelectedSeat;

        // 锁定的学生不能被「放入」覆盖，目标座上的人若锁定也不能被挤走。
        if (Constraints.IsPinned(student.Id))
        {
            SetStatus($"{student.Name} 的座位已锁定，先解除锁定再移动。", true);
            return;
        }

        if (_assignments.TryGetValue(target.Position, out var occupant) && Constraints.IsPinned(occupant.Id))
        {
            SetStatus($"{occupant.Name} 的座位已锁定，先解除锁定再移动。", true);
            return;
        }

        RecordChange("放入学生", () =>
        {
            foreach (var pair in _assignments.Where(kv => kv.Value == student).ToList())
                _assignments.Remove(pair.Key);

            _assignments[target.Position] = student;
            RefreshSeatVisuals();
            SetStatus($"已把 {student.Name} 放到 {target.PositionLabel}。");
        });
    }

    /// <summary>拖拽落座：目标有人则交换，目标为空则移动。</summary>
    public void MoveSeatStudent(SeatCellViewModel source, SeatCellViewModel target)
    {
        if (source == target) return;
        if (!_assignments.TryGetValue(source.Position, out var moving)) return;

        var involved = new List<StudentViewModel> { moving };
        if (_assignments.TryGetValue(target.Position, out var targetOccupant)) involved.Add(targetOccupant);
        if (RefuseIfPinned(involved, "移动")) return;

        RecordChange("移动座位", () =>
        {
            if (_assignments.TryGetValue(target.Position, out var occupying))
            {
                _assignments[target.Position] = moving;
                _assignments[source.Position] = occupying;
                RefreshSeatVisuals();
                SetStatus($"已把 {moving.Name} 与 {occupying.Name} 交换座位。");
            }
            else
            {
                _assignments.Remove(source.Position);
                _assignments[target.Position] = moving;
                RefreshSeatVisuals();
                SetStatus($"已把 {moving.Name} 移到 {target.PositionLabel}。");
            }
        });
    }

    public void ClearSelectedSeat()
    {
        if (SelectedSeat is null) return;

        var label = SelectedSeat.PositionLabel;
        var position = SelectedSeat.Position;
        RecordChange("清空座位", () =>
        {
            _assignments.Remove(position);
            RefreshSeatVisuals();
            SetStatus($"已清空 {label}。");
        });
    }

    // ----------------------------- 行列调整 -----------------------------

    /// <summary>所有座位视图模型，供拖拽高亮遍历。</summary>
    public IEnumerable<SeatCellViewModel> Seats => _seats.Values;

    /// <summary>完整名单（不受搜索过滤影响），供配对弹窗的选择器使用。</summary>
    public IReadOnlyList<StudentViewModel> AllStudents => _students;

    /// <summary>当前布局（含画布），供画布编辑器打开时读取。</summary>
    public SeatChartLayout CurrentLayoutPublic => CurrentLayout;

    /// <summary>当前画布，供画布编辑器打开时读取。</summary>
    public SeatChartCanvas CurrentCanvas => Canvas;

    // ----------------------------- 考场模式 -----------------------------

    /// <summary>进入考场前对日常座位的完整快照；为空表示当前不在考场模式。</summary>
    private (SeatChartLayout Layout, Dictionary<SeatPosition, StudentViewModel> Assignments,
        SeatChartSettings Settings, string Title)? _dailySnapshot;

    private ExamPlan? _examPlan;

    [ObservableProperty] private bool _isExamMode;

    public bool CanEnterExam => HasStudents && !IsBusy && !IsExamMode;
    public bool CanLeaveExam => IsExamMode;
    public string ExamSummary => _examPlan is null
        ? string.Empty
        : $"考场视图 · {_examPlan.Assignments.Count} 人 · {_examPlan.Layout.Columns} 列 × {_examPlan.Layout.Rows} 排";

    /// <summary>进入考场模式：先存日常快照，再把座位换成单人桌考场。</summary>
    public void EnterExamMode(ExamLayoutOptions options)
    {
        if (_students.Count == 0)
        {
            SetStatus("先导入一份名单，再切考场模式。", true);
            return;
        }

        _dailySnapshot = (
            CurrentLayout,
            new Dictionary<SeatPosition, StudentViewModel>(_assignments),
            ToSettings(),
            Title);

        RecordChange("进入考场模式", () => EnterExamModeCore(options));
    }

    private void EnterExamModeCore(ExamLayoutOptions options)
    {
        _examPlan = ExamPlanner.FromAssignments(
            _students.Select(s => s.Model).ToList(), GetCoreAssignments(), options);

        // 考场布局是独立的 layout（CurrentLayout 在考场模式下直接返回它），
        // 不改日常的组/排/列滑块值——那会让界面显示 4 列而实际是 7 列。
        IsExamMode = true;

        var byId = _students.ToDictionary(s => s.Id);
        _assignments.Clear();
        foreach (var (position, model) in _examPlan.Assignments)
        {
            if (byId.TryGetValue(model.Id, out var student)) _assignments[position] = student;
        }

        BuildGrid();
        RefreshSeatVisuals();
        NotifyCounts();

        var notes = _examPlan.Notes.Count > 0 ? $" · {string.Join("；", _examPlan.Notes)}" : string.Empty;
        SetStatus($"已进入考场模式：{ExamSummary}{notes}");
    }

    /// <summary>返回日常座位：整体还原布局、座位与设置。</summary>
    public void LeaveExamMode() => RecordChange("返回日常座位", LeaveExamModeCore);

    private void LeaveExamModeCore()
    {
        if (_dailySnapshot is not { } snapshot) return;

        // 关模式后 CurrentLayout 自动回到日常的组/排/列与画布。
        IsExamMode = false;
        _suppressRebuild = true;
        Canvas = snapshot.Settings.Canvas;
        Title = snapshot.Title;
        _suppressRebuild = false;

        _assignments.Clear();
        foreach (var (position, student) in snapshot.Assignments) _assignments[position] = student;

        _dailySnapshot = null;
        _examPlan = null;

        BuildGrid();
        RefreshSeatVisuals();
        NotifyCounts();
        SetStatus("已返回日常座位。");
    }

    partial void OnIsExamModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEnterExam));
        OnPropertyChanged(nameof(CanLeaveExam));
        OnPropertyChanged(nameof(CanExportConfig));
        OnPropertyChanged(nameof(ExamSummary));
    }

    /// <summary>当前考场里的座号（座位 → 1 基座号）；不在考场模式时为空。</summary>
    public IReadOnlyDictionary<SeatPosition, int> ExamSeatNumbers =>
        _examPlan?.SeatNumbers ?? new Dictionary<SeatPosition, int>();

    /// <summary>构造桌贴文档：按座号顺序，每张含姓名、考号、座号、班级与考场。</summary>
    public DeskStickerDocument BuildDeskStickers(string className, string examRoom)
    {
        var stickers = new List<DeskSticker>();

        if (_examPlan is not null)
        {
            foreach (var (position, student) in _assignments
                         .OrderBy(kv => _examPlan.SeatNumbers.GetValueOrDefault(kv.Key, int.MaxValue)))
            {
                stickers.Add(new DeskSticker(
                    student.Model.Name,
                    student.Model.Number,
                    _examPlan.SeatNumbers.GetValueOrDefault(position),
                    className,
                    examRoom));
            }
        }

        return new DeskStickerDocument(Title, stickers, "本机生成，文件未上传");
    }

    /// <summary>周轮转：只换组，行列不动；锁定跟随学生。</summary>
    public void RotateGroups(int dx)
    {
        if (IsExamMode)
        {
            SetStatus("考场视图下不能周轮转。", true);
            return;
        }

        if (_assignments.Count == 0)
        {
            SetStatus("还没有已安排的座位，先自动排座。", true);
            return;
        }

        RecordChange("周轮转", () => RotateGroupsCore(dx));
    }

    private void RotateGroupsCore(int dx)
    {
        var rotated = SeatChartComposer.RotateGroups(_assignments, CurrentLayout, dx);

        var blocked = _assignments.Count - rotated.Count;
        var kept = 0;
        foreach (var (position, student) in _assignments)
        {
            if (!rotated.ContainsKey(position)) continue;
            if (rotated[position] != student) continue;
            kept++;
        }

        _assignments.Clear();
        foreach (var (position, student) in rotated) _assignments[position] = student;

        // 锁定跟着学生走：把每条锁的座位按同样的组变换更新。
        var groups = CurrentLayout.Groups;
        if (groups > 1 && Constraints.Pins.Count > 0)
        {
            var updated = new Dictionary<Guid, SeatPosition>(Constraints.Pins.Count);
            foreach (var (id, seat) in Constraints.Pins)
            {
                var group = ((seat.Group + dx) % groups + groups) % groups;
                updated[id] = seat with { Group = group };
            }

            Constraints.ReplacePins(updated);
        }

        RefreshSeatVisuals();
        Constraints.RefreshRows();

        var direction = dx > 0 ? "右" : "左";
        var note = kept > 0 ? $"（{kept} 人因目标格是过道留在原组）" : string.Empty;
        SetStatus($"已整体轮转一组（向{direction}，最外侧绕回）{note}");
    }

    /// <summary>约束变更后由窗口层调用的刷新入口。</summary>
    public void RefreshSeatVisualsPublic() => RefreshSeatVisuals();

    /// <summary>名单变更后由窗口层调用的刷新入口。</summary>
    public void RefreshStudentsPublic() => RefreshStudents();

    /// <summary>整班座位沿当前视角平移一格：dx 向右、dy 向下，超出边缘自动绕回。</summary>
    public void ShiftAllSeats(int dx, int dy)
    {
        if (_assignments.Count == 0)
        {
            SetStatus("还没有已安排的座位，先自动排座或手动放入。", true);
            return;
        }

        if (RefuseIfPinned(_assignments.Values, "整体平移")) return;

        // 方向按屏幕换算，与 BuildGrid / 导出共用同一份摆放顺序。
        RecordChange("整班平移", () =>
        {
            var shifted = SeatChartComposer.ShiftOnScreen(_assignments, CurrentLayout, CurrentView, dx, dy);

            _assignments.Clear();
            foreach (var (position, student) in shifted)
                _assignments[position] = student;

            RefreshSeatVisuals();
            var direction = dy < 0 ? "上" : dy > 0 ? "下" : dx < 0 ? "左" : "右";
            SetStatus($"已整体向{direction}平移一格（边缘自动绕回）。");
        });
    }

    /// <summary>整排对调：两排在所有小组、所有列上互换学生。</summary>
    public void SwapRows(int rowA, int rowB)
    {
        var layout = CurrentLayout;
        if (rowA == rowB || rowA < 0 || rowB < 0 || rowA >= layout.Rows || rowB >= layout.Rows) return;
        if (_assignments.Count == 0)
        {
            SetStatus("还没有已安排的座位。", true);
            return;
        }

        if (RefuseIfPinned(_assignments.Values, "整排对调")) return;

        RecordChange("整排对调", () =>
        {
            for (var group = 0; group < layout.Groups; group++)
                for (var column = 0; column < layout.Columns; column++)
                    SwapAssignments(new SeatPosition(group, rowA, column), new SeatPosition(group, rowB, column));

            RefreshSeatVisuals();
            SetStatus($"已把第 {Math.Min(rowA, rowB) + 1} 排与第 {Math.Max(rowA, rowB) + 1} 排整排对调。");
        });
    }

    /// <summary>整列对调：两列在所有排上互换学生，可以跨小组。</summary>
    public void SwapColumns(int groupA, int columnA, int groupB, int columnB)
    {
        var layout = CurrentLayout;
        if (groupA == groupB && columnA == columnB) return;
        if (groupA < 0 || groupB < 0 || groupA >= layout.Groups || groupB >= layout.Groups ||
            columnA < 0 || columnB < 0 || columnA >= layout.Columns || columnB >= layout.Columns) return;
        if (_assignments.Count == 0)
        {
            SetStatus("还没有已安排的座位。", true);
            return;
        }

        if (RefuseIfPinned(_assignments.Values, "整列对调")) return;

        RecordChange("整列对调", () =>
        {
            for (var row = 0; row < layout.Rows; row++)
                SwapAssignments(new SeatPosition(groupA, row, columnA), new SeatPosition(groupB, row, columnB));

            RefreshSeatVisuals();
            SetStatus(columnA == columnB
                ? $"已把第 {Math.Min(groupA, groupB) + 1} 组与第 {Math.Max(groupA, groupB) + 1} 组的第 {columnA + 1} 列整列对调。"
                : $"已把第 {groupA + 1} 组第 {columnA + 1} 列与第 {groupB + 1} 组第 {columnB + 1} 列整列对调。");
        });
    }

    private void SwapAssignments(SeatPosition a, SeatPosition b)
    {
        _assignments.TryGetValue(a, out var studentA);
        _assignments.TryGetValue(b, out var studentB);

        if (studentB is not null) _assignments[a] = studentB; else _assignments.Remove(a);
        if (studentA is not null) _assignments[b] = studentA; else _assignments.Remove(b);
    }

    public bool HasSelectedStudent => SelectedStudent is not null;
    public string EditPanelTitle => SelectedStudent is null ? "新增学生" : $"编辑学生 · {SelectedStudent.Name}";

    public void ApplyStudentEdit()
    {
        if (SelectedStudent is null)
        {
            SetStatus("先在名单里选中一名学生。", true);
            return;
        }

        var name = EditName.Trim();
        if (name.Length == 0)
        {
            SetStatus("姓名不能为空。", true);
            return;
        }

        var model = SelectedStudent.Model;
        var gender = EditGenderIndex switch
        {
            1 => Gender.Male,
            2 => Gender.Female,
            _ => Gender.Unknown,
        };
        var number = EditNumber.Trim();
        var height = ParseOptional(EditHeight);
        var vision = ParseOptional(EditVision);
        var notes = EditNotes.Trim();

        RecordChange("编辑学生", () =>
        {
            model.Name = name;
            model.Number = number;
            model.Gender = gender;
            model.Height = height;
            model.Vision = vision;
            model.Notes = notes;

            SelectedStudent.Refresh();
            RefreshSeatVisuals();
            RefreshStudents();
            SetStatus($"已保存 {name} 的信息。");
        });
    }

    public void AddStudent()
    {
        var name = EditName.Trim();
        if (name.Length == 0)
        {
            SetStatus("先填写姓名，再点「新增学生」。", true);
            return;
        }

        var student = new StudentViewModel(new Student
        {
            Name = name,
            Number = EditNumber.Trim(),
            Gender = EditGenderIndex switch
            {
                1 => Gender.Male,
                2 => Gender.Female,
                _ => Gender.Unknown,
            },
            Height = ParseOptional(EditHeight),
            Vision = ParseOptional(EditVision),
            Notes = EditNotes.Trim(),
        });
        RecordChange("新增学生", () =>
        {
            _students.Add(student);
            RefreshStudents();
            SelectedStudent = student;
            NotifyCounts();
            SetStatus($"已把 {name} 加入名单。");
        });
    }

    public void DeleteSelectedStudent()
    {
        if (SelectedStudent is null)
        {
            SetStatus("先选中要删除的学生。", true);
            return;
        }

        var student = SelectedStudent;
        RecordChange("删除学生", () =>
        {
            foreach (var pair in _assignments.Where(kv => kv.Value == student).ToList())
                _assignments.Remove(pair.Key);

            // 删学生要连带删掉他的锁与规则，否则会留下指向不存在学生的幽灵约束。
            Constraints.Pins.Remove(student.Id);
            Constraints.Mutex.RemoveAll(p => p.A == student.Id || p.B == student.Id);
            Constraints.Deskmates.RemoveAll(p => p.A == student.Id || p.B == student.Id);
            Constraints.AvoidDeskmates.RemoveAll(p => p.A == student.Id || p.B == student.Id);
            Constraints.Zones.Remove(student.Id);
            Constraints.RefreshRows();

            _students.Remove(student);
            SelectedStudent = null;
            RefreshSeatVisuals();
            RefreshStudents();
            SetStatus($"已从名单中删除 {student.Name}。");
        });
    }

    private static double? ParseOptional(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    // ----------------------------- 配置 / 导出 -----------------------------

    public void ResetStyle() => RecordChange("恢复默认样式", ResetStyleCore);

    private void ResetStyleCore()
    {
        SeatBackgroundColor = Color.Parse("#FFFFFF");
        EmptySeatColor = Color.Parse("#FAF9F5");
        SeatTextColor = Color.Parse("#141413");
        SeatBorderColor = Color.Parse("#E7E3D8");
        PodiumBackgroundColor = Color.Parse("#F4F2EC");
        PodiumTextColor = Color.Parse("#85806F");
        GroupLabelColor = Color.Parse("#C6613F");
        RowLabelColor = Color.Parse("#85806F");
        FemaleMarkColor = Color.Parse("#C6613F");
        NameFontSize = 14;
        BoldNames = false;
        HideEmptySeats = false;
        MarkFemales = true;
        SpaceTwoCharNames = true;
        ShowGroupRowHints = true;
        PodiumGap = 16;
        SetStatus("座位表样式已重置。");
    }

    public SeatChartSettings ToSettings() => new()
    {
        Title = Title,
        BoldTitle = BoldTitle,
        HeadTeacher = HeadTeacher,
        Monitor = Monitor,
        View = CurrentView,
        Groups = Math.Clamp((int)Math.Round(GroupCount), 1, 8),
        Rows = Math.Clamp((int)Math.Round(RowCount), 1, 10),
        Columns = Math.Clamp((int)Math.Round(ColumnCount), 1, 4),
        CellScalePercent = Math.Clamp((int)Math.Round(CellScale), 50, 200),
        PodiumGap = Math.Clamp((int)Math.Round(PodiumGap), 0, 100),
        NameFontSize = FontSizeValue,
        BoldNames = BoldNames,
        HideEmptySeats = HideEmptySeats,
        MarkFemales = MarkFemales,
        SpaceTwoCharNames = SpaceTwoCharNames,
        ShowGroupRowHints = ShowGroupRowHints,
        SeatBackground = Hex(SeatBackgroundColor),
        EmptySeatBackground = Hex(EmptySeatColor),
        SeatText = Hex(SeatTextColor),
        SeatBorder = Hex(SeatBorderColor),
        PodiumBackground = Hex(PodiumBackgroundColor),
        PodiumText = Hex(PodiumTextColor),
        GroupLabel = Hex(GroupLabelColor),
        RowLabel = Hex(RowLabelColor),
        FemaleMark = Hex(FemaleMarkColor),
        ArrangeMode = (ArrangeMode)Math.Clamp(ArrangeModeIndex, 0, ArrangeModes.Count - 1),
        Canvas = Canvas,
        RowGapPercent = (int)Math.Round(RowGapPercent),
        ColumnGapPercent = (int)Math.Round(ColumnGapPercent),
        GroupGapPercent = (int)Math.Round(GroupGapPercent),
        TierRowGrowthPercent = (int)Math.Round(TierRowGrowthPercent),
        ShowDoors = ShowDoors,
        PodiumLabel = PodiumLabel,
        PodiumWidthPercent = (int)Math.Round(PodiumWidthPercent),
        PodiumHeight = (int)Math.Round(PodiumHeightValue),
        MentorEnabled = MentorEnabled,
        MentorSeat = MentorSeatIndex == 1 ? MentorSeat.FrontRow : MentorSeat.Center,
    };

    /// <summary>导出用的约束快照；无约束时返回 null，让配置里不多出一张空表。</summary>
    public ArrangeConstraints? GetCoreConstraints()
    {
        var built = Constraints.Build(CurrentLayout);
        var withMentor = new ArrangeConstraints
        {
            Pins = built.Pins,
            Mutex = built.Mutex,
            Deskmates = built.Deskmates,
            AvoidDeskmates = built.AvoidDeskmates,
            Zones = built.Zones,
            MentorEnabled = MentorEnabled,
            MentorSeat = MentorSeatIndex == 1 ? MentorSeat.FrontRow : MentorSeat.Center,
        };
        return withMentor.IsEmpty ? null : withMentor;
    }

    /// <summary>
    /// 导出用的文档。日常模式与旧版逐字段一致；考场模式必须走这里的旁路，
    /// 否则 ToSettings() 带出的组 / 排 / 列仍是日常滑块的值，Normalize 再把列数夹到 4——
    /// 界面画的是 6 列 × 5 排，导出却切成 4 列，后排的学生整片消失。
    ///
    /// 两处旁路都只作用于 `IsExamMode && _examPlan != null` 这一个分支：
    /// 显式布局（不夹取、直接用考场 layout）与座号表（日常为空表，渲染与导出都不画）。
    /// 不改日常滑块，也不改 _examPlan 的快照语义。
    /// </summary>
    public SeatChartDocument ComposeDocument()
    {
        var settings = ToSettings();
        if (IsExamMode && _examPlan is { } exam)
        {
            settings.LayoutOverride = exam.Layout;
            return SeatChartComposer.Compose(
                settings, GetCoreAssignments(), _students.Count, exam.SeatNumbers);
        }

        return SeatChartComposer.Compose(settings, GetCoreAssignments(), _students.Count);
    }

    public Dictionary<SeatPosition, Student> GetCoreAssignments() =>
        _assignments.ToDictionary(kv => kv.Key, kv => kv.Value.Model);

    public List<Student> GetModels() => _students.Select(s => s.Model).ToList();

    /// <summary>
    /// 套用配置或导回的座位表：布局、显示、配色、名单与座位整体替换。
    /// sourceName 为 null 时是「导入配置」；从「导入名单」导回导出的座位表时传文件名，状态栏据此说明还原了什么。
    /// </summary>
    public void ApplyConfig(SeatGridConfig config, string? sourceName = null)
    {
        // 同 LoadRoster：整体还原一份配置等于换了一份数据，撤销回去没有语义。
        _suspendUndo = true;
        _suppressRebuild = true;
        Title = config.Settings.Title;
        BoldTitle = config.Settings.BoldTitle;
        HeadTeacher = config.Settings.HeadTeacher;
        Monitor = config.Settings.Monitor;
        IsStudentView = config.Settings.View == ViewMode.Student;
        GroupCount = config.Settings.Groups;
        RowCount = config.Settings.Rows;
        ColumnCount = config.Settings.Columns;
        CellScale = config.Settings.CellScalePercent;
        PodiumGap = config.Settings.PodiumGap;
        NameFontSize = config.Settings.NameFontSize;
        BoldNames = config.Settings.BoldNames;
        HideEmptySeats = config.Settings.HideEmptySeats;
        MarkFemales = config.Settings.MarkFemales;
        SpaceTwoCharNames = config.Settings.SpaceTwoCharNames;
        ShowGroupRowHints = config.Settings.ShowGroupRowHints;
        SeatBackgroundColor = Color.Parse(config.Settings.SeatBackground);
        EmptySeatColor = Color.Parse(config.Settings.EmptySeatBackground);
        SeatTextColor = Color.Parse(config.Settings.SeatText);
        SeatBorderColor = Color.Parse(config.Settings.SeatBorder);
        PodiumBackgroundColor = Color.Parse(config.Settings.PodiumBackground);
        PodiumTextColor = Color.Parse(config.Settings.PodiumText);
        GroupLabelColor = Color.Parse(config.Settings.GroupLabel);
        RowLabelColor = Color.Parse(config.Settings.RowLabel);
        FemaleMarkColor = Color.Parse(config.Settings.FemaleMark);
        ArrangeModeIndex = (int)config.Settings.ArrangeMode;
        Canvas = config.Settings.Canvas;
        RowGapPercent = config.Settings.RowGapPercent;
        ColumnGapPercent = config.Settings.ColumnGapPercent;
        GroupGapPercent = config.Settings.GroupGapPercent;
        TierRowGrowthPercent = config.Settings.TierRowGrowthPercent;
        ShowDoors = config.Settings.ShowDoors;
        PodiumLabel = config.Settings.PodiumLabel;
        PodiumWidthPercent = config.Settings.PodiumWidthPercent;
        PodiumHeightValue = config.Settings.PodiumHeight;
        MentorEnabled = config.Settings.MentorEnabled;
        MentorSeatIndex = config.Settings.MentorSeat == MentorSeat.FrontRow ? 1 : 0;
        _suppressRebuild = false;

        _students.Clear();
        _assignments.Clear();
        foreach (var student in config.Students)
            _students.Add(new StudentViewModel(student));

        Constraints = new SeatConstraintsViewModel(_students);
        Constraints.Apply(config.Constraints);
        OnPropertyChanged(nameof(Constraints));

        SelectedStudent = null;
        SelectedSeat = null;
        BuildGrid();

        var byId = _students.ToDictionary(s => s.Model.Id);
        _assignments.Clear();
        foreach (var (position, student) in config.Assignments)
        {
            if (byId.TryGetValue(student.Id, out var vm))
                _assignments[position] = vm;
        }

        ImportSource = sourceName ?? "配置文件";
        RosterHint = string.Join("；", config.Warnings);
        RefreshSeatVisuals();
        RefreshStudents();
        NotifyCounts();
        var summary = sourceName is null
            ? "已导入配置"
            : $"已从「{sourceName}」还原 {StudentCount} 人与 {AssignedCount} 个座位";
        SetStatus(config.Warnings.Count > 0
            ? $"{summary} · {string.Join("；", config.Warnings)}"
            : $"{summary}。");

        _suspendUndo = false;
        ClearUndo();
    }

    public void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusBrush = error ? ErrorBrush : SuccessBrush;
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(StudentCount));
        OnPropertyChanged(nameof(MaleCount));
        OnPropertyChanged(nameof(FemaleCount));
        OnPropertyChanged(nameof(AssignedCount));
        OnPropertyChanged(nameof(UnassignedCount));
        OnPropertyChanged(nameof(SeatCount));
        OnPropertyChanged(nameof(RosterStats));
        OnPropertyChanged(nameof(SeatStats));
        OnPropertyChanged(nameof(InfoLine));
        OnPropertyChanged(nameof(HasStudents));
        OnPropertyChanged(nameof(CanArrange));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(IsChartEmpty));
    }
}
