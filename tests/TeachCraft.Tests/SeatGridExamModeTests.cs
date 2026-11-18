using SeatGrid.App;
using SeatGrid.Core.Exam;
using SeatGrid.Core.Export;
using SeatGrid.Core.Models;
using SeatGrid.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>
/// 考场模式下导入名单 / 配置必须先退出考场（调研 P1-2）。
///
/// 缺陷本体：<c>LoadRoster</c> 与 <c>ApplyConfig</c> 是「整体替换」，但它们不清
/// <c>IsExamMode</c> / <c>_examPlan</c> / <c>_dailySnapshot</c>。导入之后界面仍按考场画，
/// 座位却是新名单的日常坐标——考场快照指向的是旧名单的学生，名字一导就串。
///
/// 修法是替换前静默退出考场三态（不走 LeaveExamModeCore：随后 ClearUndo，
/// 走 RecordChange 只会留一个假步骤）。
/// </summary>
public sealed class SeatGridExamModeTests
{
    [Fact]
    public void LoadRoster_InExamMode_ExitsExamAndSeatsNewRoster()
    {
        var vm = NewExamViewModel(16);
        Assert.True(vm.IsExamMode);
        Assert.NotEmpty(vm.ExamSeatNumbers);

        vm.LoadRoster(Roster(12, "新名单.xlsx"));

        // 三态全清：模式标志、考场安排（座号表为空）、日常快照（返回日常不再有可还原的东西）。
        Assert.False(vm.IsExamMode);
        Assert.Empty(vm.ExamSeatNumbers);
        Assert.False(vm.CanLeaveExam);

        // 布局回到日常滑块，而不是考场那份单组 N 列。
        Assert.Equal(4, vm.CurrentLayoutPublic.Groups);
        Assert.Equal(2, vm.CurrentLayoutPublic.Columns);

        // 新名单已排座，且撤销栈为空（导入本就不该可撤）。
        Assert.Equal(12, vm.StudentCount);
        Assert.Equal(12, vm.AssignedCount);
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);

        // 状态栏说明退出了考场，老师不会以为还在考场里。
        Assert.Contains("已退出考场模式", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyConfig_InExamMode_ExitsExamAndClearsUndo()
    {
        var vm = NewExamViewModel(16);
        Assert.True(vm.IsExamMode);

        var config = Config(10);
        vm.ApplyConfig(config);

        Assert.False(vm.IsExamMode);
        Assert.Empty(vm.ExamSeatNumbers);
        Assert.False(vm.CanLeaveExam);
        Assert.Equal(4, vm.CurrentLayoutPublic.Groups);
        Assert.Equal(2, vm.CurrentLayoutPublic.Columns);
        Assert.Equal(10, vm.StudentCount);
        Assert.Equal(10, vm.AssignedCount);
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
        Assert.Contains("已退出考场模式", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRoster_OutsideExamMode_KeepsDailyBehaviour()
    {
        // 修复前全部行为对日常零变化：没进考场时状态栏不该多出「已退出考场模式」。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(8, "合成名单.xlsx"));

        Assert.False(vm.IsExamMode);
        Assert.DoesNotContain("已退出考场模式", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(8, vm.AssignedCount);
    }

    // ----------------------------- 考场锁定语义（调研 P1-5） -----------------------------

    [Fact]
    public void ExamMode_PinnedStudentCanStillMove()
    {
        // 考场下锁定不生效（AGENTS 第六节）。修复前 RefuseIfPinned 会拒绝移动，
        // 界面上锁图标还亮着——「说了不生效却又拦住操作」是最糟的组合。
        var vm = NewExamViewModel(16);
        var student = vm.Seats.First(s => !s.IsEmpty);
        vm.SelectedSeat = student;
        vm.SelectedStudent = vm.AllStudents.First(s => s.Name == student.DisplayName);
        vm.ToggleSelectedSeatPin();          // 考场下应被拒绝
        Assert.Equal(0, vm.Constraints.PinnedCount);

        // 日常先锁一把，再进考场：锁必须留着不动，但不拦考场里的移动。
        vm.LeaveExamMode();
        var dailyStudent = vm.AllStudents[0];
        vm.SelectedStudent = dailyStudent;
        vm.SelectedSeat = vm.Seats.First(s =>
            !s.IsEmpty && s.DisplayName == dailyStudent.Name);
        vm.ToggleSelectedSeatPin();
        Assert.Equal(1, vm.Constraints.PinnedCount);

        vm.EnterExamMode(new ExamLayoutOptions());
        var seats = vm.Seats.Where(s => !s.IsEmpty).Take(2).ToList();
        vm.MoveSeatStudent(seats[0], seats[1]);

        // 移动真的发生了（若被锁定拦住，状态栏会是「已锁定…先解除锁定」）。
        Assert.DoesNotContain("已锁定", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, vm.Constraints.PinnedCount);

        // 回日常：锁还在原位。
        vm.LeaveExamMode();
        Assert.Equal(1, vm.Constraints.PinnedCount);
    }

    [Fact]
    public void ExamMode_TogglePinIsRefusedWithMessage()
    {
        var vm = NewExamViewModel(12);
        var seat = vm.Seats.First(s => !s.IsEmpty);
        vm.SelectedSeat = seat;

        vm.ToggleSelectedSeatPin();

        Assert.Contains("考场模式不使用锁定", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, vm.Constraints.PinnedCount);
        Assert.False(vm.CanPinSeat);
    }

    [Fact]
    public void ExamMode_AutoArrangeIgnoresDailyConstraints()
    {
        // 日常锁 / 互斥在考场下不进引擎：否则日常锁会把考场排座按旧座位钉住，
        // 排出来的座号与考场布局对不上。
        var vm = NewExamViewModel(12);

        // 回到日常锁住一位学生，再进考场自动排座。
        vm.LeaveExamMode();
        var pinned = vm.AllStudents[0];
        vm.SelectedStudent = pinned;
        vm.SelectedSeat = vm.Seats.First(s => !s.IsEmpty && s.DisplayName == pinned.Name);
        vm.ToggleSelectedSeatPin();
        Assert.Equal(1, vm.Constraints.PinnedCount);

        vm.EnterExamMode(new ExamLayoutOptions());
        vm.ArrangeModeIndex = 1;             // 男女交替：结果一定与考场原序不同
        vm.AutoArrange();

        // 排完仍然是考场模式，且 12 人全部有座——没有被幽灵锁拦下的人。
        Assert.True(vm.IsExamMode);
        Assert.Equal(12, vm.AssignedCount);
        Assert.Equal(1, vm.Constraints.PinnedCount);   // 日常锁本身不动
    }

    // ----------------------------- 考场画布守卫（调研 P2-10） -----------------------------

    [Fact]
    public void ExamMode_CanvasEditingIsDisabled()
    {
        // 考场是独立快照：画布（过道 / 门窗）属于日常座位表，在考场里改会写到日常字段上，
        // 回日常时莫名其妙多出几条过道。两个按钮都绑 CanEditCanvas。
        var vm = NewExamViewModel(16);
        Assert.False(vm.CanEditCanvas);

        vm.LeaveExamMode();
        Assert.True(vm.CanEditCanvas);
    }

    [Fact]
    public void ExamMode_ClearCanvasIsRefused()
    {
        var vm = NewExamViewModel(16);

        // 日常先铺一张 U 型，进考场后画布按钮仍应置灰。
        vm.LeaveExamMode();
        vm.ApplyUShape(openTowardPodium: true);
        var canvas = vm.Canvas;
        Assert.False(canvas.IsEmpty);
        Assert.True(vm.CanEditCanvas);

        vm.EnterExamMode(new ExamLayoutOptions());
        Assert.False(vm.CanEditCanvas);
        Assert.Equal(canvas, vm.Canvas);
    }

    [Fact]
    public void CanEditCanvas_NotifiesOnExamModeChange()
    {
        var vm = NewExamViewModel(16);
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.LeaveExamMode();

        Assert.Contains(nameof(SeatGridViewModel.CanEditCanvas), raised);
    }

    // ----------------------------- 考场座号上屏（调研 P3-12） -----------------------------

    [Fact]
    public void ExamMode_SeatNumbersAppearOnScreen()
    {
        // ExamSeatNumbers 早就有了，但界面上没有消费方——屏幕上看不到座号，
        // 与 PNG / Excel 里画着座号对不上。座位格现在带 SeatNumberLabel。
        var vm = NewExamViewModel(30);

        var labelled = vm.Seats.Where(s => s.HasSeatNumber).ToList();
        Assert.Equal(30, labelled.Count);

        var numbers = labelled.Select(s => s.SeatNumberLabel).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(1, 30).Select(n => $"{n:00} 号"), numbers);

        // 与 ExamSeatNumbers 逐格一致：1 号在左上、6 号在右上、7 号在第二排最右（蛇形）。
        var byPos = labelled.ToDictionary(s => s.Position, s => s.SeatNumberLabel);
        Assert.Equal("01 号", byPos[new SeatPosition(0, 0, 0)]);
        Assert.Equal("06 号", byPos[new SeatPosition(0, 0, 5)]);
        Assert.Equal("07 号", byPos[new SeatPosition(0, 1, 5)]);
    }

    [Fact]
    public void DailyMode_HasNoSeatNumbersOnScreen()
    {
        // 日常座位表不画座号：与导出同一条语义，空表时一律不画。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(12, "合成名单.xlsx"));
        vm.AutoArrange();

        Assert.DoesNotContain(vm.Seats, s => s.HasSeatNumber);
    }

    [Fact]
    public void ExamMode_LeavingClearsSeatNumbers()
    {
        var vm = NewExamViewModel(12);
        Assert.Contains(vm.Seats, s => s.HasSeatNumber);

        vm.LeaveExamMode();

        Assert.DoesNotContain(vm.Seats, s => s.HasSeatNumber);
    }

    // ----------------------------- 快速换人失败不关窗（调研 P3-14） -----------------------------

    [Fact]
    public void QuickSwapByName_ReturnsFalseAndKeepsSeatsOnAmbiguousName()
    {
        // 输入「张」这类重名前缀：返回 false（浮窗据此留在原地显示提示），座位不动。
        var vm = new SeatGridViewModel();
        vm.LoadRoster(DuplicateNameRoster());
        vm.AutoArrange();

        var occupied = vm.Seats.Where(s => !s.IsEmpty).Take(1).ToList();
        var before = occupied[0].DisplayName;

        var swapped = vm.QuickSwapByName(occupied[0], "张");

        Assert.False(swapped);
        Assert.Contains("缩小范围", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(before, vm.Seats.Single(s => s.Position == occupied[0].Position).DisplayName);
    }

    [Fact]
    public void QuickSwapByName_ReturnsTrueOnSuccess()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(8, "合成名单.xlsx"));
        vm.AutoArrange();

        var source = vm.Seats.First(s => !s.IsEmpty);
        var other = vm.AllStudents.First(s => s.Name != source.DisplayName);

        Assert.True(vm.QuickSwapByName(source, other.Name));
        Assert.Equal(other.Name, vm.Seats.Single(s => s.Position == source.Position).DisplayName);
    }

    [Fact]
    public void QuickSwapByName_ReturnsFalseWhenTargetHasNoSeat()
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(8, "合成名单.xlsx"));
        vm.AutoArrange();

        // 把最后一位学生的座位清掉，让「还没有座位」这条路径可达。
        var homeless = vm.AllStudents[^1];
        vm.SelectedSeat = vm.Seats.First(s => !s.IsEmpty && s.DisplayName == homeless.Name);
        vm.ClearSelectedSeat();

        var source = vm.Seats.First(s => !s.IsEmpty);
        var swapped = vm.QuickSwapByName(source, homeless.Name);

        Assert.False(swapped);
        Assert.Contains("还没有座位", vm.StatusText, StringComparison.Ordinal);
    }

    // ----------------------------- 夹具 -----------------------------

    /// <summary>含两位「张」姓学生的名单，用来触发重名分支。</summary>
    private static RosterImportResult DuplicateNameRoster()
    {
        var students = new List<Student>
        {
            new() { Name = "张伟", Gender = Gender.Male, Number = "2026001" },
            new() { Name = "张敏", Gender = Gender.Female, Number = "2026002" },
            new() { Name = "陈思远", Gender = Gender.Male, Number = "2026003" },
            new() { Name = "李雨桐", Gender = Gender.Female, Number = "2026004" },
        };

        return new RosterImportResult("重名名单.xlsx", "重名名单.xlsx", students, Array.Empty<string>());
    }

    private static SeatGridViewModel NewExamViewModel(int count)
    {
        var vm = new SeatGridViewModel();
        vm.LoadRoster(Roster(count, "合成名单.xlsx"));
        vm.GroupCount = 4;
        vm.RowCount = 6;
        vm.ColumnCount = 2;
        vm.AutoArrange();
        vm.EnterExamMode(new ExamLayoutOptions());
        return vm;
    }

    /// <summary>一份可导回的配置：日常 4 组 × 6 排 × 2 列，前 N 个座位放人。</summary>
    private static SeatGridConfig Config(int count)
    {
        var roster = Roster(count, "配置名单.xlsx");
        var settings = new SeatChartSettings { Groups = 4, Rows = 6, Columns = 2 };
        var assignments = new Dictionary<SeatPosition, Student>();
        for (var i = 0; i < roster.Students.Count; i++)
            assignments[new SeatPosition(i / 12, (i % 12) / 2, i % 2)] = roster.Students[i];

        return new SeatGridConfig(settings, roster.Students.ToList(), assignments, new List<string>());
    }

    /// <summary>合成名单：全部虚构姓名，与 DemoRoster 同一套写法。</summary>
    private static RosterImportResult Roster(int count, string fileName)
    {
        var names = new[]
        {
            "陈思远", "李雨桐", "王浩然", "张欣怡", "刘子涵", "赵梓萱", "周俊杰", "吴梦琪",
            "徐一鸣", "孙悦", "马晨曦", "朱嘉怡", "胡博文", "郭诗涵", "何宇航", "高晓雅",
            "林书言", "罗文静", "郑凯", "梁若曦", "谢明轩", "宋佳宁", "唐子睿", "许婉清",
            "韩泽宇", "冯可欣", "邓砚青", "曹语嫣", "彭嘉树", "曾梦瑶", "萧然", "田雨欣",
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

        return new RosterImportResult(fileName, fileName, students, Array.Empty<string>());
    }
}
