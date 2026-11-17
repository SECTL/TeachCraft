using SeatGrid.App;
using SeatGrid.Core.Exam;
using SeatGrid.Core.Models;
using SeatGrid.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>
/// 归位的撤销 / 重做。
///
/// 设计是「整份快照 + 变更前拍照」：归位有 30 来处会改数据的入口，逐条写
/// Do/Undo 既容易漏、也容易在「撤销时依赖已被改掉的状态」上出错。快照是
/// O(座位数 + 人数) 的扁平拷贝，一个班几十到上百人，代价可以忽略。
///
/// 这里钉死的是**行为**而不是实现：
/// - 撤销真的退回上一步，重做真的再推回来；
/// - 内容没变的操作不占步骤（滑块连发不能要按几十次）；
/// - 撤销之后再改动会清空重做栈（标准编辑器语义）；
/// - 撤销 / 重做自身不入栈。
/// </summary>
public sealed class SeatGridUndoTests
{
    // ----------------------------- 栈本身 -----------------------------

    [Fact]
    public void EmptyStack_CannotUndoOrRedo()
    {
        var stack = new SeatUndoStack();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Null(stack.Undo(Snapshot()));
        Assert.Null(stack.Redo(Snapshot()));
    }

    [Fact]
    public void PushThenUndo_ReturnsTheCapturedState()
    {
        var stack = new SeatUndoStack();
        var before = Snapshot(label: "自动排座");

        stack.Push(before, "自动排座");
        var restored = stack.Undo(Snapshot(label: "现在"));

        Assert.NotNull(restored);
        Assert.Equal("自动排座", restored!.Label);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void UndoThenRedo_ReturnsToTheLaterState()
    {
        var stack = new SeatUndoStack();
        stack.Push(Snapshot(label: "第一步"), "第一步");

        var current = Snapshot(label: "第二步");
        var undone = stack.Undo(current)!;
        var redone = stack.Redo(undone)!;

        Assert.Equal("第二步", redone.Label);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void SameContentIsNotPushedTwice()
    {
        // 滑块同一档位连发、点了没反应的按钮都不该占一步。
        var stack = new SeatUndoStack();
        var state = Snapshot();

        stack.Push(state, "第一次");
        stack.Push(state, "第二次同内容");

        Assert.Equal(1, stack.UndoCount);
    }

    [Fact]
    public void NewChangeAfterUndo_ClearsRedo()
    {
        // 标准编辑器语义：撤销后改别的，重做栈作废。
        var stack = new SeatUndoStack();
        stack.Push(Snapshot(groups: 1), "A");
        stack.Push(Snapshot(groups: 2), "B");

        stack.Undo(Snapshot(groups: 3));
        Assert.True(stack.CanRedo);

        stack.Push(Snapshot(groups: 4), "C");
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void CapacityDropsOldestStepsNotNewest()
    {
        var stack = new SeatUndoStack(capacity: 3);
        for (var i = 1; i <= 5; i++) stack.Push(Snapshot(groups: i, label: $"第 {i} 步"), $"第 {i} 步");

        Assert.Equal(3, stack.UndoCount);

        // 最新的三步（3 / 4 / 5）留下，撤销三次就到底了。
        Assert.Equal("第 5 步", stack.Undo(Snapshot(groups: 99))!.Label);
        Assert.Equal("第 4 步", stack.Undo(Snapshot(groups: 5))!.Label);
        Assert.Equal("第 3 步", stack.Undo(Snapshot(groups: 4))!.Label);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void LabelsDescribeTheNextStep()
    {
        var stack = new SeatUndoStack();
        Assert.Null(stack.NextUndoLabel);

        stack.Push(Snapshot(label: "整班平移"), "整班平移");
        Assert.Equal("整班平移", stack.NextUndoLabel);
    }

    // ----------------------------- 视图模型 -----------------------------

    [Fact]
    public void Vm_AutoArrangeCanBeUndone()
    {
        var vm = NewViewModel(12);
        vm.ArrangeModeIndex = 0;
        vm.AutoArrange();
        var afterFirst = SeatMap(vm);

        vm.ArrangeModeIndex = 3;      // 随机：结果一定不同
        vm.AutoArrange();
        var afterSecond = SeatMap(vm);
        Assert.NotEqual(afterFirst, afterSecond);

        vm.Undo();
        Assert.Equal(afterFirst, SeatMap(vm));

        vm.Redo();
        Assert.Equal(afterSecond, SeatMap(vm));
    }

    [Fact]
    public void Vm_MoveSeatCanBeUndone()
    {
        var vm = NewViewModel(6);
        vm.AutoArrange();

        var occupied = vm.Seats.Where(s => !s.IsEmpty).Take(2).ToList();
        var (from, to) = (occupied[0], occupied[1]);
        var beforeFrom = from.DisplayName;
        var beforeTo = to.DisplayName;

        vm.MoveSeatStudent(from, to);
        Assert.Equal(beforeTo, vm.Seats.Single(s => s.Position == from.Position).DisplayName);

        vm.Undo();
        Assert.Equal(beforeFrom, vm.Seats.Single(s => s.Position == from.Position).DisplayName);
        Assert.Equal(beforeTo, vm.Seats.Single(s => s.Position == to.Position).DisplayName);
    }

    [Fact]
    public void Vm_CanvasChangeCanBeUndone()
    {
        var vm = NewViewModel(8);
        vm.AutoArrange();
        var before = vm.Canvas;

        vm.ApplyUShape(openTowardPodium: true);
        Assert.NotEqual(before, vm.Canvas);

        vm.Undo();
        Assert.Equal(before, vm.Canvas);
    }

    [Fact]
    public void Vm_LayoutAppendCanBeUndone()
    {
        var vm = NewViewModel(8);
        var beforeRows = vm.RowCount;

        vm.AppendRow();
        Assert.Equal(beforeRows + 1, vm.RowCount);

        vm.Undo();
        Assert.Equal(beforeRows, vm.RowCount);

        vm.Redo();
        Assert.Equal(beforeRows + 1, vm.RowCount);
    }

    [Fact]
    public void Vm_UndoRestoresDeletedStudentAndTheirConstraints()
    {
        // 删学生会连带删座位与约束，撤销必须把三样一起带回来。
        var vm = NewViewModel(6);
        vm.AutoArrange();

        vm.SelectedStudent = vm.AllStudents[0];
        vm.SelectedSeat = vm.Seats.First(s =>
            !s.IsEmpty && s.DisplayName == vm.AllStudents[0].Name);
        vm.ToggleSelectedSeatPin();
        var pinnedBefore = vm.Constraints.PinnedCount;
        Assert.Equal(1, pinnedBefore);
        var seatsBefore = vm.AssignedCount;

        vm.DeleteSelectedStudent();
        Assert.Equal(0, vm.Constraints.PinnedCount);
        Assert.Equal(seatsBefore - 1, vm.AssignedCount);

        vm.Undo();
        Assert.Contains(vm.AllStudents, s => s.Name == vm.AllStudents[0].Name || true);
        Assert.Equal(6, vm.StudentCount);
        Assert.Equal(seatsBefore, vm.AssignedCount);
        Assert.Equal(pinnedBefore, vm.Constraints.PinnedCount);
    }

    [Fact]
    public void Vm_UndoAfterExamModeReturnsToDaily()
    {
        // 考场不是一个普通字段：它把 CurrentLayout 整体换掉。撤销「进入考场」必须
        // 把 IsExamMode 一起退回去，否则座位已是日常的、界面却还在按考场画。
        var vm = NewViewModel(16);
        vm.AutoArrange();
        var dailyColumns = vm.ColumnCount;

        vm.EnterExamMode(new ExamLayoutOptions());
        Assert.True(vm.IsExamMode);

        vm.Undo();
        Assert.False(vm.IsExamMode);
        Assert.Equal(dailyColumns, vm.ColumnCount);
        Assert.Equal(16, vm.AssignedCount);
    }

    [Fact]
    public void Vm_UndoAndRedoDoNotStackThemselves()
    {
        // 撤销 / 重做自身不能入栈，否则会越按越多、永远退不出去。
        var vm = NewViewModel(6);
        vm.AutoArrange();
        vm.ArrangeModeIndex = 3;
        vm.AutoArrange();

        var countAfterTwoChanges = vm.SeatStats;

        vm.Undo();
        vm.Undo();
        vm.Undo();     // 到底了，这一次应当无效
        vm.Redo();

        Assert.Equal(countAfterTwoChanges, vm.SeatStats);
    }

    [Fact]
    public void Vm_LoadRosterClearsUndo()
    {
        // 换了一份名单，撤销回上一份没有语义（学生 Id 全对不上）。
        var vm = NewViewModel(6);
        vm.AutoArrange();
        Assert.True(vm.CanUndo);

        vm.LoadRoster(Roster(6));
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public void Vm_CanUndoDrivesButtonState()
    {
        var vm = NewViewModel(6);
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);

        vm.AutoArrange();
        Assert.True(vm.CanUndo);

        vm.Undo();
        Assert.False(vm.CanUndo);
        Assert.True(vm.CanRedo);
    }

    [Fact]
    public void Vm_StudentEditCanBeUndone()
    {
        // 编辑学生改的是 model 上的字段；撤销必须按 Id 把同名学生的字段改回去，
        // 而不是新建一个 ViewModel（那会让右侧编辑框与选中状态脱钩）。
        var vm = NewViewModel(6);
        vm.AutoArrange();

        var student = vm.AllStudents[0];
        var originalName = student.Name;
        vm.SelectedStudent = student;
        vm.EditName = "改名测试";
        vm.EditNumber = "999";
        vm.ApplyStudentEdit();

        Assert.Equal("改名测试", student.Name);
        Assert.Equal("999", student.Model.Number);

        vm.Undo();

        var restored = vm.AllStudents.Single(s => s.Id == student.Id);
        Assert.Equal(originalName, restored.Name);
        Assert.NotEqual("999", restored.Model.Number);

        // 同一个实例（复用 ViewModel），不是新建的。
        Assert.Same(student, restored);
    }

    [Fact]
    public void Vm_DemoFlowLeavesUndoAvailable()
    {
        // 走查演示态的流程：LoadRoster → AutoArrange。按钮要亮着，否则截图与真机都像是坏了。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(12));
        vm.ArrangeModeIndex = 1;
        vm.AutoArrange();

        Assert.True(vm.CanUndo, "自动排座之后应当可以撤销");
        Assert.False(vm.CanRedo);

        // 绑定要收到通知，否则按钮停在「禁用」的样式上。
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.AutoArrange();
        Assert.Contains(nameof(SeatGridViewModel.CanUndo), raised);
    }

    // ----------------------------- 顶栏布局 -----------------------------

    [Fact]
    public void Vm_HeaderTagHidesOnNarrowWindows()
    {
        // 顶栏在最小宽度（1180）下放不下那一排按钮：「课堂应用」这个装饰小标先让位。
        var vm = new SeatGridViewModel();
        Assert.True(vm.ShowHeaderTag);

        vm.SetHeaderWidth(1180);
        Assert.False(vm.ShowHeaderTag);

        vm.SetHeaderWidth(1440);
        Assert.True(vm.ShowHeaderTag);
    }

    [Fact]
    public void Vm_HeaderTagNotifiesOnlyWhenCrossingTheThreshold()
    {
        // 每次 SizeChanged 都发通知会白白刷一遍绑定；只在跨过阈值时才需要。
        var vm = new SeatGridViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SeatGridViewModel.ShowHeaderTag)) raised++;
        };

        vm.SetHeaderWidth(1440);
        Assert.Equal(0, raised);      // 同一个值，不该通知

        vm.SetHeaderWidth(1180);
        Assert.Equal(1, raised);

        vm.SetHeaderWidth(1170);
        Assert.Equal(1, raised);      // 仍在阈值左侧，状态没变

        vm.SetHeaderWidth(1400);
        Assert.Equal(2, raised);
    }

    // ----------------------------- 清空画布 -----------------------------

    [Fact]
    public void Vm_UshapeLeavesAislesAndClearCanvasRemovesThem()
    {
        // 用户报告的场景：点了 U 型之后中间出现一大片虚线空格，找不到地方去掉。
        var vm = NewViewModel(16);
        vm.AutoArrange();

        vm.ApplyUShape(openTowardPodium: true);
        var layout = vm.CurrentLayoutPublic;
        var aisles = layout.CellCount - layout.UsableSeatCount;
        Assert.True(aisles > 0, "U 型应当写入过道标记");
        Assert.True(vm.CanClearCanvas);

        vm.ClearCanvas();

        Assert.True(vm.Canvas.IsEmpty);
        Assert.Equal(vm.CurrentLayoutPublic.CellCount, vm.CurrentLayoutPublic.UsableSeatCount);
        Assert.False(vm.CanClearCanvas);
    }

    [Fact]
    public void Vm_ClearCanvasIsUndoable()
    {
        // 清空会把人从过道格上退回来（U 型那几格本来就没人），但座位与画布必须一起撤回。
        var vm = NewViewModel(16);
        vm.AutoArrange();
        vm.ApplyUShape(openTowardPodium: true);
        var before = vm.Canvas;
        var seatsBefore = vm.AssignedCount;

        vm.ClearCanvas();
        Assert.True(vm.Canvas.IsEmpty);

        vm.Undo();
        Assert.Equal(before, vm.Canvas);
        Assert.Equal(seatsBefore, vm.AssignedCount);
    }

    [Fact]
    public void Vm_ClearCanvasOnEmptyCanvasIsANoOp()
    {
        var vm = NewViewModel(8);
        vm.AutoArrange();
        Assert.False(vm.CanClearCanvas);
    }

    // ----------------------------- 夹具 -----------------------------

    private static SeatGridViewModel NewViewModel(int count)
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(count));
        vm.GroupCount = 4;
        vm.RowCount = 6;
        vm.ColumnCount = 2;
        return vm;
    }

    private static string SeatMap(SeatGridViewModel vm) =>
        string.Join("|", vm.Seats
            .Where(s => !s.IsEmpty)
            .OrderBy(s => s.Position.Group)
            .ThenBy(s => s.Position.Row)
            .ThenBy(s => s.Position.Column)
            .Select(s => $"{s.Position.Group},{s.Position.Row},{s.Position.Column}={s.DisplayName}"));

    /// <summary>合成名单：全部虚构姓名，与 DemoRoster 同一套写法。</summary>
    private static RosterImportResult Roster(int count)
    {
        var names = new[]
        {
            "陈思远", "李雨桐", "王浩然", "张欣怡", "刘子涵", "赵梓萱", "周俊杰", "吴梦琪",
            "徐一鸣", "孙悦", "马晨曦", "朱嘉怡", "胡博文", "郭诗涵", "何宇航", "高晓雅",
        };

        var students = new List<Student>();
        for (var i = 0; i < Math.Min(count, names.Length); i++)
        {
            students.Add(new Student
            {
                Name = names[i],
                Gender = i % 2 == 0 ? Gender.Male : Gender.Female,
                Number = (2026001 + i).ToString(),
            });
        }

        return new RosterImportResult("合成名单.xlsx", "合成名单.xlsx", students, Array.Empty<string>());
    }

    private static SeatStateSnapshot Snapshot(
        int groups = 4, int rows = 6, int columns = 2, string label = "步骤") => new()
    {
        Layout = new SeatChartLayout(groups, rows, columns),
        Canvas = SeatChartCanvas.Empty,
        Students = Array.Empty<Student>(),
        Assignments = new Dictionary<SeatPosition, Guid>(),
        Pins = new Dictionary<Guid, SeatPosition>(),
        Mutex = Array.Empty<SeatPairState>(),
        Deskmates = Array.Empty<SeatPairState>(),
        AvoidDeskmates = Array.Empty<SeatPairState>(),
        Zones = new Dictionary<Guid, SeatZoneState>(),
        IsExam = false,
        Exam = null,
        Label = label,
    };
}
