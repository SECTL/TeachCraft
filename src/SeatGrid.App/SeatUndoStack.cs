using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Models;
using SeatGrid.Core.Seating;

namespace SeatGrid.App;

/// <summary>
/// 一份可撤销的座位表状态快照。
///
/// 为什么用整份快照而不是命令对象：归位有 30 来处会改数据的入口（自动排座、拖拽、
/// 平移、镜像、轮转、末尾增删、画布、约束、名单增删改……），每条都写一对 Do/Undo
/// 既容易漏，也容易在「撤销时依赖已被改掉的状态」上出错。快照是 O(座位数 + 人数)
/// 的扁平拷贝，一个班几十到上百人，代价可以忽略；换来的是「任何入口都没有忘记
/// 登记」这条更强的保证——只要变更前调一次 <see cref="SeatUndoStack.Capture"/>。
///
/// 快照里存的是**数据**，不是 ViewModel 对象：学生用 <see cref="Student.Copy"/> 克隆并
/// 保留 Id，座位用它自己的坐标。这样撤销时能整体重建 ViewModel，而不必去猜
/// 哪些派生属性需要重新通知。
/// </summary>
public sealed class SeatStateSnapshot
{
    public required SeatChartLayout Layout { get; init; }
    public required SeatChartCanvas Canvas { get; init; }
    public required IReadOnlyList<Student> Students { get; init; }
    public required IReadOnlyDictionary<SeatPosition, Guid> Assignments { get; init; }

    /// <summary>锁定：学生 Id → 座位。</summary>
    public required IReadOnlyDictionary<Guid, SeatPosition> Pins { get; init; }

    public required IReadOnlyList<SeatPairState> Mutex { get; init; }
    public required IReadOnlyList<SeatPairState> Deskmates { get; init; }
    public required IReadOnlyList<SeatPairState> AvoidDeskmates { get; init; }

    /// <summary>区域限制：学生 Id → 允许 / 禁止的排区间。</summary>
    public required IReadOnlyDictionary<Guid, SeatZoneState> Zones { get; init; }

    /// <summary>
    /// 拍照时是否处在考场模式，以及（在考场里时）那份考场安排。
    ///
    /// 考场不是一个普通字段：它把 <c>CurrentLayout</c> 整体换成 <c>_examPlan.Layout</c>，
    /// 而 <c>Layout</c> 记的是**当时界面上的**布局。不记这个标志，撤销「进入考场模式」
    /// 只会还原座位与布局，`IsExamMode` 仍是 true，界面继续按考场画——
    /// 撤销看起来没生效。
    /// </summary>
    public bool IsExam { get; init; }

    /// <summary>考场安排；只有 <see cref="IsExam"/> 为真时才有值。</summary>
    public ExamPlanSnapshot? Exam { get; init; }

    /// <summary>这一步做了什么，供状态栏显示「已撤销：自动排座」。</summary>
    public required string Label { get; init; }

    /// <summary>
    /// 内容相等，不含 Label。拖一次滑块会连发几十次变更，
    /// 用内容比对把它们合成一步，否则 Ctrl+Z 要按几十次才退得回去。
    /// </summary>
    public bool ContentEquals(SeatStateSnapshot? other)
    {
        if (other is null) return false;

        if (Layout.Groups != other.Layout.Groups ||
            Layout.Rows != other.Layout.Rows ||
            Layout.Columns != other.Layout.Columns) return false;

        if (Students.Count != other.Students.Count) return false;
        for (var i = 0; i < Students.Count; i++)
        {
            var a = Students[i];
            var b = other.Students[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Gender != b.Gender ||
                a.Number != b.Number || a.Height != b.Height || a.Vision != b.Vision ||
                a.Score != b.Score || a.Notes != b.Notes || a.OriginalGroup != b.OriginalGroup)
                return false;
        }

        if (Assignments.Count != other.Assignments.Count) return false;
        foreach (var (position, id) in Assignments)
            if (!other.Assignments.TryGetValue(position, out var otherId) || otherId != id) return false;

        if (Pins.Count != other.Pins.Count) return false;
        foreach (var (id, seat) in Pins)
            if (!other.Pins.TryGetValue(id, out var otherSeat) || otherSeat != seat) return false;

        if (!PairsEqual(Mutex, other.Mutex)) return false;
        if (!PairsEqual(Deskmates, other.Deskmates)) return false;
        if (!PairsEqual(AvoidDeskmates, other.AvoidDeskmates)) return false;

        if (Zones.Count != other.Zones.Count) return false;
        foreach (var (id, zone) in Zones)
            if (!other.Zones.TryGetValue(id, out var otherZone) || !zone.Equals(otherZone)) return false;

        if (IsExam != other.IsExam) return false;

        // 考场模式下布局本身就是考场布局，上面已比过；座号表由 ExamPlanner 决定，不单独比。

        // 画布：过道集合 + 三条走廊 + 门窗。SeatChartCanvas 自己实现了结构相等。
        return Canvas.Equals(other.Canvas);
    }

    private static bool PairsEqual(IReadOnlyList<SeatPairState> a, IReadOnlyList<SeatPairState> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
}

/// <summary>约束里的一对（互斥 / 结对 / 上期同桌）。与 Core 的 StudentPair 同构，但可序列化比较。</summary>
public readonly record struct SeatPairState(Guid A, Guid B);

/// <summary>区域限制的快照形态：允许 / 禁止的矩形列表。</summary>
public sealed class SeatZoneState
{
    public required IReadOnlyList<SeatRect> Allowed { get; init; }
    public required IReadOnlyList<SeatRect> Denied { get; init; }

    public bool IsEmpty => Allowed.Count == 0 && Denied.Count == 0;

    public StudentZone ToZone() => new(Allowed.ToList(), Denied.ToList());

    public bool Equals(SeatZoneState? other)
    {
        if (other is null) return false;
        return Allowed.SequenceEqual(other.Allowed) && Denied.SequenceEqual(other.Denied);
    }
}

/// <summary>
/// 撤销 / 重做栈。
///
/// 语义与老师对「撤销」的预期一致：
/// - **超出上限时从最老的一端丢**（保留最近的 N 步），而不是停止记录——
///   停止记录意味着「按了没反应」，比丢最老的一步更让人困惑。
/// - **内容没变就不入栈**：滑块连发、重复点击同一按钮都不该占一步。
/// - **撤销后新的改动清空重做栈**（标准编辑器语义），否则重做会把两条分叉揉在一起。
/// - 撤销 / 重做自身不入栈。
/// </summary>
public sealed class SeatUndoStack
{
    /// <summary>默认 100 步。一个班一周的编辑远远不到，但足够撤回一次误操作。</summary>
    public const int DefaultCapacity = 100;

    private readonly List<SeatStateSnapshot> _undo = new();
    private readonly List<SeatStateSnapshot> _redo = new();
    private readonly int _capacity;

    public SeatUndoStack(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>下一步撤销会退回的标签（「自动排座」），供按钮悬浮提示。</summary>
    public string? NextUndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    public string? NextRedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>
    /// 记一步。<paramref name="before"/> 是**变更前**的状态，<paramref name="label"/> 说明这次做了什么。
    ///
    /// 变更前的状态与栈顶内容相同就整个跳过：那意味着上一次所谓「变更」其实什么都没改
    /// （滑块同一档位连发、点了没反应的按钮）。跳过而不是覆盖栈顶——栈顶是上一步的真实
    /// 起点，覆盖它会让那一步撤不回去。
    /// </summary>
    public void Push(SeatStateSnapshot before, string label)
    {
        if (_undo.Count > 0 && _undo[^1].ContentEquals(before)) return;

        _undo.Add(before);
        if (_undo.Count > _capacity) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>
    /// 撤销：把 <paramref name="current"/> 压进重做栈，返回要恢复的状态。
    /// 没有可撤销的步骤时返回 null。
    /// </summary>
    public SeatStateSnapshot? Undo(SeatStateSnapshot current)
    {
        if (_undo.Count == 0) return null;

        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return previous;
    }

    /// <summary>重做：把 <paramref name="current"/> 压回撤销栈，返回要恢复的状态。</summary>
    public SeatStateSnapshot? Redo(SeatStateSnapshot current)
    {
        if (_redo.Count == 0) return null;

        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        return next;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

/// <summary>
/// 考场安排的快照。只留重建所需的东西：布局、谁坐哪、座号与备注。
/// 与 <see cref="ExamPlan"/> 分开是因为那个类型带着 <c>Student</c> 引用，
/// 而快照里学生是按 Id 记的（撤销时再换回当前实例）。
/// </summary>
public sealed class ExamPlanSnapshot
{
    public required SeatChartLayout Layout { get; init; }
    public required IReadOnlyDictionary<SeatPosition, Guid> Assignments { get; init; }
    public required IReadOnlyDictionary<SeatPosition, int> SeatNumbers { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public SeatGrid.Core.Exam.ExamPlan ToExamPlan(IReadOnlyDictionary<Guid, Student> byId)
    {
        var assignments = new Dictionary<SeatPosition, Student>();
        foreach (var (position, id) in Assignments)
            if (byId.TryGetValue(id, out var student)) assignments[position] = student;

        return new SeatGrid.Core.Exam.ExamPlan(
            Layout, assignments, new Dictionary<SeatPosition, int>(SeatNumbers), Notes.ToList());
    }
}
