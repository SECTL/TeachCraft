using Homework.App;
using Homework.Core.Models;
using Homework.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>
/// 共享名单纯映射：只消费姓名与学号，不引用任何云 DTO / HTTP 类型。
/// 全部使用合成姓名（苏晚晴 / 陈屿 / 许砚）与合成班级（八年级1班 / 九年级11班）。
/// </summary>
public sealed class HomeworkSharedRosterImportTests
{
    private static SharedRosterClassInput Class(string name, params SharedRosterStudentInput[] students) =>
        new(name, students);

    private static SharedRosterStudentInput Student(string name, string number = "") =>
        new(name, number);

    [Fact]
    public void SingleClass_CreatesClassAndStudentsWithNewGuids()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01"), Student("陈屿", "02")),
        ]);

        Assert.Single(result.Classes);
        Assert.Equal("八年级1班", result.Classes[0].Name);
        Assert.NotEqual(Guid.Empty, result.Classes[0].Id);
        Assert.Equal(2, result.Students.Count);
        Assert.Equal(["苏晚晴", "陈屿"], result.Students.Select(s => s.Name).ToArray());
        Assert.Equal(["01", "02"], result.Students.Select(s => s.Number).ToArray());
        Assert.All(result.Students, s =>
        {
            Assert.NotEqual(Guid.Empty, s.Id);
            Assert.Equal(result.Classes[0].Id, s.ClassId);
        });
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.SkippedRows);
    }

    [Fact]
    public void MultiClass_PreservesInputOrderAndCounts()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01"), Student("陈屿", "02")),
            Class("九年级11班", Student("许砚", "11")),
        ]);

        Assert.Equal(["八年级1班", "九年级11班"], result.Classes.Select(c => c.Name).ToArray());
        Assert.Equal(3, result.Students.Count);
        Assert.Equal(result.Classes[0].Id, result.Students[0].ClassId);
        Assert.Equal(result.Classes[0].Id, result.Students[1].ClassId);
        Assert.Equal(result.Classes[1].Id, result.Students[2].ClassId);
        Assert.Equal(0, result.SkippedRows);
    }

    [Fact]
    public void EmptyName_SkippedWithWarningAndCounted()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01"), Student("   ", "02"), Student("", "03"), Student("陈屿", "04")),
        ]);

        Assert.Equal(["苏晚晴", "陈屿"], result.Students.Select(s => s.Name).ToArray());
        Assert.Equal(2, result.SkippedRows);
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("没有姓名")));
        Assert.All(result.Warnings.Where(w => w.Contains("没有姓名")),
            w => Assert.Contains("八年级1班", w));
    }

    [Fact]
    public void DuplicateName_WarnsAndSkipsLaterOccurrence()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01"), Student("苏晚晴", "02")),
        ]);

        Assert.Single(result.Students);
        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal(1, result.SkippedRows);
        Assert.Single(result.Warnings);
        Assert.Contains("苏晚晴", result.Warnings[0]);
    }

    [Fact]
    public void DuplicateNumber_WarnsAndSkipsLaterOccurrence()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01"), Student("陈屿", "01")),
        ]);

        Assert.Single(result.Students);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal(1, result.SkippedRows);
        Assert.Single(result.Warnings);
        Assert.Contains("01", result.Warnings[0]);
    }

    [Fact]
    public void SameNumberInDifferentClasses_IsAllowed()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01")),
            Class("九年级11班", Student("陈屿", "01")),
        ]);

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.SkippedRows);
    }

    [Fact]
    public void SourceWarnings_AppearFirst()
    {
        var result = SharedRosterImport.ToRosterImportResult(
            [Class("八年级1班", Student("苏晚晴", "01"), Student("苏晚晴", "02"))],
            ["云端名单提示一", "云端名单提示二"]);

        Assert.Equal(["云端名单提示一", "云端名单提示二"], result.Warnings.Take(2).ToArray());
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains("苏晚晴", result.Warnings[2]);
    }

    [Fact]
    public void EmptyClassName_FallsBackToUnassignedWithWarning()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("   ", Student("苏晚晴", "01")),
        ]);

        Assert.Single(result.Classes);
        Assert.Equal("未分班", result.Classes[0].Name);
        Assert.Single(result.Students);
        Assert.Equal(result.Classes[0].Id, result.Students[0].ClassId);
        Assert.Contains("未分班", result.Warnings[0]);
    }

    [Fact]
    public void OnlyNameAndNumberConsumed_NoGenderInvented()
    {
        // 输入里没有性别 / 可选字段；映射结果只带姓名与学号，不臆造其它信息。
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", ""), Student("陈屿", "02")),
        ]);

        Assert.Equal(string.Empty, result.Students[0].Number);
        Assert.Equal("苏晚晴", result.Students[0].Name);
        Assert.Equal("02", result.Students[1].Number);
        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CountSkipReasons_SplitsEmptyDuplicateAndMissing()
    {
        var warnings = new[]
        {
            "「八年级1班」有一行没有姓名，已跳过。",
            "班级「八年级1班」内学号「01」重复，已跳过重复行。",
            "班级「八年级1班」内存在同名学生「许砚」，已跳过重复行。",
            "名单缺失：云端没有找到对应的内容，可能已被删除。（x）",
        };

        var reasons = SharedRosterImport.CountSkipReasons(warnings);

        Assert.Equal(new RosterSkipBreakdown(1, 2, 1), reasons);
        Assert.Equal(4, reasons.Total);
    }

    [Fact]
    public void ExtraSkipped_AddsToTotalWithoutChangingStudents()
    {
        var result = SharedRosterImport.ToRosterImportResult(
            [Class("八年级1班", Student("苏晚晴", "01"))], null, 2);

        Assert.Single(result.Students);
        Assert.Equal(2, result.SkippedRows);
    }

    [Fact]
    public void NormalizeClassName_TrimsCollapsesAndIgnoresCase()
    {
        Assert.Equal(SharedRosterImport.NormalizeClassName("八年级1班"),
            SharedRosterImport.NormalizeClassName("  八年级1班  "));
        Assert.Equal(SharedRosterImport.NormalizeClassName("八年级  1班"),
            SharedRosterImport.NormalizeClassName("八年级 1班"));
        Assert.Equal(SharedRosterImport.NormalizeClassName("Class A"),
            SharedRosterImport.NormalizeClassName("class a"));
    }

    [Fact]
    public void SpaceVariants_MergeIntoOneClassAndCatchCrossVariantDuplicates()
    {
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班", Student("苏晚晴", "01")),
            Class("  八年级1班  ", Student("陈屿", "01")),
            Class("九年级  2班", Student("许砚", "02")),
            Class("九年级 2班", Student("陈屿", "02")),
        ]);

        Assert.Equal(["八年级1班", "九年级 2班"], result.Classes.Select(c => c.Name).ToArray());
        Assert.Equal(["苏晚晴", "许砚"], result.Students.Select(s => s.Name).ToArray());
        Assert.Equal(2, result.SkippedRows);
    }

    [Fact]
    public void ApplyRoster_StatusText_ReportsEachSkipCategorySeparately()
    {
        var batch = HomeworkBatch.Create("批次1");
        var vm = new BatchWorkspaceViewModel(batch);
        var result = SharedRosterImport.ToRosterImportResult([
            Class("八年级1班",
                Student("苏晚晴", "01"),
                Student("   ", "02"),
                Student("陈屿", "01"),
                Student("许砚", "03"),
                Student("许砚", "04")),
        ], [$"{SharedRosterImport.MissingRosterMarker}：云端没有找到对应的内容，可能已被删除。（x）"], 1);

        vm.ApplyRoster(result);

        Assert.Contains("空姓名 1 行", vm.StatusText);
        Assert.Contains("重复学号或姓名 2 行", vm.StatusText);
        Assert.Contains("名单缺失 1 份", vm.StatusText);
        Assert.DoesNotContain("跳过 4 行空姓名", vm.StatusText);
    }
}
