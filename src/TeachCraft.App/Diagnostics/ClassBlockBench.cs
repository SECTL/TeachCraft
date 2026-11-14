using ClassBlock.Core.Import;

namespace TeachCraft.App.Diagnostics;

/// <summary>
/// 时序业务基准（T4）：把「读取总表」这一条真实路径计时，结果进 <see cref="PerfBench"/>
/// 的同一张表，与竞名的四项并列。
/// <para>
/// 放在 App 层而不是测试项目：发布产物的 <c>--perf-bench</c> 里没有测试程序集，
/// 基准注册在 App 层才能在真实二进制上跑（第一版放在测试项目里，反射找不到类型，
/// 报告里根本没有时序那两行）。
/// </para>
/// <para>
/// 数据全部为合成：虚构校名与姓名，生成到临时目录，每个样本覆盖写同一个文件。
/// </para>
/// </summary>
public static class ClassBlockBench
{
    private const int Classes = 20;

    private static readonly string[] TeacherSurnames =
    {
        "施", "穆", "骆", "章", "洪", "翟", "敖", "尹", "幸", "游",
        "茹", "荀", "虞", "芮", "束", "竺", "眭", "扈", "邴", "糜",
    };

    private static readonly string[] Subjects =
    {
        "语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史", "地理",
    };

    /// <summary>把「时序 · 读取总表」登记到基准表；幂等，重复调用不会重复登记。</summary>
    public static void Register()
    {
        if (PerfBench.BusinessBenchmarks.Any(b => b.Name == "时序 · 读取总表")) return;

        string? corpus = null;

        PerfBench.BusinessBenchmarks.Add(("时序 · 生成 20 班语料", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "TeachCraft.PerfBench",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            PerfBench.RegisterTemporaryDirectory(dir);
            corpus = WriteMaster(dir);
        }, 1));

        PerfBench.BusinessBenchmarks.Add(("时序 · 读取总表", () =>
        {
            if (corpus is null) return;
            MasterScheduleImporter.Import(corpus);
        }, 3));
    }

    /// <summary>合成总表：星期 / 节次 / 班级行 / 课服 / 午学 / 早读备注，全按导入器契约排版。</summary>
    private static string WriteMaster(string directory)
    {
        var path = Path.Combine(directory, "示例中学总表.xlsx");
        var days = new[] { "星期一", "星期二", "星期三", "星期四", "星期五" };

        using var wb = new ClosedXML.Excel.XLWorkbook();

        // ---- 分工表（班主任）----
        var duty = wb.AddWorksheet("分工表");
        duty.Cell(1, 1).Value = "九年级分工表（2026年春）";
        duty.Cell(2, 1).Value = "班级";
        duty.Cell(2, 2).Value = "班主任";
        for (var i = 0; i < Classes; i++)
        {
            // 必须带「班」字：ClassKey.TryParse 的正则要求以「班」结尾（`九1` 不认、`九1班` 认）。
            duty.Cell(3 + i, 1).Value = $"九{i + 1}班";
            duty.Cell(3 + i, 2).Value = $"{TeacherSurnames[i % TeacherSurnames.Length]}老师";
        }

        // ---- 课程表 ----
        var sheet = wb.AddWorksheet("课程表");
        sheet.Cell(1, 1).Value = "九年级课程总表 2026年5月";
        sheet.Cell(2, 1).Value = "班级";
        for (var d = 0; d < 5; d++)
        {
            sheet.Cell(2, 2 + d * 10).Value = days[d];
            sheet.Range(2, 2 + d * 10, 2, 11 + d * 10).Merge();
        }

        // FindPeriodRow 用 int.TryParse，节次必须是纯数字。
        for (var p = 0; p < 7; p++)
            sheet.Cell(3, 2 + p).Value = p + 1;

        var sub = new[] { "课后服务1", "课后服务2", "晚修" };
        for (var d = 0; d < 5; d++)
            for (var k = 0; k < 3; k++)
                sheet.Cell(3, 9 + d * 10 + k).Value = sub[k];

        for (var r = 0; r < Classes; r++)
        {
            sheet.Cell(4 + r, 1).Value = $"九{r + 1}班";
            for (var d = 0; d < 5; d++)
            {
                for (var p = 0; p < 7; p++)
                {
                    var s = Subjects[(r + d + p) % Subjects.Length];
                    var t = TeacherSurnames[(r * 3 + p) % TeacherSurnames.Length];
                    sheet.Cell(4 + r, 2 + d * 10 + p).Value = $"{s} {t}老师";
                }
                sheet.Cell(4 + r, 9 + d * 10).Value = "培优辅差";
                sheet.Cell(4 + r, 10 + d * 10).Value = "阳光体育";
                sheet.Cell(4 + r, 11 + d * 10).Value = $"{TeacherSurnames[r % TeacherSurnames.Length]}老师";
            }
        }

        // 早读备注：与课程表同 sheet（导入器只扫候选表本身），放在数据区之后。
        sheet.Cell(Classes + 12, 3).Value =
            "早读安排：一、三英语，二、四语文，周五自由阅读；请各班按时进入教室，值班教师巡查记录。";

        // ---- 午学表 ----
        var lunch = wb.AddWorksheet("午学");
        lunch.Cell(1, 1).Value = "午学安排";
        lunch.Cell(2, 1).Value = "班级";
        for (var d = 0; d < 5; d++) lunch.Cell(2, 2 + d).Value = $"{days[d]}12:00";
        for (var r = 0; r < Classes; r++)
        {
            lunch.Cell(3 + r, 1).Value = $"九{r + 1}班";
            for (var d = 0; d < 5; d++) lunch.Cell(3 + r, 2 + d).Value = "午学";
        }

        wb.SaveAs(path);
        return path;
    }
}
