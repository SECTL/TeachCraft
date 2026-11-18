using System.Reflection;
using GradeStrip.App.ViewModels;
using GradeStrip.Core.Comparison;
using GradeStrip.Core.Model;

namespace TeachCraft.Tests;

/// <summary>
/// 分晓考试会话的稳定性（第二轮审计 P2-3 / P2-4）。
///
/// 会话切换、重排、删除都会走 <c>ApplyActiveExamAndComparison</c>，它曾经把
/// 「第几场」也编进重置键：删除 / 上移其他场次会重新编号，主考看起来「换了」，
/// 老师手改的标题与筛好的班级就被冲回默认（P2-3）；二级页则相反——换到另一场
/// 考试后学生画像 / 学科分析继续显示上一场的数据，没有任何标记（P2-4）。
///
/// 走反射而不是公开 API：这些入口（MoveSessionUp / RemoveSession）本来就能直接调用，
/// 但会话列表只有导入才能填充，而导入包在 RunBusyAsync 里（无 UI 线程的测试环境会挂起）。
/// 反射只用来塞数据，断言落在公开属性上。
/// </summary>
public sealed class GradeStripSessionTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.NonPublic | BindingFlags.Instance;

    private static List<ExamSession> SessionsOf(MainViewModel vm) =>
        (List<ExamSession>)typeof(MainViewModel).GetField("_sessions", PrivateInstance)!.GetValue(vm)!;

    private static void ApplyActiveExam(MainViewModel vm) =>
        typeof(MainViewModel).GetMethod("ApplyActiveExamAndComparison", PrivateInstance)!
            .Invoke(vm, null);

    private static ExamSession CreateSession(MainViewModel vm, string path, ExamData data) =>
        (ExamSession)typeof(MainViewModel).GetMethod("CreateSession", PrivateInstance)!
            .Invoke(vm, new object[] { path, data })!;

    private static ExamData Exam(string examName, params (int No, string Name, string Class)[] students) => new()
    {
        ExamName = examName,
        Subjects = new[] { "数学" },
        Students = students.Select(s => new StudentRecord
        {
            No = s.No,
            Name = s.Name,
            ClassName = s.Class,
            Total = new SubjectScore { Score = 100 - s.No, ClassRank = s.No },
            Subjects = new Dictionary<string, SubjectScore>
            {
                ["数学"] = new SubjectScore { Score = 100 - s.No, ClassRank = s.No },
            },
        }).ToList(),
    };

    private static string TempFile(string name) =>
        Path.Combine(Path.GetTempPath(), "GradeStripSessionTests", name);

    /// <summary>三场考试、主考第三场；上移第三场会让它重新编号。</summary>
    private static MainViewModel ThreeSessions(string suffix)
    {
        var vm = new MainViewModel();
        CreateSession(vm, TempFile($"a-{suffix}.xlsx"), Exam("第一次段考", (1, "苏晚晴", "九1班")));
        CreateSession(vm, TempFile($"b-{suffix}.xlsx"), Exam("第二次段考", (1, "苏晚晴", "九1班")));
        CreateSession(vm, TempFile($"c-{suffix}.xlsx"), Exam("第三次段考", (1, "苏晚晴", "九1班")));
        vm.SetActiveSession(3);
        return vm;
    }

    [Fact]
    public void MoveOtherSessionUp_KeepsHandEditedTitleAndClassFilter()
    {
        // 主考第三场时把第二场上移：展示的考试没变，手改标题与班级筛选必须留着。
        // 这条是语义锁定（旧键在这种情况下也恰好稳定）；真正「修复前必红」的是
        // 下面删除更早场次那条——只有它会改到主考自己的编号。
        var vm = ThreeSessions("move");
        vm.TitleOverride = "九年段考 · 手改标题";
        vm.SelectedStripClass = "九1班";

        vm.MoveSessionUp(2);

        Assert.Equal("九年段考 · 手改标题", vm.TitleOverride);
        Assert.Equal("九1班", vm.SelectedStripClass);
        Assert.Equal("第三次段考", vm.ExamName);
    }

    [Fact]
    public void RemoveEarlierSession_KeepsHandEditedTitleAndClassFilter()
    {
        // 修复前必红：删除第 1 场后主考从「第 3 场」重新编号成「第 2 场」，
        // 旧键「Order:路径」把这次重新编号误判成换了考试（审计 P2-3）。
        var vm = ThreeSessions("remove");
        vm.TitleOverride = "九年段考 · 手改标题";
        vm.SelectedStripClass = "九1班";

        vm.RemoveSession(1);

        Assert.Equal("九年段考 · 手改标题", vm.TitleOverride);
        Assert.Equal("九1班", vm.SelectedStripClass);
        Assert.Equal("第三次段考", vm.ExamName);
    }

    [Fact]
    public void SwitchingToSessionWithoutCurrentStudent_ClosesDetailWithNotice()
    {
        // 修复前必红：Detail 找不到该生时什么都不做，上一场的画像继续挂在屏幕上（审计 P2-4）。
        var vm = new MainViewModel();
        CreateSession(vm, TempFile("detail-a.xlsx"),
            Exam("第一次段考", (1, "苏晚晴", "九1班"), (2, "陈屿", "九1班")));
        CreateSession(vm, TempFile("detail-b.xlsx"),
            Exam("第二次段考", (1, "顾知远", "九1班")));
        vm.SetActiveSession(1);

        vm.OpenStudent(new StudentRow { No = 2, Name = "陈屿" });
        Assert.NotNull(vm.Detail);
        Assert.Equal(2, vm.Section);

        vm.SetActiveSession(2);

        Assert.Null(vm.Detail);
        Assert.Equal(0, vm.Section);
        Assert.Contains("已回到概览", vm.Status);
    }

    [Fact]
    public void SwitchingToSessionWithoutCurrentSubject_ClosesSubjectDetail()
    {
        // 学科分析同理：新一场没有这门学科时必须回概览，不能留着旧学科的排名页。
        var vm = new MainViewModel();
        CreateSession(vm, TempFile("subject-a.xlsx"),
            Exam("第一次段考", (1, "苏晚晴", "九1班")));
        var other = Exam("第二次段考", (1, "苏晚晴", "九1班"));
        var withoutMath = new ExamData
        {
            ExamName = other.ExamName,
            Subjects = new[] { "语文" },
            Students = other.Students.Select(s => new StudentRecord
            {
                No = s.No,
                Name = s.Name,
                ClassName = s.ClassName,
                Total = s.Total,
                Subjects = new Dictionary<string, SubjectScore>
                {
                    ["语文"] = new SubjectScore { Score = 90, ClassRank = 1 },
                },
            }).ToList(),
        };
        CreateSession(vm, TempFile("subject-b.xlsx"), withoutMath);
        vm.SetActiveSession(1);

        vm.OpenSubject("数学");
        Assert.NotNull(vm.SubjectDetail);
        Assert.Equal(3, vm.Section);

        vm.SetActiveSession(2);

        Assert.Null(vm.SubjectDetail);
        Assert.Equal(0, vm.Section);
        Assert.Contains("已回到概览", vm.Status);
    }

    [Fact]
    public void SwitchingSessions_RebuildsDetailFromNewData()
    {
        // 找得到同一个学生时按新数据重建：分数必须跟着新一场走，而不是留着旧值。
        var vm = new MainViewModel();
        CreateSession(vm, TempFile("rebuild-a.xlsx"),
            Exam("第一次段考", (1, "苏晚晴", "九1班")));
        CreateSession(vm, TempFile("rebuild-b.xlsx"),
            Exam("第二次段考", (1, "苏晚晴", "九1班")));
        vm.SetActiveSession(1);

        vm.OpenStudent(new StudentRow { No = 1, Name = "苏晚晴" });
        var before = vm.Detail!.TotalText;

        vm.SetActiveSession(2);

        Assert.NotNull(vm.Detail);
        Assert.Equal("苏晚晴", vm.Detail!.Name);
        Assert.Equal(before, vm.Detail.TotalText); // 两场数据相同，但对象是重建的
        Assert.Equal(2, vm.Section);
    }

    [Fact]
    public void ReimportingSameFile_UpdatesInPlaceAndKeepsHandEditedTitle()
    {
        // 既有语义的显式锁定（不是回归）：重复导入同一份文件是「就地更新那一场」，
        // 标题与筛选保留；考试名与数据刷新。若将来要「考试名变了重新套默认标题」，
        // 那是新决策（比较 DefaultTitle），不走 examKey。
        var vm = new MainViewModel();
        var path = TempFile("reimport.xlsx");
        CreateSession(vm, path, Exam("第一次段考", (1, "苏晚晴", "九1班")));
        ApplyActiveExam(vm); // 真实流程：导入后立即应用一次，重置键落在这份文件上
        vm.TitleOverride = "手改标题";
        vm.SelectedStripClass = "九1班";

        var refreshed = CreateSession(vm, path, Exam("第一次段考（更正）", (1, "苏晚晴", "九1班")));
        ApplyActiveExam(vm);

        Assert.Single(SessionsOf(vm));
        Assert.Equal(1, refreshed.Order);
        Assert.Equal("第一次段考（更正）", vm.ExamName);
        Assert.Equal("手改标题", vm.TitleOverride);
        Assert.Equal("九1班", vm.SelectedStripClass);
    }
}
