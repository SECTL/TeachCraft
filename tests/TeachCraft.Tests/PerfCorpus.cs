using System.Globalization;
using ClosedXML.Excel;

namespace TeachCraft.Tests;

/// <summary>
/// 性能调查用的合成语料生成器（docs/performance-audit · T2a）。
///
/// 只造「格式真实、内容虚构」的成绩表：列结构与分晓的宽表识别一致（考号 / 姓名 / 班级 + 每科一列），
/// 姓名一律由固定姓氏池 + 固定双字名池组合，班级只用通用年级班号，不含任何学校名。
/// 生成物只写临时目录、按需重建，绝不入库（AGENTS.md 第十节与 T2「产物按需生成」）。
/// </summary>
internal static class PerfCorpus
{
    /// <summary>
    /// 虚构姓氏池。刻意避开「张 / 李 / 王 / 赵 / 刘」这些最大众的姓，
    /// 也避开单字名，避免合成姓名与真实姓名撞形（T2「禁止使用形似真实姓名的两字组合」）。
    /// </summary>
    private static readonly string[] Surnames =
    {
        "沈", "陆", "程", "顾", "苏", "陈", "林", "叶", "温", "裴",
    };

    /// <summary>虚构双字名池，与姓氏池组合出 100 个合成姓名。</summary>
    private static readonly string[] GivenNames =
    {
        "青禾", "明川", "知微", "望舒", "清和", "叙白", "听澜", "若初", "既明", "照野",
    };

    /// <summary>科目列顺序：考号、姓名、班级之后每科一列，共 10 科。</summary>
    public static readonly string[] SubjectPool =
    {
        "语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史", "地理", "体育",
    };

    /// <summary>表名带「汇总」，与智学网真实导出同名，顺带覆盖 <c>GetCandidateSheets</c> 的优先分支。</summary>
    public const string SheetName = "全科成绩汇总";

    /// <summary>固定文件名：两次生成落在不同目录时，文件名也必须一致才能比对。</summary>
    public const string FileName = "PerfGradeSheet.xlsx";

    /// <summary>学号起点，形如 2026010001；按学生序号递增。</summary>
    public const int FirstExamNo = 2026010001;

    /// <summary>班级数，与真实年级量级一致（3000 人 → 每班 150 人）。</summary>
    public const int ClassCount = 20;

    /// <summary>缺考比例（百分比）：约 2% 的成绩格留空，模拟真实数据里的缺考。</summary>
    private const int AbsentPercent = 2;

    /// <summary>姓氏 × 名字能产生的全部合成姓名（100 个），供测试断言「只用了这个池子」。</summary>
    public static IReadOnlyList<string> AllNames { get; } =
        Surnames.SelectMany(surname => GivenNames.Select(given => surname + given)).ToList();

    /// <summary>
    /// 写一份合成成绩表到 <paramref name="directory"/>，返回 .xlsx 的绝对路径。
    /// 数据完全由 <c>new Random(20261110 + students)</c> 驱动，同参数重复调用得到逐格一致的文件。
    /// </summary>
    public static string WriteGradeSheet(string directory, int students = 3000, int subjects = 10)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("必须给出输出目录。", nameof(directory));
        if (students <= 0)
            throw new ArgumentOutOfRangeException(nameof(students), students, "学生数必须大于 0。");
        if (subjects <= 0 || subjects > SubjectPool.Length)
            throw new ArgumentOutOfRangeException(
                nameof(subjects), subjects, $"科目数必须在 1…{SubjectPool.Length} 之间。");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);

        // 种子只由学生数决定：同规模的可复现性可断言，不同规模之间也能各自稳定。
        var random = new Random(20261110 + students);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SheetName);

        sheet.Cell(1, 1).Value = "考号";
        sheet.Cell(1, 2).Value = "姓名";
        sheet.Cell(1, 3).Value = "班级";
        for (var s = 0; s < subjects; s++)
            sheet.Cell(1, 4 + s).Value = SubjectPool[s];

        for (var i = 0; i < students; i++)
        {
            var row = i + 2;

            // 班级按行连续排（真实导出的汇总表就是一个班接一个班），20 个班尽量均分；
            // 姓名按全表轮转，避免「同一个班全是同一个姓」这种一眼假的分布。
            var classIndex = (int)((long)i * ClassCount / students) + 1;
            var name = AllNames[i % AllNames.Count];

            // 考号按文本写：避免任何阅读器把它当浮点再转出科学计数法
            sheet.Cell(row, 1).Value = (FirstExamNo + i).ToString(CultureInfo.InvariantCulture);
            sheet.Cell(row, 2).Value = name;
            sheet.Cell(row, 3).Value = "九年级" + classIndex.ToString(CultureInfo.InvariantCulture) + "班";

            for (var s = 0; s < subjects; s++)
            {
                // 缺考留空单元格，而不是写 0 或「缺考」文本：分晓把空值当作不算分
                if (random.Next(100) < AbsentPercent) continue;

                // 注意随机数的消费顺序必须稳定：缺考分支只消费一次 Next，成绩分支消费两次
                sheet.Cell(row, 4 + s).Value = random.Next(0, 101);
            }
        }

        workbook.SaveAs(path);
        return path;
    }
}
