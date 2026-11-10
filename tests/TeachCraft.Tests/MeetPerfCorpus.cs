using System.Globalization;
using ClosedXML.Excel;

namespace TeachCraft.Tests;

/// <summary>
/// 「60 个班」合成运动会秩序册语料生成器（性能基准 T2 用）。
/// <para>
/// 产出结构与真实秩序册一致：一份 <c>.xlsx</c> 里同时有「代表队名单 + 竞赛日程 + 项目分组表」，
/// 由 <see cref="MeetRoster.Core.Import.OrderBookImporter"/> 一次读成一个 <c>MeetProject</c>。
/// 三张表的排版约定都照导入器的既有契约写：
/// </para>
/// <list type="bullet">
/// <item>「代表队名单」按班分块，块头 A 列写「{年级} {班号}班」，块内先给
/// 「领队 / 教练 / 工作人员」，再用「男子 / 女子」小标题分两段，每人一对「号码布 + 姓名」占两列
/// （后续行的 A 列留空，导入器据此沿用上一段的性别）。</item>
/// <item>「竞赛日程」按「日期 + 上午/下午 + 径赛/田赛/趣味赛」分节，节内每行是
/// 序号 / 组别 / 性别 / 比赛项目 / 赛次 / 人数 / 组数 / 录取 / 时间。</item>
/// <item>「项目分组表」每个项目一个块：块头给组别、性别+项目、赛次，下面是
/// 「第 N 组」道次行、号码布行、姓名行、班级行。**一页 8 道**；人数超过 8 的项目
/// 按 8 人一组分道次，长跑（800 米 / 1000 米）保持单组跨页——真实秩序册就是这样，
/// 导入器的 <c>ReadIndividualGroupPages</c> 专为读这种跨页形态而写。
/// 班级列**只写「N班」**，年级由块头给出，导入器负责拼成全名。</item>
/// </list>
/// <para>
/// 姓名池共 50 个虚构姓 × 60 个虚构双字名 = 3000 个互不重复的组合；默认 60 班 × 30 人
/// 实际取用 1800 个（步长 181 与 3000 互质，取的是一整轮置换里的前 1800 个，两两不重复）。
/// 学校名固定为仓库各处的合成学校「示例中学」，不含任何真实学生 / 教师 / 学校信息。
/// 生成过程不用随机数：同一组参数两次生成得到同一份名单与同一份分组。
/// </para>
/// </summary>
public static class MeetPerfCorpus
{
    /// <summary>生成物的文件名（导入器会从中推出工程标题「示例中学田径运动会」）。</summary>
    public const string FileName = "示例中学田径运动会秩序册.xlsx";

    /// <summary>合成学校名（与仓库其它 fixture 一致）。</summary>
    public const string SchoolName = "示例中学";

    /// <summary>每个班的运动员人数（男 15 + 女 15）。</summary>
    public const int AthletesPerClass = 30;

    /// <summary>分组表每页的道数：真实秩序册按 8 道一页排版。</summary>
    public const int LanesPerPage = 8;

    private static readonly string[] Grades = ["七年级", "八年级", "九年级"];

    // ---------------------------------------------------------------- 公共入口

    /// <summary>
    /// 在 <paramref name="directory"/> 下写一份合成秩序册，返回 <c>.xlsx</c> 的完整路径。
    /// 同样的参数两次调用会产出可复现的工作簿：姓名按固定顺序取用，分组只依赖列表下标。
    /// </summary>
    public static string WriteOrderBook(string directory, int classes = 60)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("需要给出一个目录来写入秩序册。", nameof(directory));
        if (classes < 1)
            throw new ArgumentOutOfRangeException(nameof(classes), classes, "班级数至少为 1。");

        Directory.CreateDirectory(directory);

        var rosterClasses = BuildClasses(classes);
        var pools = rosterClasses
            .GroupBy(c => c.Grade)
            .Select(g => new GradePool(g.Key, g.ToList()))
            .ToList();

        // 先一次性规划出「哪个项目、哪些人、分几组、每组几道」，
        // 日程与分组表共用同一份规划，保证两边的「人数」永远对得上。
        var plan = Plan(pools);

        var path = Path.Combine(directory, FileName);
        using var workbook = new XLWorkbook();
        WriteRosterSheet(workbook.AddWorksheet("代表队名单"), rosterClasses);
        WriteScheduleSheet(workbook.AddWorksheet("竞赛日程"), plan);
        WriteGroupingSheet(workbook.AddWorksheet("项目分组表"), plan);
        workbook.SaveAs(path);

        return path;
    }

    // ---------------------------------------------------------------- 名册模型

    private sealed record CorpusAthlete(string Name, string Bib);

    private sealed record CorpusClass(
        string Grade,
        int Number,
        IReadOnlyList<CorpusAthlete> Males,
        IReadOnlyList<CorpusAthlete> Females)
    {
        public string Name => Grade + Number + "班";

        public int Count => Males.Count + Females.Count;
    }

    /// <summary>一个年级的男女运动员池：按固定顺序轮流分配项目，保证可复现。</summary>
    private sealed class GradePool
    {
        private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _cursors = new(StringComparer.Ordinal);

        public GradePool(string grade, List<CorpusClass> classes)
        {
            Grade = grade;
            Classes = classes;
            // 按「第几人」为外层、班级为内层交错入池：顺序取人时就会在班级间轮转，
            // 项目因此摊到全年级每个班上，而不是只落在头几个班。
            var perGender = classes.Count == 0 ? 0 : classes[0].Males.Count;
            for (var slot = 0; slot < perGender; slot++)
            {
                foreach (var item in classes)
                {
                    if (slot < item.Males.Count) Males.Add((item, item.Males[slot]));
                    if (slot < item.Females.Count) Females.Add((item, item.Females[slot]));
                }
            }
        }

        public string Grade { get; }

        public List<CorpusClass> Classes { get; }

        public List<(CorpusClass Class, CorpusAthlete Athlete)> Males { get; } = [];

        public List<(CorpusClass Class, CorpusAthlete Athlete)> Females { get; } = [];

        /// <summary>
        /// 为一项个人项目挑人：同一项内不重复，每人最多两个单项（与报名规则一致），
        /// 按性别池顺序轮转。池子用尽时从头再取，因此结果只取决于调用顺序。
        /// </summary>
        public List<(CorpusClass Class, CorpusAthlete Athlete)> Pick(string gender, int count)
        {
            var pool = gender == "女子" ? Females : Males;
            var picked = new List<(CorpusClass Class, CorpusAthlete Athlete)>();
            if (pool.Count == 0 || count <= 0) return picked;

            var cursor = _cursors.TryGetValue(gender, out var value) ? value : 0;
            var takenInSlot = new HashSet<string>(StringComparer.Ordinal);
            for (var guard = 0; picked.Count < count && guard < pool.Count * 4; guard++)
            {
                var item = pool[cursor % pool.Count];
                cursor++;
                if (!takenInSlot.Add(item.Athlete.Bib)) continue;

                var used = _used.TryGetValue(item.Athlete.Bib, out var times) ? times : 0;
                if (used >= 2)
                {
                    takenInSlot.Remove(item.Athlete.Bib);
                    continue;
                }

                _used[item.Athlete.Bib] = used + 1;
                picked.Add(item);
            }

            _cursors[gender] = cursor;
            return picked;
        }

        /// <summary>团体项目按班报名，取该年级靠前的若干个班。</summary>
        public List<CorpusClass> PickClasses(int count) =>
            Classes.Take(Math.Min(count, Classes.Count)).ToList();
    }

    // ---------------------------------------------------------------- 项目规划

    /// <summary>报名表 / 秩序册里的一个比赛时段规格。</summary>
    /// <param name="EntryCount">个人项目的参赛人数；团体项目忽略。</param>
    /// <param name="TeamClasses">团体项目的参赛班数；个人项目为 0。</param>
    /// <param name="SingleHeat">true 表示整个项目只排一组（长跑按真实秩序册的做法跨页续排）。</param>
    private sealed record SlotSpec(
        string Gender,
        string EventName,
        string Stage,
        int EntryCount = 0,
        int TeamClasses = 0,
        bool SingleHeat = false)
    {
        public bool IsTeam => TeamClasses > 0;
    }

    /// <summary>日程里的一节：同一日期、同一上下午、同一场地分类。</summary>
    private sealed record SectionSpec(string DateText, string Period, string Section, SlotSpec[] Slots);

    private sealed record HeatPlan(int Number, IReadOnlyList<PlannedEntry> Entries);

    private sealed record PlannedEntry(string Bib, string Name, int ClassNumber);

    private sealed record SlotPlan(
        string Grade,
        string Gender,
        string EventName,
        string Stage,
        bool IsTeam,
        IReadOnlyList<HeatPlan> Heats)
    {
        public int EntryCount => Heats.Sum(h => h.Entries.Count);

        /// <summary>分组表块头 C 列的写法：「女子100米」「混合4x100米接力」。</summary>
        public string FullEventName => Gender + EventName;
    }

    /// <summary>
    /// 赛程骨架：两个比赛日 × 上午/下午 × 径赛/田赛/趣味赛。
    /// 个人项目的人数 × 年级数不能超过「人数池 × 每人两个单项」的上限，
    /// 否则分组表会在后面几项悄悄取不满人。
    /// </summary>
    private static readonly SectionSpec[] Sections =
    [
        new("2026-11-11", "上午", "径赛",
        [
            new("男子", "100米", "预赛", EntryCount: 64),
            new("女子", "100米", "预赛", EntryCount: 64),
            new("男子", "400米", "决赛", EntryCount: 56),
            new("女子", "400米", "决赛", EntryCount: 56),
            // 长跑按真实秩序册的做法排成单组跨页：24 人 = 3 页，续页重新给一行道次。
            new("男子", "1000米", "决赛", EntryCount: 24, SingleHeat: true),
            new("女子", "800米", "决赛", EntryCount: 24, SingleHeat: true),
        ]),
        new("2026-11-11", "下午", "田赛",
        [
            new("男子", "跳高", "决赛", EntryCount: 56),
            new("女子", "跳高", "决赛", EntryCount: 56),
            new("男子", "实心球", "决赛", EntryCount: 56),
            new("女子", "实心球", "决赛", EntryCount: 56),
            new("男子", "立定跳远", "决赛", EntryCount: 56),
            new("女子", "立定跳远", "决赛", EntryCount: 56),
        ]),
        new("2026-11-12", "上午", "径赛",
        [
            new("男子", "200米", "预赛", EntryCount: 64),
            new("女子", "200米", "预赛", EntryCount: 64),
            new("男子", "100米", "决赛", EntryCount: 8),
            new("女子", "100米", "决赛", EntryCount: 8),
            new("男子", "200米", "决赛", EntryCount: 8),
            new("女子", "200米", "决赛", EntryCount: 8),
        ]),
        new("2026-11-12", "下午", "趣味赛",
        [
            new("混合", "一分钟跳绳", "决赛", TeamClasses: 20),
        ]),
        new("2026-11-12", "下午", "径赛",
        [
            new("混合", "4x100米接力", "决赛", TeamClasses: 20),
            new("混合", "20*50米男女混合接力", "决赛", TeamClasses: 20),
        ]),
    ];

    /// <summary>一节的完整规划：节标题 + 节内按「年级 × 项目」排好的时段。</summary>
    private sealed record SectionPlan(SectionSpec Section, IReadOnlyList<SlotPlan> Slots);

    private static List<SectionPlan> Plan(IReadOnlyList<GradePool> pools)
    {
        var plan = new List<SectionPlan>(Sections.Length);
        foreach (var section in Sections)
        {
            var slots = new List<SlotPlan>(pools.Count * section.Slots.Length);
            foreach (var pool in pools)
            {
                foreach (var spec in section.Slots)
                    slots.Add(PlanSlot(pool, spec));
            }

            plan.Add(new SectionPlan(section, slots));
        }

        return plan;
    }

    private static SlotPlan PlanSlot(GradePool pool, SlotSpec spec)
    {
        if (spec.IsTeam)
        {
            // 团体项目按班列队、不点名队员；每 8 个班一页。
            var classes = pool.PickClasses(spec.TeamClasses);
            var heats = new List<HeatPlan>();
            for (var start = 0; start < classes.Count; start += LanesPerPage)
            {
                var page = classes
                    .Skip(start)
                    .Take(LanesPerPage)
                    .Select(c => new PlannedEntry(string.Empty, string.Empty, c.Number))
                    .ToList();
                heats.Add(new HeatPlan(start / LanesPerPage + 1, page));
            }

            return new SlotPlan(pool.Grade, spec.Gender, spec.EventName, spec.Stage, true, heats);
        }

        var picked = pool.Pick(spec.Gender, spec.EntryCount);
        var entries = picked
            .Select(p => new PlannedEntry(p.Athlete.Bib, p.Athlete.Name, p.Class.Number))
            .ToList();

        var individualHeats = new List<HeatPlan>();
        var heatSize = spec.SingleHeat ? int.MaxValue : LanesPerPage;
        for (var start = 0; start < entries.Count; start += heatSize)
        {
            var page = entries.Skip(start).Take(heatSize).ToList();
            individualHeats.Add(new HeatPlan(start / heatSize + 1, page));
        }

        return new SlotPlan(pool.Grade, spec.Gender, spec.EventName, spec.Stage, false, individualHeats);
    }

    // ---------------------------------------------------------------- 姓名池

    // 虚构姓氏池：单字姓，与双字名池组合后可得到 50 × 60 = 3000 个互不重复的姓名。
    private static readonly string[] Surnames =
    [
        "沈", "苏", "陈", "林", "顾", "韩", "温", "陆", "程", "叶",
        "许", "周", "宋", "秦", "江", "谢", "白", "唐", "齐", "楚",
        "燕", "洛", "岑", "裴", "祁", "舒", "慕", "商", "卫", "邵",
        "柏", "花", "柳", "闻", "简", "季", "常", "宁", "傅", "单",
        "曲", "詹", "边", "娄", "蔚", "衡", "步", "都", "麦", "束",
    ];

    // 虚构双字名池：诗画意象的两字组合，与仓库既有合成姓名（苏晚晴 / 陈屿 / 顾知远）同一风格。
    private static readonly string[] GivenNames =
    [
        "晚晴", "见夏", "知远", "明川", "映棠", "临溪", "予安", "听澜", "知雨", "之遥",
        "青禾", "知微", "以宁", "叙白", "观澜", "行舟", "望舒", "疏桐", "沐白", "清让",
        "若谷", "星野", "拾光", "云开", "竹青", "砚秋", "松寒", "月白", "染墨", "溪亭",
        "帆影", "栀夏", "鹿鸣", "澄川", "山晓", "岚烟", "序秋", "棠溪", "沐辰", "砚清",
        "榆川", "禾风", "汀兰", "澜舟", "疏影", "越川", "榕白", "星禾", "岚川", "泽秋",
        "南枝", "北亭", "拾月", "渡舟", "听雪", "栖迟", "知秋", "望川", "归远", "明溪",
    ];

    // 领队 / 教练 / 工作人员用的人名，同样为虚构。
    private static readonly string[] StaffNames =
    [
        "沈青禾", "韩知微", "顾知远", "程叙白", "温以宁", "陆明川",
        "苏映棠", "叶临溪", "裴知让", "商晚舟", "洛听白", "楚言蹊",
    ];

    /// <summary>
    /// 姓名池：50 个姓 × 60 个名 = 3000 个互不重复的组合。
    /// <para>
    /// 取用时按步长 181 跳着取（181 与 3000 互质，因此在整池上是一轮完整置换）。
    /// 默认 60 班 × 30 人 = 1800 次取用，落在这轮置换的前 1800 个位置上，两两不重复；
    /// 池里后 1200 个组合在默认规模下用不到。
    /// </para>
    /// <para>
    /// 为什么叠一层步长而不是顺序取：顺序取时相邻姓名只差姓氏的位移，
    /// 一个班里会出现整排「X 青禾」这种同尾字连排。跳着取能让连续 30 个名字
    /// 姓氏各不相同、名字也各不相同。步长固定，所以两次生成拿到的是同一串名字。
    /// </para>
    /// </summary>
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

    /// <summary>姓名池的取样步长；与池长 3000 互质，保证一整轮不重名。</summary>
    private const int NameStride = 181;

    /// <summary>把 <paramref name="classes"/> 个班摊到三个年级上（先按每级上限均分，末尾年级少几个班）。</summary>
    private static List<CorpusClass> BuildClasses(int classes)
    {
        var names = BuildNamePool();
        var perGrade = (classes + Grades.Length - 1) / Grades.Length;
        var result = new List<CorpusClass>(classes);

        for (var index = 0; index < classes; index++)
        {
            var grade = Grades[Math.Min(index / perGrade, Grades.Length - 1)];
            var number = index % perGrade + 1;

            var males = new List<CorpusAthlete>(AthletesPerClass / 2);
            var females = new List<CorpusAthlete>(AthletesPerClass / 2);
            for (var slot = 0; slot < AthletesPerClass; slot++)
            {
                var ordinal = index * AthletesPerClass + slot;
                var name = names[ordinal * NameStride % names.Count];
                // 号码布全局唯一：班级序号 × 100 + 班内序号，与真实秩序册的四位号码同形。
                var bib = ((index + 1) * 100 + slot + 1).ToString("D4", CultureInfo.InvariantCulture);
                var athlete = new CorpusAthlete(name, bib);
                if (slot < AthletesPerClass / 2) males.Add(athlete);
                else females.Add(athlete);
            }

            result.Add(new CorpusClass(grade, number, males, females));
        }

        return result;
    }

    // ---------------------------------------------------------------- 代表队名单

    private static void WriteRosterSheet(IXLWorksheet sheet, IReadOnlyList<CorpusClass> classes)
    {
        sheet.Cell(1, 1).Value = "代表队名单";

        var row = 3;
        for (var index = 0; index < classes.Count; index++)
        {
            var item = classes[index];
            // 块头：导入器用 ^(七|八|九|高一|高二|高三)年级?\s*(\d+)\s*班 匹配，年级与班号之间可以有空格。
            sheet.Cell(row, 1).Value = $"{item.Grade} {item.Number}班 ({item.Count})人";
            row++;

            sheet.Cell(row, 1).Value = "领队: ";
            sheet.Cell(row, 2).Value = StaffNames[index % StaffNames.Length];
            row++;

            sheet.Cell(row, 1).Value = "教练: ";
            sheet.Cell(row, 2).Value = StaffNames[(index + 5) % StaffNames.Length];
            row++;

            sheet.Cell(row, 1).Value = "工作人员: ";
            sheet.Cell(row, 2).Value = StaffNames[(index + 9) % StaffNames.Length];
            row++;

            row = WriteGenderSection(sheet, row, "男子", item.Males);
            row = WriteGenderSection(sheet, row, "女子", item.Females);
        }
    }

    /// <summary>写一段「男子 / 女子」运动员：每行 4 对「号码布 + 姓名」，A 列只有首行写性别。</summary>
    private static int WriteGenderSection(
        IXLWorksheet sheet, int row, string gender, IReadOnlyList<CorpusAthlete> athletes)
    {
        for (var start = 0; start < athletes.Count; start += 4)
        {
            if (start == 0) sheet.Cell(row, 1).Value = gender;
            for (var offset = 0; offset < 4 && start + offset < athletes.Count; offset++)
            {
                var athlete = athletes[start + offset];
                sheet.Cell(row, 2 + offset * 2).Value = athlete.Bib;
                sheet.Cell(row, 3 + offset * 2).Value = athlete.Name;
            }

            row++;
        }

        return row;
    }

    // ---------------------------------------------------------------- 竞赛日程

    private static void WriteScheduleSheet(IXLWorksheet sheet, IReadOnlyList<SectionPlan> plan)
    {
        sheet.Cell(1, 1).Value = SchoolName + "田径运动会";

        var row = 3;
        foreach (var sectionPlan in plan)
        {
            var section = sectionPlan.Section;
            // 节标题：导入器用「日期 + 上午/下午 + 径赛/田赛/趣味赛」识别，三者缺一不可。
            sheet.Cell(row, 1).Value = $"{section.DateText}  {section.Period}  {section.Section}";
            row += 2;
            WriteScheduleHeader(sheet, row);
            row++;

            var clock = new TimeOnly(section.Period == "上午" ? 8 : 14, 30);
            var sequence = 1;

            foreach (var slot in sectionPlan.Slots)
            {
                sheet.Cell(row, 1).Value = sequence++;
                sheet.Cell(row, 2).Value = slot.Grade;
                sheet.Cell(row, 3).Value = slot.Gender;
                sheet.Cell(row, 4).Value = slot.EventName;
                sheet.Cell(row, 5).Value = slot.Stage;
                sheet.Cell(row, 6).Value = slot.EntryCount;
                sheet.Cell(row, 7).Value = Math.Max(1, slot.Heats.Count);
                sheet.Cell(row, 8).Value = slot.IsTeam ? 8 : 7;
                sheet.Cell(row, 9).Value = clock.ToString("HH:mm", CultureInfo.InvariantCulture);
                row++;

                clock = clock.AddMinutes(8);
            }
        }
    }

    private static void WriteScheduleHeader(IXLWorksheet sheet, int row)
    {
        string[] heads = ["序号", "组别", "性别", "比赛项目", "赛次", "人数", "组数", "录取", "时间"];
        for (var i = 0; i < heads.Length; i++)
            sheet.Cell(row, 1 + i).Value = heads[i];
    }

    // ---------------------------------------------------------------- 项目分组表

    private static void WriteGroupingSheet(IXLWorksheet sheet, IReadOnlyList<SectionPlan> plan)
    {
        sheet.Cell(1, 1).Value = "项目分组表";
        var row = 2;

        // 顺序必须与竞赛日程完全一致：导入器按「年级|性别|项目|赛次」把块挂到时段上。
        foreach (var slot in plan.SelectMany(s => s.Slots))
        {
            if (slot.IsTeam) WriteTeamBlock(sheet, ref row, slot);
            else WriteIndividualBlock(sheet, ref row, slot);
        }
    }

    /// <summary>
    /// 个人项目块：块头 / 道次 / 号码布 / 姓名 / 班级。一组超过 8 道时在续页重新给一行道次
    /// （第 9 道…），这正是 <c>ReadIndividualGroupPages</c> 要读的跨页形态。
    /// </summary>
    private static void WriteIndividualBlock(IXLWorksheet sheet, ref int row, SlotPlan slot)
    {
        sheet.Cell(row, 1).Value = slot.Grade;
        sheet.Cell(row, 3).Value = slot.FullEventName;
        sheet.Cell(row, 6).Value = slot.Stage;
        sheet.Cell(row, 7).Value =
            $"{slot.EntryCount}人/{Math.Max(1, slot.Heats.Count)}组/取 7 名";
        row++;

        foreach (var heat in slot.Heats)
        {
            for (var pageStart = 0; pageStart < heat.Entries.Count; pageStart += LanesPerPage)
            {
                var page = heat.Entries.Skip(pageStart).Take(LanesPerPage).ToList();

                // 首行带「第 N 组」，续页只有道次（A 列为空）——导入器据此判断这是同一组的下一页。
                sheet.Cell(row, 1).Value = pageStart == 0 ? $"第 {heat.Number} 组" : string.Empty;
                for (var i = 0; i < page.Count; i++)
                    sheet.Cell(row, 2 + i).Value = pageStart + i + 1;
                row++;

                for (var i = 0; i < page.Count; i++)
                    sheet.Cell(row, 2 + i).Value = page[i].Bib;
                row++;

                for (var i = 0; i < page.Count; i++)
                    sheet.Cell(row, 2 + i).Value = page[i].Name;
                row++;

                // 班级列只写「N班」：年级由块头给出，导入器拼成全名。
                for (var i = 0; i < page.Count; i++)
                    sheet.Cell(row, 2 + i).Value = page[i].ClassNumber + "班";
                row++;
            }
        }
    }

    /// <summary>团体项目块：按班列队、不点名队员（「第 N 组」+ 道次行 + 班级行）。</summary>
    private static void WriteTeamBlock(IXLWorksheet sheet, ref int row, SlotPlan slot)
    {
        sheet.Cell(row, 1).Value = slot.Grade;
        sheet.Cell(row, 3).Value = slot.FullEventName;
        sheet.Cell(row, 6).Value = slot.Stage;
        sheet.Cell(row, 7).Value =
            $"{slot.EntryCount}队/{Math.Max(1, slot.Heats.Count)}组/取 8 名";
        row++;

        foreach (var heat in slot.Heats)
        {
            sheet.Cell(row, 1).Value = $"第 {heat.Number} 组";
            for (var i = 0; i < heat.Entries.Count; i++)
                sheet.Cell(row, 2 + i).Value = i + 1;
            row++;

            for (var i = 0; i < heat.Entries.Count; i++)
                sheet.Cell(row, 2 + i).Value = heat.Entries[i].ClassNumber + "班";
            row++;
        }
    }
}
