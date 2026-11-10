using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using GradeWeave.Core.Import;
using GradeWeave.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 「20 个班 × 9 个学科 × 694 节」合成织序工作簿语料（<see cref="WeavePerfCorpus"/>）的自检。
/// <para>
/// 语料是性能基准第三档的输入，必须先证明三件事：① 它真的能被
/// <see cref="WeaveWorkbookImporter"/> 读成一份规模正确的工程，而不是「读取失败有多快」；
/// ② 规模确实落在 AGENTS.md 记录的 20 班 / 62 教师 / 694 节基线上，基准数字才能与既有回归对照；
/// ③ 同参数两次生成逐格一致，否则跨机器、跨提交的性能数据没法比较。
/// 另外守一道隐私红线：姓名只能来自语料自己的虚构池，不得与另两档语料混同。
/// </para>
/// 生成物只落临时目录，测试结束即删，仓库里不出现大文件。
/// </summary>
public sealed class WeavePerfCorpusTests
{
    /// <summary>
    /// 隐私红线的金丝雀：这些串一旦出现在生成物里，说明有人把真实工作簿（或真实机构信息）
    /// 的内容抄进了语料。清单只放字段名与厂商标记，不放任何姓名样式的字串
    /// （AGENTS.md 第十节）。
    /// </summary>
    private static readonly string[] SuspiciousTokens =
    {
        "身份证",
        "学籍号",
        "家长",
        "联系电话尾号",
        "家庭住址",
        "准考证号",
        "示例一中",
        "实验中学",
    };

    [Fact]
    public void GeneratedWorkbook_ImportsIntoTwentyClassesAndNineSubjects()
    {
        WithCorpus(directory =>
        {
            var path = WeavePerfCorpus.WriteWorkbook(directory);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);

            var result = new WeaveWorkbookImporter().Import(path);
            var project = result.Project;

            // 班级：20 个，名字是「九（N）班」，编号 1…20 连续。
            Assert.Equal(WeavePerfCorpus.BaselineClasses, project.Classes.Count);
            for (var index = 0; index < project.Classes.Count; index++)
                Assert.Equal($"九（{index + 1}）班", project.Classes[index].Name);

            // 学科：9 个，顺序即分工表列顺序，周课时与语料规格一致（合计 35 = 5 天 × 7 节）。
            Assert.Equal(WeavePerfCorpus.SubjectNames, project.Subjects.Select(s => s.Name).ToList());
            Assert.Equal(35, project.Subjects.Sum(s => s.WeeklyHours));
            Assert.All(project.Subjects, s => Assert.InRange(s.WeeklyHours, 1, 6));
            Assert.True(project.Subjects.Single(s => s.Name == "语文").IsMain);
            Assert.True(project.Subjects.Single(s => s.Name == "数学").IsMain);
            Assert.True(project.Subjects.Single(s => s.Name == "英语").IsMain);
            Assert.False(project.Subjects.Single(s => s.Name == "劳动").IsMain);

            // 每班 9 个学科里有一个「化学」缺口（6 班 / 14 班），其余 8 科全有任课关系：
            // 20 × 9 − 2 = 178 条。
            Assert.Equal(20 * 9 - 2, project.Assignments.Count);
            Assert.Equal(
                project.Assignments.Count,
                project.Assignments.Select(a => (a.ClassId, a.SubjectId)).Distinct().Count());

            // 课程表被读成参考基线；导入器不把任何一节课标成「固定」。
            Assert.NotEmpty(project.Lessons);
            Assert.All(project.Lessons, lesson => Assert.False(lesson.IsFixed));
            Assert.All(project.Lessons, lesson => Assert.NotEqual(string.Empty, lesson.SubjectId));

            // 四张表都被认出来，且没有一条「无法识别」级别的提醒——
            // 生成器与导入器的结构契约对不上时，这里先红。
            Assert.Contains(result.Sheets, s => s.Kind.Contains("分工表", StringComparison.Ordinal));
            Assert.Contains(result.Sheets, s => s.Kind.Contains("确认表", StringComparison.Ordinal));
            Assert.Contains(result.Sheets, s => s.Kind.Contains("课程表", StringComparison.Ordinal));
            Assert.Empty(result.Warnings);

            // 会议活动备注被解析成教研与会议建议（只进建议，不写进工程）。
            Assert.NotEmpty(result.SuggestedResearchBlocks);
            Assert.Contains(result.SuggestedMeetingBlocks,
                block => block.Name == "班主任会议" && block.DayIndex == 1);
            Assert.Empty(project.ResearchBlocks);
            Assert.Empty(project.MeetingBlocks);
        });
    }

    [Fact]
    public void GeneratedWorkbook_KeepsTheSixtyTwoTeacherSixHundredNinetyFourLessonBaseline()
    {
        WithCorpus(directory =>
        {
            var project = new WeaveWorkbookImporter()
                .Import(WeavePerfCorpus.WriteWorkbook(directory))
                .Project;

            // 教师：62 位，全部在确认表里建档，分工表只引用同一批姓名，所以不会多出人来。
            Assert.Equal(WeavePerfCorpus.BaselineTeachers, project.Teachers.Count);
            Assert.True(project.Teachers.Count > 0);
            Assert.Equal(
                project.Teachers.Count,
                project.Teachers.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(project.Teachers, t => Assert.NotEqual(string.Empty, t.Subject));

            // 课时总量的断言区间：660…700。
            //   上限 700 = 20 个班 × 5 天 × 7 节正课，一个班不可能再多排一节；
            //   下限 660 = 700 − 40，给「个别班个别学科没有固定任课教师」留出余量——
            //   真实年级工作簿里确实会出现「轮流」「见附件」，导入器按规则不建任课关系。
            // 本语料刻意保留 2 个化学缺口（6 班 / 14 班，各 3 节/周），所以精确值是 694，
            // 与 AGENTS.md「20 班 / 62 教师 / 694 节」的规模基线、SchedulerScaleTests 的回归完全一致。
            var totalHours = project.Assignments.Sum(a => a.WeeklyHours);
            Assert.InRange(totalHours, 660, 700);
            Assert.Equal(WeavePerfCorpus.BaselineHours, totalHours);

            // 每个班的课时合计：有化学的 35 节，缺化学的 32 节。
            foreach (var schoolClass in project.Classes)
            {
                var hours = project.Assignments
                    .Where(a => a.ClassId == schoolClass.Id)
                    .Sum(a => a.WeeklyHours);
                var expected = schoolClass.Name is "九（6）班" or "九（14）班" ? 32 : 35;
                Assert.Equal(expected, hours);
            }

            // 课程表基线必须与任课关系同规模：694 个课程格 ↔ 694 节周课时。
            // 这条恒等式同时守住「化学缺口别只在一边留」——只改一边会立刻失衡。
            Assert.Equal(totalHours, project.Lessons.Count);
            Assert.Equal(totalHours, project.Lessons
                .Select(lesson => (lesson.ClassId, lesson.DayIndex, lesson.Period))
                .Distinct()
                .Count());
        });
    }

    [Fact]
    public void TwoRuns_ProduceIdenticalWorkbooks()
    {
        WithCorpus(first =>
        {
            WithCorpus(second =>
            {
                var firstPath = WeavePerfCorpus.WriteWorkbook(first);
                var secondPath = WeavePerfCorpus.WriteWorkbook(second);

                Assert.NotEqual(firstPath, secondPath);

                // 逐格比对，而不是只比工程规模：随机数一旦混进来，先红在具体单元格上。
                var firstCells = ReadAllCells(firstPath);
                var secondCells = ReadAllCells(secondPath);
                Assert.Equal(firstCells, secondCells);

                // 再比一遍导入结果（ID 分配也必须是确定的）。
                var importer = new WeaveWorkbookImporter();
                Assert.Equal(Fingerprint(importer.Import(firstPath).Project),
                    Fingerprint(importer.Import(secondPath).Project));
            });
        });
    }

    [Fact]
    public void GeneratedWorkbook_UsesOnlyItsOwnFictionalNamePool()
    {
        WithCorpus(directory =>
        {
            var path = WeavePerfCorpus.WriteWorkbook(directory);

            var fileName = Path.GetFileName(path);
            foreach (var token in SuspiciousTokens)
                Assert.DoesNotContain(token, fileName, StringComparison.Ordinal);

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

            // 教师姓名必须全部来自本语料自己的 256 个组合池里，
            // 且不得与成绩表语料（PerfCorpus）用过的姓名重合——三档语料混同会让性能数据串档。
            var allowed = WeavePerfCorpus.NamePool.ToHashSet(StringComparer.Ordinal);
            var teacherNames = workbook.Worksheet("确认表").CellsUsed()
                .Where(c => c.Address.ColumnNumber == 2 && c.Address.RowNumber > 1)
                .Select(c => c.GetString())
                .ToList();

            Assert.Equal(WeavePerfCorpus.BaselineTeachers, teacherNames.Count);
            Assert.Equal(teacherNames.Count, teacherNames.Distinct(StringComparer.Ordinal).Count());
            Assert.All(teacherNames, name => Assert.Contains(name, allowed));
            Assert.All(teacherNames, name => Assert.DoesNotContain(name, PerfCorpus.AllNames));

            // 姓名一律是「单字姓 + 双字名」的三字合成名，不出现单字名或数字占位。
            Assert.All(teacherNames, name => Assert.Matches(@"^[一-龥]{3}$", name));

            // 池子没被写窄：62 位教师取到了 62 个不同姓名。
            Assert.Equal(WeavePerfCorpus.BaselineTeacherNames.OrderBy(n => n, StringComparer.Ordinal),
                teacherNames.OrderBy(n => n, StringComparer.Ordinal));
        });
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>在临时目录里跑一段测试，结束时无论成败都删掉目录（生成物一律不入库）。</summary>
    private static void WithCorpus(Action<string> body)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "WeavePerfCorpus.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            body(directory);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* 清理失败不影响测试结论 */ }
        }
    }

    /// <summary>把工作簿压成「表名!单元格地址=值」的行序列，用于逐格比对两次生成。</summary>
    private static List<string> ReadAllCells(string path)
    {
        using var workbook = new XLWorkbook(path);

        var rows = new List<string>();
        foreach (var sheet in workbook.Worksheets)
        {
            rows.Add("[" + sheet.Name + "]");
            foreach (var cell in sheet.CellsUsed())
            {
                var value = cell.Value;
                var text = value.IsNumber
                    ? value.GetNumber().ToString("R", CultureInfo.InvariantCulture)
                    : cell.GetString();
                rows.Add(cell.Address.ToString() + "=" + text);
            }
        }

        return rows;
    }

    /// <summary>工程指纹：班级 / 教师 / 任课关系 / 课程基线，连 ID 一起比（ID 也必须可复现）。</summary>
    private static string Fingerprint(WeaveProject project)
    {
        var builder = new StringBuilder();
        builder.AppendLine("school=" + project.SchoolName);
        builder.AppendLine("grade=" + project.GradeName);
        builder.AppendLine("term=" + project.TermName);

        foreach (var item in project.Classes)
            builder.AppendLine($"class {item.Id} {item.Name} {item.HeadTeacher}");
        foreach (var subject in project.Subjects)
            builder.AppendLine($"subject {subject.Id} {subject.Name} {subject.WeeklyHours} {subject.IsMain}");
        foreach (var teacher in project.Teachers)
            builder.AppendLine($"teacher {teacher.Id} {teacher.Name} {teacher.Subject} {teacher.WeeklyHours} " +
                               string.Join('/', teacher.Roles));
        foreach (var assignment in project.Assignments)
            builder.AppendLine($"assignment {assignment.ClassId} {assignment.SubjectId} " +
                               $"{assignment.TeacherId} {assignment.WeeklyHours}");
        foreach (var lesson in project.Lessons
                     .OrderBy(l => l.ClassId, StringComparer.Ordinal)
                     .ThenBy(l => l.DayIndex)
                     .ThenBy(l => l.Period))
        {
            builder.AppendLine($"lesson {lesson.ClassId} {lesson.DayIndex} {lesson.Period} " +
                               $"{lesson.SubjectId} {lesson.TeacherId} {lesson.IsFixed}");
        }

        return builder.ToString();
    }
}
