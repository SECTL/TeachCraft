using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using GradeStrip.Core.Import;

namespace TeachCraft.Tests;

/// <summary>
/// 合成语料生成器的守卫测试（docs/performance-audit · T2a）。
///
/// 三个角度：① 生成物必须是分晓真的能读回来的成绩表（不是「看起来像」）；
/// ② 同参数必须逐格可复现，否则性能数据没法跨机器对比；③ 不得夹带任何真实数据痕迹。
/// 生成物只落临时目录，测试结束即删，仓库里不出现大文件。
/// </summary>
public sealed class PerfCorpusTests
{
    /// <summary>
    /// 隐私红线的金丝雀：这些串一旦出现在生成物里，说明有人把真实导出（或真实机构信息）
    /// 的内容抄进了语料。清单刻意只放字段名与厂商标记，不放任何姓名样式的字串
    /// （AGENTS.md 第十节 + T2「禁止使用形似真实姓名的两字组合」）。
    /// </summary>
    private static readonly string[] SuspiciousTokens =
    {
        "身份证",
        "学籍号",
        "家长",
        "联系电话",
        "家庭住址",
        "准考证号",
        "智学网",
        "示例中学",
    };

    [Fact]
    public void GeneratedSheet_LoadsAsTenSubjectGradeSheet()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = PerfCorpus.WriteGradeSheet(directory);

            var data = GradeSheetImporter.Load(path);

            Assert.Equal(3000, data.Students.Count);
            Assert.Equal(10, data.Subjects.Count);
            Assert.Equal(PerfCorpus.SubjectPool, data.Subjects);

            // 班级列必须被识别成 20 个班（跨班时 ExamData.ClassName 留空，由 AvailableClasses 提供）
            Assert.Empty(data.ClassName);
            Assert.Equal(20, data.AvailableClasses.Count);
            Assert.All(data.AvailableClasses, c => Assert.StartsWith("九年级", c, StringComparison.Ordinal));

            // 学号按 2026010001 递增，且被当成文本原样读回（不能被浮点化成科学计数法）
            Assert.Equal("2026010001", data.Students[0].ExamNo);
            Assert.Equal("2026013000", data.Students[^1].ExamNo);
            Assert.Equal(3000, data.Students.Select(s => s.ExamNo).Distinct().Count());

            // 每行每科都要么是 0–100 的分数、要么是空单元格（缺考）；不能出现 NaN 或越界
            var scored = 0;
            var absent = 0;
            foreach (var student in data.Students)
            {
                foreach (var subject in PerfCorpus.SubjectPool)
                {
                    var score = student[subject].Score;
                    if (score is null)
                    {
                        absent++;
                        continue;
                    }

                    Assert.InRange(score.Value, 0, 100);
                    scored++;
                }
            }

            Assert.True(scored > 3000 * 10 * 0.9, $"实考格子太少：{scored}");
            Assert.True(absent > 0, "应当有少量缺考的空单元格");
            Assert.True(absent < 3000 * 10 * 0.1, $"缺考格子太多：{absent}");

            // 有分的学生总分必须由各科相加得到
            var first = data.Students[0];
            Assert.Equal(
                Math.Round(first.Subjects.Values.Where(v => v.Score.HasValue)
                    .Sum(v => v.Score!.Value), 2, MidpointRounding.AwayFromZero),
                first.Total.Score);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TwoRuns_ProduceIdenticalNameAndScoreSequence()
    {
        var firstDirectory = NewTempDirectory();
        var secondDirectory = NewTempDirectory();
        try
        {
            var firstPath = PerfCorpus.WriteGradeSheet(firstDirectory);
            var secondPath = PerfCorpus.WriteGradeSheet(secondDirectory);

            Assert.NotEqual(firstPath, secondPath);

            var firstRows = ReadNameAndScores(firstPath);
            var secondRows = ReadNameAndScores(secondPath);

            Assert.Equal(3000, firstRows.Count);
            Assert.Equal(firstRows, secondRows);
        }
        finally
        {
            DeleteDirectory(firstDirectory);
            DeleteDirectory(secondDirectory);
        }
    }

    [Fact]
    public void GeneratedSheet_CarriesNoSuspiciousTokens()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = PerfCorpus.WriteGradeSheet(directory);

            // 文件名
            var fileName = Path.GetFileName(path);
            foreach (var token in SuspiciousTokens)
                Assert.DoesNotContain(token, fileName, StringComparison.Ordinal);

            // 表名 + 全表单元格文本
            using var workbook = new XLWorkbook(path);
            foreach (var sheet in workbook.Worksheets)
            {
                foreach (var token in SuspiciousTokens)
                    Assert.DoesNotContain(token, sheet.Name, StringComparison.Ordinal);

                foreach (var cell in sheet.CellsUsed())
                {
                    var text = cell.GetString();
                    if (text.Length == 0) continue;

                    foreach (var token in SuspiciousTokens)
                        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
                }
            }

            // 姓名只能来自固定姓氏池 × 固定双字名池
            var allowed = PerfCorpus.AllNames.ToHashSet(StringComparer.Ordinal);
            var names = workbook.Worksheet(1).CellsUsed()
                .Where(c => c.Address.ColumnNumber == 2 && c.Address.RowNumber > 1)
                .Select(c => c.GetString())
                .ToList();

            Assert.Equal(3000, names.Count);
            Assert.All(names, name => Assert.Contains(name, allowed));

            // 100 个组合都要用上，避免「池子写了但只用了前几个」这类假覆盖
            Assert.Equal(allowed.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>把整表压成「姓名|科1|科2|…」的行序列，便于直接比较两份文件。</summary>
    private static List<string> ReadNameAndScores(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(1);
        var lastRow = sheet.LastRowUsed()!.RowNumber();

        var rows = new List<string>(lastRow);
        for (var row = 2; row <= lastRow; row++)
        {
            var builder = new StringBuilder();
            builder.Append(sheet.Cell(row, 2).GetString());
            for (var i = 0; i < PerfCorpus.SubjectPool.Length; i++)
            {
                builder.Append('|');
                var cell = sheet.Cell(row, 4 + i);
                if (cell.IsEmpty()) continue;
                builder.Append(cell.GetDouble().ToString("R", CultureInfo.InvariantCulture));
            }

            rows.Add(builder.ToString());
        }

        return rows;
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TeachCraft.Tests", "PerfCorpus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不该让测试变红；系统盘迟早会回收
        }
    }
}
