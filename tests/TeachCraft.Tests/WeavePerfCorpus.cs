using System.Globalization;
using ClosedXML.Excel;

namespace TeachCraft.Tests;

/// <summary>
/// 「20 个班 × 9 个学科 × 694 节」合成织序工作簿语料生成器（性能基准 T2b）。
/// <para>
/// 产出结构与 <see cref="GradeWeave.Core.Import.WeaveWorkbookImporter"/> 的既有契约逐条对齐：
/// 一份 <c>.xlsx</c> 里同时有「分工表 + 确认表 + 课程表 + 会议活动备注」，导入后得到
/// 20 个班 / 9 个学科 / 62 位教师 / 694 节的 <c>WeaveProject</c>。
/// </para>
/// <list type="bullet">
/// <item>
/// 「分工表」第 1 行是标题（导入器从中解析学校 / 年级 / 学期），第 2 行表头固定为
/// 「班级 / 班主任 / 语文…劳动」，下面 20 个班级行，最后一行「课时」给各科周课时。
/// 导入器把含「课时」的那一行当**整张表的全局行**读（不是逐班一行），所以这里只写一行；
/// 写成逐班重复会让第一个班的课时数据覆盖掉全表。
/// </item>
/// <item>
/// 「确认表」表头为「序号 / 姓名 / 学科 / 兼职岗位 / 任教班级 / 学科周课时 / 联系电话」。
/// 导入器先读确认表建教师档案、再读分工表的任课格，因此 62 位教师全部在这里建档，
/// 分工表只引用同一批姓名，教师总数才正好等于参数值。
/// </item>
/// <item>
/// 「课程表」每天 10 列一组（7 节 + 课后服务 1 / 2 + 晚修），行是班级、格是「学科 + 教师」。
/// 导入器按每格开头的学科名匹配学科、按余下文本匹配教师，这些格子成为导入后的参考基线课时。
/// </item>
/// <item>「会议活动备注」写在课程表下方，导入器据此给出教研与会议建议（只进建议，不写进工程）。</item>
/// </list>
/// <para>
/// 课时规模：5 天 × 7 节 = 35 节/班，20 个班满编 700 节。真实年级工作簿里 6 班与 14 班的化学
/// 标「轮流」、没有固定任课教师，合成语料照实保留这两个缺口（2 × 3 = 6 节），于是共 694 节——
/// 与 AGENTS.md 记录的「20 班 / 62 教师 / 694 节」规模基线一致，性能数字可以直接对照既有回归。
/// 这两个班的课程表里对应的 3 个化学格留空（不写「轮流」，否则导入器会按非学科内容记提醒），
/// 因此「课程表基线的格子数 == 任课关系的课时总数」这条恒等式仍然成立。
/// </para>
/// <para>
/// 姓名池为 16 个虚构姓 × 16 个虚构双字名（256 个组合），姓氏刻意与 <c>PerfCorpus</c>（成绩表）
/// 和 <c>MeetPerfCorpus</c>（秩序册）的姓氏池不相交，三档语料的姓名因此不可能混同；
/// 学校名沿用仓库各处的合成学校「示例中学」。生成过程不使用随机数：同一组参数两次生成
/// 得到逐格一致的工作簿，生成物只写调用方给的临时目录，绝不入库。
/// </para>
/// </summary>
internal static class WeavePerfCorpus
{
    // ---------------------------------------------------------------- 公共常量

    /// <summary>生成物的文件名：两次生成落在不同目录时文件名也必须一致才能比对。</summary>
    public const string FileName = "WeavePerfWorkbook.xlsx";

    /// <summary>合成学校名（与仓库其它 fixture 一致）。</summary>
    public const string SchoolName = "示例中学";

    /// <summary>合成年级名；班级名由它推出「九（1）班」。</summary>
    public const string GradeName = "九年级";

    /// <summary>合成学期名，写在标题里供导入器解析。</summary>
    public const string TermName = "2026年秋季学期";

    /// <summary>默认班级数：与真实年级规模一致。</summary>
    public const int BaselineClasses = 20;

    /// <summary>默认教师数：9 个学科按基准分布拆开正好 62 位。</summary>
    public const int BaselineTeachers = 62;

    /// <summary>默认课时总量：20 个班 × 35 节 − 2 个班各缺 3 节化学 = 694。</summary>
    public const int BaselineHours = 694;

    /// <summary>一周五天。</summary>
    public const int Days = 5;

    /// <summary>每天七节正课（课程表里还有课后服务 1 / 2 与晚修三列，导入器不读）。</summary>
    public const int PeriodsPerDay = 7;

    // ---------------------------------------------------------------- 学科与班级

    /// <summary>
    /// 9 个学科：(名称, 周课时, 基准教师数)。
    /// 周课时合计 6+6+6+4+3+3+3+3+1 = 35 = 5 天 × 7 节，即每个班正好排满；
    /// 教师数合计 10+10+10+8+6+5+5+6+2 = 62，与 AGENTS.md 的规模基线一致。
    /// 课时与教师数的比例也贴合真实分工：语文 10 人各带 2 班、化学 6 人各带 3 班、劳动 2 人各带 10 班。
    /// </summary>
    private static readonly (string Name, int Hours, int BaselineTeachers)[] SubjectSpecs =
    [
        ("语文", 6, 10),
        ("数学", 6, 10),
        ("英语", 6, 10),
        ("物理", 4, 8),
        ("化学", 3, 6),
        ("道法", 3, 5),
        ("历史", 3, 5),
        ("体育", 3, 6),
        ("劳动", 1, 2),
    ];

    private const int ChineseIndex = 0;
    private const int MathIndex = 1;
    private const int ChemistryIndex = 4;

    /// <summary>
    /// 不开化学的班号：真实秩序册里 6 班与 14 班的化学标「轮流」、没有固定任课教师，
    /// 导入器因此不会为这两个班生成化学任课关系。合成语料照抄这两个缺口，
    /// 20 个班时课时总量才正好落在 694（= 700 − 2 × 3）。
    /// </summary>
    private static readonly int[] ChemistryGapClasses = [6, 14];

    /// <summary>课程表表头的星期文本，导入器用「(?:星期|周)([一二三四五])」识别。</summary>
    public static readonly string[] DayNames =
        ["星期一", "星期二", "星期三", "星期四", "星期五"];

    /// <summary>学科名清单，顺序即分工表的列顺序。</summary>
    public static IReadOnlyList<string> SubjectNames { get; } =
        SubjectSpecs.Select(spec => spec.Name).ToList();

    // ---------------------------------------------------------------- 姓名池

    /// <summary>
    /// 虚构姓氏池：刻意避开 <c>PerfCorpus</c>（沈陆程顾苏陈林叶温裴）与 <c>MeetPerfCorpus</c>
    /// （沈苏陈林顾韩温陆程叶许周宋秦江谢白唐齐楚燕洛岑裴祁舒慕商卫邵柏花柳闻简季常宁傅单曲詹边娄蔚衡步都麦束）
    /// 用过的姓，三档语料的姓名不可能撞车。
    /// </summary>
    private static readonly string[] Surnames =
    [
        "郑", "冯", "曹", "袁", "潘", "范", "彭", "韦",
        "方", "孟", "姚", "贺", "卢", "严", "黎", "童",
    ];

    /// <summary>虚构双字名池：与两支既有语料的名字池也不重合，且都不含导入器的教师名噪声词。</summary>
    private static readonly string[] GivenNames =
    [
        "承宇", "泽川", "书言", "观棋", "立诚", "和光", "怀瑾", "抱朴",
        "含章", "慎行", "静姝", "婉清", "望之", "亦舟", "沐言", "疏朗",
    ];

    /// <summary>取样步长；256 = 2⁸，奇数步长必然与池长互质，整池上是一轮不重复的置换。</summary>
    private const int NameStride = 37;

    /// <summary>16 个姓 × 16 个名 = 256 个互不重复的合成姓名，供测试断言「只用了这个池子」。</summary>
    public static IReadOnlyList<string> NamePool { get; } = BuildNamePool();

    /// <summary>默认规模下按建档顺序排列的 62 位教师姓名（先是 10 位语文，最后是 2 位劳动）。</summary>
    public static IReadOnlyList<string> BaselineTeacherNames { get; } =
        BuildPlan(BaselineClasses, BaselineTeachers).Teachers.Select(t => t.Name).ToList();

    // ---------------------------------------------------------------- 公共入口

    /// <summary>
    /// 在 <paramref name="directory"/> 下写一份合成年级工作簿，返回 <c>.xlsx</c> 的完整路径。
    /// 数据完全由参数决定（不用随机数），同样的参数两次调用产出逐格一致的文件。
    /// </summary>
    public static string WriteWorkbook(
        string directory, int classes = BaselineClasses, int teachers = BaselineTeachers)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("必须给出输出目录。", nameof(directory));
        if (classes < 1)
            throw new ArgumentOutOfRangeException(nameof(classes), classes, "班级数至少为 1。");
        if (teachers < SubjectSpecs.Length)
            throw new ArgumentOutOfRangeException(
                nameof(teachers), teachers, $"教师数至少为 {SubjectSpecs.Length}（每个学科至少一位）。");
        if (teachers > NamePool.Count)
            throw new ArgumentOutOfRangeException(
                nameof(teachers), teachers, $"教师数最多为 {NamePool.Count}（姓名池上限）。");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);

        var plan = BuildPlan(classes, teachers);

        using var workbook = new XLWorkbook();
        WriteStaffingSheet(workbook.AddWorksheet("分工表"), plan);
        WriteConfirmSheet(workbook.AddWorksheet("确认表"), plan);
        WriteScheduleSheet(workbook.AddWorksheet("课程表"), plan);
        workbook.SaveAs(path);

        return path;
    }

    // ---------------------------------------------------------------- 计划模型

    private sealed record CorpusTeacher(string Name, string Subject, int SubjectIndex);

    private sealed record CorpusClass(
        int Number,
        string Name,
        string HeadTeacher,
        CorpusTeacher?[] Teachers)
    {
        /// <summary>该班某学科的任课教师；null 表示这个班不开这门课。</summary>
        public CorpusTeacher? TeacherAt(int subjectIndex) => Teachers[subjectIndex];
    }

    private sealed class CorpusPlan
    {
        public required IReadOnlyList<(string Name, int Hours)> Subjects { get; init; }
        public required IReadOnlyList<CorpusTeacher> Teachers { get; init; }
        public required IReadOnlyList<CorpusClass> Classes { get; init; }
    }

    private static CorpusPlan BuildPlan(int classes, int teachers)
    {
        var teacherCounts = DistributeTeachers(teachers);

        // 教师按「学科 → 学科内序号」建档：导入器读确认表时的插入顺序就是这里的顺序，
        // 所以 t1…tN 的编号可复现。姓名按固定步长跳过取用，相邻教师不会同一姓氏连排。
        var pool = NamePool;
        var teacherList = new List<CorpusTeacher>(teachers);
        var teacherStart = new int[SubjectSpecs.Length];
        for (var subjectIndex = 0; subjectIndex < SubjectSpecs.Length; subjectIndex++)
        {
            teacherStart[subjectIndex] = teacherList.Count;
            for (var i = 0; i < teacherCounts[subjectIndex]; i++)
            {
                var name = pool[teacherList.Count * NameStride % pool.Count];
                teacherList.Add(new CorpusTeacher(
                    name, SubjectSpecs[subjectIndex].Name, subjectIndex));
            }
        }

        // 每门课的参与班级：化学在 6 班与 14 班不开。
        var participants = new int[SubjectSpecs.Length][];
        for (var subjectIndex = 0; subjectIndex < SubjectSpecs.Length; subjectIndex++)
        {
            participants[subjectIndex] = Enumerable.Range(1, classes)
                .Where(number => !(subjectIndex == ChemistryIndex && IsChemistryGap(number)))
                .ToArray();
        }

        // 每个班 × 每个学科的任课教师：按「该科参与班级里的位置」均分给该科教师。
        // 整数除法保证每个教师至少分到班、也不越出本科室的教师区间（位置 × 人数 / 班数 < 人数）。
        var assigned = new CorpusTeacher?[classes + 1][];
        for (var number = 0; number <= classes; number++)
            assigned[number] = new CorpusTeacher?[SubjectSpecs.Length];

        for (var subjectIndex = 0; subjectIndex < SubjectSpecs.Length; subjectIndex++)
        {
            var list = participants[subjectIndex];
            var count = teacherCounts[subjectIndex];
            for (var position = 0; position < list.Length; position++)
            {
                assigned[list[position]][subjectIndex] =
                    teacherList[teacherStart[subjectIndex] + position * count / list.Length];
            }
        }

        var classList = new List<CorpusClass>(classes);
        for (var number = 1; number <= classes; number++)
        {
            var teachersOfClass = assigned[number];

            // 班主任：奇数班由语文老师担任、偶数班由数学老师担任——两者都是该班任课教师，
            // 20 个班正好用到 10 位语文 + 10 位数学教师，不会为了凑班主任多造出人来。
            var head = teachersOfClass[number % 2 == 1 ? ChineseIndex : MathIndex]!;
            classList.Add(new CorpusClass(number, ClassName(number), head.Name, teachersOfClass));
        }

        return new CorpusPlan
        {
            Subjects = SubjectSpecs.Select(spec => (spec.Name, spec.Hours)).ToArray(),
            Teachers = teacherList,
            Classes = classList,
        };
    }

    /// <summary>
    /// 把 <paramref name="teachers"/> 位教师摊到 9 个学科上。
    /// 默认 62 位时直接返回基准分布（10/10/10/8/6/5/5/6/2）；别的总数先给每个学科留 1 位
    /// （学科不能没有任课教师），余下的按基准权重用最大余数法均摊，余数相同按学科顺序补齐。
    /// 这样任何合法参数下每个学科至少 1 位教师，任课分配的整数除法才不会越出本科室的教师区间。
    /// </summary>
    private static int[] DistributeTeachers(int teachers)
    {
        var weights = SubjectSpecs.Select(spec => spec.BaselineTeachers).ToArray();
        if (teachers == weights.Sum()) return (int[])weights.Clone();

        var counts = new int[weights.Length];
        Array.Fill(counts, 1);

        var spare = teachers - weights.Length;
        var weightSum = weights.Sum();
        var exact = weights.Select(weight => (double)weight * spare / weightSum).ToArray();
        var extra = exact.Select(value => (int)Math.Floor(value)).ToArray();

        var remainder = spare - extra.Sum();
        var order = Enumerable.Range(0, weights.Length)
            .OrderByDescending(index => exact[index] - extra[index])
            .ThenBy(index => index)
            .ToArray();
        for (var index = 0; index < remainder; index++)
            extra[order[index % order.Length]]++;

        for (var index = 0; index < counts.Length; index++)
            counts[index] += extra[index];

        return counts;
    }

    private static bool IsChemistryGap(int classNumber) =>
        ChemistryGapClasses.Contains(classNumber);

    private static string ClassName(int number) =>
        GradeName[..1] + "（" + number.ToString(CultureInfo.InvariantCulture) + "）班";

    private static IReadOnlyList<string> BuildNamePool()
    {
        var names = new List<string>(Surnames.Length * GivenNames.Length);
        foreach (var surname in Surnames)
        {
            foreach (var given in GivenNames)
                names.Add(surname + given);
        }

        return names;
    }

    // ---------------------------------------------------------------- 分工表

    private static void WriteStaffingSheet(IXLWorksheet sheet, CorpusPlan plan)
    {
        var columns = 2 + plan.Subjects.Count;
        var title = sheet.Range(1, 1, 1, columns);
        title.Merge();
        title.FirstCell().Value = $"{SchoolName} {GradeName} {TermName} 年级课表教师分工表";

        sheet.Cell(2, 1).Value = "班级";
        sheet.Cell(2, 2).Value = "班主任";
        for (var subjectIndex = 0; subjectIndex < plan.Subjects.Count; subjectIndex++)
            sheet.Cell(2, 3 + subjectIndex).Value = plan.Subjects[subjectIndex].Name;

        var row = 3;
        foreach (var item in plan.Classes)
        {
            sheet.Cell(row, 1).Value = item.Name;
            sheet.Cell(row, 2).Value = item.HeadTeacher;

            for (var subjectIndex = 0; subjectIndex < plan.Subjects.Count; subjectIndex++)
            {
                // 该班不开这门课时留空（化学缺口）：导入器跳过空格、不建任课关系。
                if (item.TeacherAt(subjectIndex) is { } teacher)
                    sheet.Cell(row, 3 + subjectIndex).Value = teacher.Name;
            }

            row++;
        }

        // 周课时是整张分工表的全局行，必须放在班级行下方，且只能有一行。
        sheet.Cell(row, 1).Value = "课时";
        for (var subjectIndex = 0; subjectIndex < plan.Subjects.Count; subjectIndex++)
            sheet.Cell(row, 3 + subjectIndex).Value = plan.Subjects[subjectIndex].Hours;
    }

    // ---------------------------------------------------------------- 确认表

    private static void WriteConfirmSheet(IXLWorksheet sheet, CorpusPlan plan)
    {
        string[] headers = ["序号", "姓名", "学科", "兼职岗位", "任教班级", "学科周课时", "联系电话"];
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(1, index + 1).Value = headers[index];

        var row = 2;
        foreach (var teacher in plan.Teachers)
        {
            var ordinal = row - 1;
            sheet.Cell(row, 1).Value = ordinal;
            sheet.Cell(row, 2).Value = teacher.Name;
            sheet.Cell(row, 3).Value = teacher.Subject;
            sheet.Cell(row, 4).Value = RolesOf(plan, teacher);
            sheet.Cell(row, 5).Value = ClassesOf(plan, teacher);
            sheet.Cell(row, 6).Value = plan.Subjects[teacher.SubjectIndex].Hours;
            // 电话列沿用仓库既有的合成写法（000-0000-NNNN），只占位，不承载任何真实号码。
            sheet.Cell(row, 7).Value = "000-0000-" + ordinal.ToString("0000", CultureInfo.InvariantCulture);
            row++;
        }
    }

    private static string RolesOf(CorpusPlan plan, CorpusTeacher teacher)
    {
        var roles = new List<string>();

        if (plan.Classes.Any(item => item.HeadTeacher == teacher.Name)) roles.Add("班主任");
        if (plan.Teachers.First(t => t.SubjectIndex == teacher.SubjectIndex).Name == teacher.Name)
            roles.Add("科组长");
        if (plan.Teachers[0].Name == teacher.Name) roles.Add("年级长");

        return string.Join('/', roles);
    }

    private static string ClassesOf(CorpusPlan plan, CorpusTeacher teacher) =>
        string.Join("、", plan.Classes
            .Where(item => ReferenceEquals(item.TeacherAt(teacher.SubjectIndex), teacher))
            .Select(item => item.Name));

    // ---------------------------------------------------------------- 课程表

    /// <summary>
    /// 一周课位模板：行是星期（0 = 周一），列是第 1–7 节，值是学科下标。
    /// 每学科每天不超过 2 节、主课尽量排在上午四节，且每天正好 7 节；
    /// 各学科出现次数与 <see cref="SubjectSpecs"/> 的周课时一致（合计 35）。
    /// 全班共用同一张模板——真实学校也常这么做——于是同一星期几的课型一眼可读。
    /// </summary>
    private static readonly int[][] WeekTemplate =
    [
        [0, 1, 2, 3, 0, 4, 6], // 周一：语 数 英 物 | 语 化 历
        [0, 1, 2, 3, 1, 5, 7], // 周二：语 数 英 物 | 数 道 体
        [0, 1, 2, 2, 4, 6, 7], // 周三：语 数 英 | 英 化 历 体
        [0, 1, 2, 3, 5, 6, 8], // 周四：语 数 英 物 | 道 历 劳
        [0, 1, 2, 3, 4, 5, 7], // 周五：语 数 英 物 | 化 道 体
    ];

    /// <summary>课程表下方的会议活动备注：只描述通用作息，不含任何学校或个人信息。</summary>
    private const string MeetingNoteText =
        "会议活动备注：每周科组活动安排（集备或公开课）：周二上午语文科组，下午数学科组；" +
        "周三上午英语科组，周三下午物理科组；周四上午化学科组、道法科组，下午历史科组；" +
        "周五上午体育科组、劳动科组。科组长、备课组长会议，周二6、7节。" +
        "班主任会议，周一6、7节。周五上午校办会、行政会、年级管委会。";

    private static void WriteScheduleSheet(IXLWorksheet sheet, CorpusPlan plan)
    {
        var lastColumn = 1 + Days * 10;
        sheet.Range(1, 1, 1, lastColumn).Merge().FirstCell().Value =
            $"{SchoolName} {GradeName} {TermName} 课程表";

        sheet.Cell(2, 1).Value = "班级";

        for (var day = 0; day < Days; day++)
        {
            var start = 2 + day * 10;

            // 星期标题横跨当天的 10 列（7 节 + 课后服务 1 / 2 + 晚修）。
            var dayHeader = sheet.Range(2, start, 2, start + 9);
            dayHeader.Merge();
            dayHeader.FirstCell().Value = DayNames[day];

            for (var period = 1; period <= PeriodsPerDay; period++)
                sheet.Cell(3, start + period - 1).Value = period;

            sheet.Cell(3, start + 7).Value = "课后服务1";
            sheet.Cell(3, start + 8).Value = "课后服务2";
            sheet.Cell(3, start + 9).Value = "晚修";
        }

        var row = 4;
        foreach (var item in plan.Classes)
        {
            sheet.Cell(row, 1).Value = item.Name;

            for (var day = 0; day < Days; day++)
            {
                var start = 2 + day * 10;
                for (var period = 1; period <= PeriodsPerDay; period++)
                {
                    var teacher = item.TeacherAt(WeekTemplate[day][period - 1]);
                    // 该班不开这门课（化学缺口）时这一格留空，导入器跳过、不生成基线课时。
                    if (teacher is null) continue;

                    sheet.Cell(row, start + period - 1).Value = teacher.Subject + teacher.Name;
                }
            }

            row++;
        }

        row++;
        sheet.Cell(row, 1).Value = MeetingNoteText;
    }
}
