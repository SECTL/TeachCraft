using Avalonia.Threading;
using TeachCraft.App.Views;
using TeachCraft.Core.Control;

namespace TeachCraft.App.ControlCenter;

/// <summary>
/// 将集控指令从后台线程切回 Avalonia UI 线程执行；只调用主窗口与子窗口公开的安全窄接口，
/// 绝不越过边界读取或返回学生姓名、学号等隐私字段。
/// </summary>
public sealed class TeachCraftAppControlHost : ITeachCraftControlHost
{
    private readonly MainWindow _window;

    public TeachCraftAppControlHost(MainWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public Task<string> OpenSeatGridAsync(string? view, CancellationToken ct) => OnUiAsync(() =>
    {
        var seatGrid = _window.EnsureSeatGridWindowOpen();
        if (view is "teacher" or "student")
            return seatGrid.SetRemoteView(view == "student");

        return seatGrid.ReadRemoteSummary().View;
    }, ct);

    public Task<string> SetSeatGridViewAsync(string view, CancellationToken ct) => OnUiAsync(() =>
    {
        var seatGrid = _window.EnsureSeatGridWindowOpen();
        return seatGrid.SetRemoteView(view == "student");
    }, ct);

    public Task<bool> SetSeatGridProjectionAsync(bool on, CancellationToken ct) => OnUiAsync(() =>
    {
        if (on)
        {
            var seatGrid = _window.EnsureSeatGridWindowOpen();
            return seatGrid.SetRemoteProjection(true);
        }

        return _window.ActiveSeatGridWindow?.SetRemoteProjection(false) ?? false;
    }, ct);

    public Task<SeatGridReadSummary> SetSeatGridExamAsync(bool on, int columns, CancellationToken ct) => OnUiAsync(() =>
    {
        if (!on && _window.ActiveSeatGridWindow is null)
            return new SeatGridReadSummary(false, false, "teacher", 0, 0, 0, 0, 0, 0, false, "归位 · 未打开");

        var seatGrid = on ? _window.EnsureSeatGridWindowOpen() : _window.ActiveSeatGridWindow!;
        seatGrid.SetRemoteExam(on, columns);
        return ToSeatSummary(isOpen: true, seatGrid.ReadRemoteSummary());
    }, ct);

    public async Task<string?> ShowClassBlockAsync(string? className, CancellationToken ct)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var classBlock = _window.EnsureClassBlockWindowOpen();
            return await classBlock.ShowRemoteClassAsync(className);
        });
    }

    public Task<SeatGridReadSummary> ReadSeatGridAsync(CancellationToken ct) => OnUiAsync(() =>
    {
        if (_window.ActiveSeatGridWindow is not { } seatGrid)
            return new SeatGridReadSummary(false, false, "teacher", 0, 0, 0, 0, 0, 0, false, string.Empty);

        return ToSeatSummary(isOpen: true, seatGrid.ReadRemoteSummary());
    }, ct);

    public Task<ClassBlockReadSummary> ReadClassBlockAsync(CancellationToken ct) => OnUiAsync(() =>
    {
        if (_window.ActiveClassBlockWindow is not { } classBlock)
            return new ClassBlockReadSummary(false, false, string.Empty, string.Empty, 0, null, Array.Empty<string>());

        var s = classBlock.ReadRemoteSummary();
        return new ClassBlockReadSummary(
            IsOpen: true,
            HasMaster: s.HasMaster,
            Grade: s.Grade,
            Term: s.Term,
            ClassCount: s.Classes.Count,
            SelectedClass: s.SelectedClass,
            Classes: s.Classes);
    }, ct);

    public Task<GradeWeaveReadSummary> ReadGradeWeaveAsync(CancellationToken ct) => OnUiAsync(() =>
    {
        if (_window.ActiveGradeWeaveWindow is not { } weave)
            return new GradeWeaveReadSummary(false, false, string.Empty, 0, Array.Empty<string>(), 0, 0, 0, 0, 0, 0, 0, 0);

        var s = weave.ReadRemoteSummary();
        return new GradeWeaveReadSummary(
            IsOpen: true,
            HasProject: s.HasProject,
            ProjectTitle: s.ProjectTitle,
            ClassCount: s.Classes.Count,
            Classes: s.Classes,
            ConflictTotal: s.ConflictTotal,
            TeacherClash: s.TeacherClash,
            ClassClash: s.ClassClash,
            TeacherUnavailable: s.TeacherUnavailable,
            LayeredClash: s.LayeredClash,
            MissingTeacher: s.MissingTeacher,
            PeriodMismatch: s.PeriodMismatch,
            Unplaced: s.Unplaced);
    }, ct);

    private static Task<T> OnUiAsync<T>(Func<T> action, CancellationToken ct)
        => Dispatcher.UIThread.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            return action();
        }).GetTask();

    private static SeatGridReadSummary ToSeatSummary(
        bool isOpen,
        (bool IsExam, string View, int StudentCount, int AssignedCount, int SeatCount, int Groups, int Rows, int Columns, bool Projecting, string Title) s)
        => new(isOpen, s.IsExam, s.View, s.StudentCount, s.AssignedCount, s.SeatCount, s.Groups, s.Rows, s.Columns, s.Projecting, s.Title);
}
