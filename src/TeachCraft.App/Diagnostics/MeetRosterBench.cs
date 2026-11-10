using ClosedXML.Excel;
using MeetRoster.Core.Export;
using MeetRoster.Core.Import;
using MeetRoster.Core.Models;

namespace TeachCraft.App.Diagnostics;

/// <summary>
/// 竞名业务基准（T3）：把「60 班秩序册导入 → 导出模型构建 → PDF 分页 → 导出预览」
/// 四个真实步骤各自计时，结果进 <see cref="PerfBench"/> 的同一张表。
/// <para>
/// 为什么在 App 层：语料生成与这些 Core 调用都要能直接引用；放在测试项目里
/// <c>--perf-bench</c> 就拿不到（发布产物不含测试程序集）。
/// </para>
/// <para>
/// 数据全部为合成：虚构校名「示例中学」、虚构姓名。生成到临时目录，跑完即删。
/// </para>
/// </summary>
internal static class MeetRosterBench
{
    private const int Classes = 60;
    private const int AthletesPerClass = 30;

    private static readonly string[] Surnames =
    {
        "商", "洛", "岑", "裴", "祁", "舒", "慕", "卫", "邵", "柏",
        "柳", "闻", "简", "季", "常", "宁", "傅", "曲", "詹", "边",
    };

    private static readonly string[] GivenNames =
    {
        "砚清", "听雪", "惊蛰", "知秋", "望川", "归远", "明溪", "南枝",
        "北亭", "拾月", "渡舟", "疏桐", "沐白", "清让", "若谷", "星野",
    };

    /// <summary>把四项基准登记到 <see cref="PerfBench"/>；每项都自带语料生成，互不依赖。</summary>
    public static void Register()
    {
        string? corpus = null;

        PerfBench.BusinessBenchmarks.Add(("竞名 · 生成 60 班语料", () =>
        {
            var dir = NewDir();
            corpus = WriteOrderBook(dir);
        }, 1));

        PerfBench.BusinessBenchmarks.Add(("竞名 · 秩序册导入", () =>
        {
            if (corpus is null) return;
            OrderBookImporter.Import(corpus);
        }, 3));

        PerfBench.BusinessBenchmarks.Add(("竞名 · 导出模型构建", () =>
        {
            if (corpus is null) return;
            var project = OrderBookImporter.Import(corpus).Project;
            MeetExportBuilder.Build(project, new MeetExportOptions());
        }, 3));

        // 预览只要第 1 页——这正是导出设置弹窗每次改设置都走的那条路。
        PerfBench.BusinessBenchmarks.Add(("竞名 · 预览首页（导出弹窗路径）", () =>
        {
            if (corpus is null) return;
            var project = OrderBookImporter.Import(corpus).Project;
            var summaries = MeetExportBuilder.Build(project, new MeetExportOptions());
            MeetPdfExporter.RenderPreviewPages(project, summaries, new MeetExportOptions(), 1);
        }, 5));
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.PerfBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>合成秩序册：三张表按 OrderBookImporter 的读取契约排版。</summary>
    private static string WriteOrderBook(string directory)
    {
        var path = Path.Combine(directory, "示例中学田径运动会秩序册.xlsx");
        var grades = new[] { "七年级", "八年级", "九年级" };
        var perGrade = (Classes + grades.Length - 1) / grades.Length;

        using var wb = new XLWorkbook();

        // ---- 代表队名单 ----
        var roster = wb.AddWorksheet("代表队名单");
        var row = 1;
        roster.Cell(row++, 1).Value = "示例中学田径运动会";
        roster.Cell(row++, 1).Value = "代表队名单";
        row++;

        var index = 0;
        for (var c = 0; c < Classes; c++)
        {
            var grade = grades[Math.Min(c / perGrade, grades.Length - 1)];
            var number = c % perGrade + 1;

            roster.Cell(row++, 1).Value = $"{grade} {number}班";
            roster.Cell(row, 1).Value = "领队";
            roster.Cell(row, 2).Value = $"领队{c + 1}";
            roster.Cell(row, 3).Value = "教练";
            roster.Cell(row, 4).Value = $"教练{c + 1}";
            row += 2;

            foreach (var gender in new[] { "男子", "女子" })
            {
                roster.Cell(row++, 1).Value = gender;
                for (var a = 0; a < AthletesPerClass / 2; a++)
                {
                    var name = Surnames[index % Surnames.Length] +
                               GivenNames[(index / Surnames.Length) % GivenNames.Length];
                    // 导入器从第 2 列起按「号码在偶数列、姓名在奇数列」两两读，
                    // A 列留给性别小标题 / 领队这类行标签。写错列的话整班会被判成没有名单。
                    roster.Cell(row, 2).Value = 4000 + index;
                    roster.Cell(row, 3).Value = name;
                    row++;
                    index++;
                }
            }
        }

        // ---- 竞赛日程 ----
        var schedule = wb.AddWorksheet("竞赛日程");
        var srow = 1;
        schedule.Cell(srow++, 1).Value = "竞赛日程";
        foreach (var (date, half, kind) in new[]
                 {
                     ("2026-11-11", "上午", "径赛"), ("2026-11-11", "下午", "田赛"),
                     ("2026-11-12", "上午", "径赛"), ("2026-11-12", "下午", "趣味赛"),
                 })
        {
            schedule.Cell(srow++, 1).Value = $"{date} {half} {kind}";
            schedule.Cell(srow, 1).Value = "组别";
            schedule.Cell(srow, 2).Value = "性别";
            schedule.Cell(srow, 3).Value = "项目";
            schedule.Cell(srow, 4).Value = "赛次";
            schedule.Cell(srow, 5).Value = "人数";
            schedule.Cell(srow, 6).Value = "组数";
            schedule.Cell(srow, 7).Value = "录取";
            schedule.Cell(srow, 8).Value = "时间";
            srow++;

            foreach (var grade in grades)
            {
                foreach (var (gender, ev) in new[]
                         {
                             ("男子", "100米"), ("女子", "100米"),
                             ("男子", "800米"), ("女子", "800米"),
                         })
                {
                    schedule.Cell(srow, 1).Value = grade;
                    schedule.Cell(srow, 2).Value = gender;
                    schedule.Cell(srow, 3).Value = ev;
                    schedule.Cell(srow, 4).Value = "预决赛";
                    schedule.Cell(srow, 5).Value = perGrade * 2;
                    schedule.Cell(srow, 6).Value = Math.Max(1, perGrade * 2 / 8);
                    schedule.Cell(srow, 7).Value = "取 7 名";
                    schedule.Cell(srow, 8).Value = "08:30";
                    srow++;
                }
            }
        }

        // ---- 项目分组表 ----
        var grouping = wb.AddWorksheet("项目分组表");
        var grow = 1;
        grouping.Cell(grow++, 1).Value = "项目分组表";
        var bibIndex = 0;
        foreach (var grade in grades)
        {
            foreach (var gender in new[] { "男子", "女子" })
            {
                foreach (var ev in new[] { "100米", "800米" })
                {
                    grouping.Cell(grow, 1).Value = grade;
                    grouping.Cell(grow, 3).Value = gender + ev;
                    grouping.Cell(grow, 6).Value = "预决赛";
                    grouping.Cell(grow, 7).Value = $"{perGrade * 2}人/2组/取 7 名";
                    grow++;

                    for (var heat = 1; heat <= 2; heat++)
                    {
                        grouping.Cell(grow, 1).Value = $"第 {heat} 组";
                        for (var lane = 0; lane < 8; lane++)
                            grouping.Cell(grow, 2 + lane).Value = lane + 1;
                        grow++;

                        for (var lane = 0; lane < 8; lane++)
                        {
                            var n = bibIndex % (Classes * AthletesPerClass);
                            grouping.Cell(grow, 2 + lane).Value = 4000 + n;
                        }
                        grow++;

                        for (var lane = 0; lane < 8; lane++)
                        {
                            var n = bibIndex % (Classes * AthletesPerClass);
                            grouping.Cell(grow, 2 + lane).Value =
                                Surnames[n % Surnames.Length] +
                                GivenNames[(n / Surnames.Length) % GivenNames.Length];
                            bibIndex++;
                        }
                        grow++;

                        for (var lane = 0; lane < 8; lane++)
                        {
                            var n = (bibIndex - 8 + lane) % (Classes * AthletesPerClass);
                            var classNo = n / AthletesPerClass % perGrade + 1;
                            grouping.Cell(grow, 2 + lane).Value = $"{classNo}班";
                        }
                        grow++;
                    }
                }
            }
        }

        wb.SaveAs(path);
        return path;
    }
}
