using System.IO;
using ClosedXML.Excel;
using GradeStrip.Core.Export;
using GradeStrip.Core.Import;
using GradeStrip.Core.Model;
using Xunit;

namespace GradeStrip.Tests;

/// <summary>
/// 导出守卫下沉 Core 层（第二轮审计 P2-5 / AGENTS 第八.8）。
///
/// App 层守卫是第一道（状态栏话术），但 AGENTS 第八.8 要求「任何可能覆盖输入文件的
/// 路径必须在 Core 层拒绝」——任何绕过界面的新入口（脚本、将来的批量导出）都得被拦住。
/// 这里直接调 <see cref="ScoreStripExporter.Export"/> 断言它自己会拒绝。
///
/// 抛的是 <see cref="InvalidOperationException"/> 而不是 ToolboxException：
/// 后者定义在 TeachCraft.Core，GradeStrip.Core 不引用它（加了就破坏项目边界）。
/// 分晓的 FriendlyError 把 InvalidOperationException 当业务消息原样透传，语义闭环现成。
/// </summary>
public class ScoreStripExportGuardTests
{
    private static ExamData CreateExam() => new()
    {
        ExamName = "第一次段考",
        ClassName = "九1班",
        Subjects = new[] { "数学" },
        Students = new List<StudentRecord>
        {
            new()
            {
                No = 1,
                Name = "苏晚晴",
                ClassName = "九1班",
                Total = new SubjectScore { Score = 100, ClassRank = 1 },
                Subjects = new Dictionary<string, SubjectScore>
                {
                    ["数学"] = new SubjectScore { Score = 100, ClassRank = 1 },
                },
            },
        },
    };

    [Fact]
    public void Export_RefusesToOverwriteImportedSource()
    {
        // 修复前必红：Export 通篇没有来源比对，直接把导入的原件覆盖掉。
        var source = Path.Combine(Path.GetTempPath(), $"gradestrip-source-{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(source, Array.Empty<byte>());

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ScoreStripExporter.Export(CreateExam(), source, importedSources: new[] { source }));

            Assert.Contains("不能覆盖", ex.Message, StringComparison.Ordinal);
            Assert.Contains("分析报表", ex.Message, StringComparison.Ordinal);

            // 消息是中文业务话术，不是底层库的英文堆栈——FriendlyError 会原样透传它。
            Assert.Contains(ex.Message, c => c >= '\u4e00' && c <= '\u9fff');
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
        }
    }

    [Fact]
    public void Export_RefusesWhenAnyImportedSourceCollides()
    {
        // 多份来源里命中任意一份都要拒绝（成绩总表 + 报表原件混在一起传）。
        var report = Path.Combine(Path.GetTempPath(), $"gradestrip-report-{Guid.NewGuid():N}.xlsx");
        var examSheet = Path.Combine(Path.GetTempPath(), $"gradestrip-exam-{Guid.NewGuid():N}.xlsx");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScoreStripExporter.Export(
                CreateExam(), report, importedSources: new[] { examSheet, report }));

        Assert.Contains("不能覆盖", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_WithoutImportedSourcesStillWrites()
    {
        // 默认参数（null）= 不设防：GradeStrip.Tests 里十处既有调用与内部工具不受影响。
        var target = Path.Combine(Path.GetTempPath(), $"gradestrip-out-{Guid.NewGuid():N}.xlsx");
        try
        {
            var result = ScoreStripExporter.Export(CreateExam(), target);

            Assert.Equal(1, result.StudentCount);
            Assert.True(File.Exists(target));

            using var workbook = new XLWorkbook(target);
            Assert.NotEmpty(workbook.Worksheets);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public void Export_DistinctPathWithSourcesStillWrites()
    {
        // 守卫只拦命中来源的路径：正常导出不能被误伤。
        var source = Path.Combine(Path.GetTempPath(), $"gradestrip-src-{Guid.NewGuid():N}.xlsx");
        var target = Path.Combine(Path.GetTempPath(), $"gradestrip-ok-{Guid.NewGuid():N}.xlsx");
        try
        {
            var result = ScoreStripExporter.Export(
                CreateExam(), target, importedSources: new[] { source });

            Assert.Equal(1, result.StudentCount);
            Assert.True(File.Exists(target));
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    // ---------------------------------------------------------------- 报表来源字段（P2-1）

    [Fact]
    public void OverviewReport_CarriesFullSourcePath()
    {
        // 报表此前只记文件名，导出守卫无法与目标路径比对。
        var dir = Path.Combine(Path.GetTempPath(), $"gradestrip-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "班级成绩对比.xlsx");

        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("班级成绩对比");
                sheet.Cell(1, 1).Value = "班级成绩对比--某某中学";
                sheet.Cell(2, 1).Value = "班级";
                sheet.Cell(2, 2).Value = "数学";
                sheet.Range(2, 2, 2, 4).Merge();
                sheet.Cell(3, 2).Value = "得分";
                sheet.Cell(3, 3).Value = "教师";
                sheet.Cell(3, 4).Value = "排名";
                sheet.Cell(4, 1).Value = "九1班";
                sheet.Cell(4, 2).Value = 88.5d;
                sheet.Cell(4, 3).Value = "沈青禾";
                sheet.Cell(4, 4).Value = 1d;
                workbook.SaveAs(path);
            }

            var report = Assert.Single(OverviewReportImporter.Load(path));

            Assert.Equal("班级成绩对比.xlsx", report.SourceFile);
            Assert.NotNull(report.SourceFullPath);
            Assert.Equal(Path.GetFullPath(path), report.SourceFullPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OverviewReportFilter_PreservesFullSourcePath()
    {
        // 班级筛选会重建 OverviewReport：漏带 SourceFullPath 会让筛选后的报表丢掉守卫依据。
        var report = new OverviewReport
        {
            Kind = OverviewReportKind.ClassComparison,
            SourceFile = "报表.xlsx",
            SourceFullPath = @"C:\数据\报表.xlsx",
            Sheets = new[]
            {
                new OverviewSheet
                {
                    Name = "班级成绩对比",
                    Columns = new[] { new OverviewColumn { Header = "班级" } },
                    Rows = new[] { (IReadOnlyList<string>)new[] { "九1班" } },
                },
            },
        };

        var filtered = OverviewReportFilter.Filter(report, "九1班");

        Assert.Equal(report.SourceFullPath, filtered.SourceFullPath);
    }
}
