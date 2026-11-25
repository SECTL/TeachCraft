using Homework.Core.Models;
using Homework.Core.Recognition;

namespace TeachCraft.Tests;

/// <summary>
/// 学号解析器的纯逻辑回归。全部使用合成姓名（苏晚晴 / 陈屿 / 许砚 / 叶临溪），
/// 不依赖文件、UI 线程或真实名单。
/// </summary>
public sealed class FillInNumberResolverTests
{
    private static HomeworkClass Cls(string name = "八年级1班") => new(Guid.NewGuid(), name);

    private static HomeworkStudent Stu(HomeworkClass cls, string number, string name) =>
        new(Guid.NewGuid(), cls.Id, number, name);

    // ----------------------------- 精确匹配 -----------------------------

    [Fact]
    public void ExactMatch_ReturnsMatchedStudent()
    {
        var cls = Cls();
        var student = Stu(cls, "001", "苏晚晴");

        var result = FillInNumberResolver.Resolve("001", [student, Stu(cls, "002", "陈屿")]);

        Assert.Equal(FillInNumberStatus.Matched, result.Status);
        Assert.Equal(student.Id, result.StudentId);
        Assert.Null(result.FailureReason);
    }

    // ----------------------------- 前导零 / 数值回退 -----------------------------

    [Fact]
    public void LeadingZeros_DigitsWithZeros_MatchExactly()
    {
        var cls = Cls();
        var student = Stu(cls, "001", "苏晚晴");

        var result = FillInNumberResolver.Resolve("001", [student]);

        Assert.Equal(FillInNumberStatus.Matched, result.Status);
        Assert.Equal(student.Id, result.StudentId);
    }

    [Fact]
    public void NumericFallback_DigitsWithoutZeros_MatchStudentWithLeadingZeros()
    {
        var cls = Cls();
        var student = Stu(cls, "001", "苏晚晴");

        var result = FillInNumberResolver.Resolve("1", [student]);

        Assert.Equal(FillInNumberStatus.Matched, result.Status);
        Assert.Equal(student.Id, result.StudentId);
    }

    [Fact]
    public void NumericFallback_AllZeroDigits_NormalizeToZero()
    {
        var cls = Cls();
        var student = Stu(cls, "000", "苏晚晴");

        var result = FillInNumberResolver.Resolve("00", [student]);

        Assert.Equal(FillInNumberStatus.Matched, result.Status);
        Assert.Equal(student.Id, result.StudentId);
    }

    [Fact]
    public void ExactMatch_TakesPriorityOverNumericFallback()
    {
        var cls = Cls();
        var exact = Stu(cls, "1", "苏晚晴");
        var padded = Stu(cls, "001", "陈屿");

        var result = FillInNumberResolver.Resolve("1", [padded, exact]);

        Assert.Equal(FillInNumberStatus.Matched, result.Status);
        Assert.Equal(exact.Id, result.StudentId);
    }

    // ----------------------------- 歧义 -----------------------------

    [Fact]
    public void CrossClassDuplicate_ExactMatch_IsAmbiguous_AndListsBothClassNames()
    {
        var classA = Cls("八年级1班");
        var classB = Cls("八年级2班");
        var a = Stu(classA, "001", "苏晚晴");
        var b = Stu(classB, "001", "陈屿");
        var classNames = new Dictionary<Guid, string>
        {
            [classA.Id] = classA.Name,
            [classB.Id] = classB.Name,
        };

        var result = FillInNumberResolver.Resolve("001", [a, b], classNames);

        Assert.Equal(FillInNumberStatus.Ambiguous, result.Status);
        Assert.Null(result.StudentId);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("苏晚晴", result.FailureReason!);
        Assert.Contains("陈屿", result.FailureReason!);
        Assert.Contains("八年级1班", result.FailureReason!);
        Assert.Contains("八年级2班", result.FailureReason!);
        Assert.DoesNotContain(classA.Id.ToString(), result.FailureReason!);
        Assert.DoesNotContain(classB.Id.ToString(), result.FailureReason!);
    }

    [Fact]
    public void CrossClassDuplicate_WithoutClassNames_FallsBackToClassId_AndListsBothStudents()
    {
        var classA = Cls("八年级1班");
        var classB = Cls("八年级2班");
        var a = Stu(classA, "001", "苏晚晴");
        var b = Stu(classB, "001", "陈屿");

        var result = FillInNumberResolver.Resolve("001", [a, b], classNames: null);

        Assert.Equal(FillInNumberStatus.Ambiguous, result.Status);
        Assert.Null(result.StudentId);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("苏晚晴", result.FailureReason!);
        Assert.Contains("陈屿", result.FailureReason!);
        Assert.Contains(classA.Id.ToString(), result.FailureReason!);
        Assert.Contains(classB.Id.ToString(), result.FailureReason!);
    }

    [Fact]
    public void NumericFallback_MultipleMatches_IsAmbiguous()
    {
        var cls = Cls();
        var a = Stu(cls, "01", "苏晚晴");
        var b = Stu(cls, "001", "陈屿");

        var result = FillInNumberResolver.Resolve("1", [a, b]);

        Assert.Equal(FillInNumberStatus.Ambiguous, result.Status);
        Assert.Null(result.StudentId);
        Assert.Contains("苏晚晴", result.FailureReason!);
        Assert.Contains("陈屿", result.FailureReason!);
    }

    // ----------------------------- 不合格学号 / 无匹配 -----------------------------

    [Fact]
    public void EmptyNumber_IsIgnored_AndYieldsNoMatch()
    {
        var cls = Cls();
        var student = Stu(cls, "", "苏晚晴");

        var result = FillInNumberResolver.Resolve("001", [student]);

        Assert.Equal(FillInNumberStatus.NoMatch, result.Status);
        Assert.Null(result.StudentId);
        Assert.Contains("没有学号为 001 的学生", result.FailureReason!);
    }

    [Fact]
    public void NonDigitNumber_IsIgnored_AndYieldsNoMatch()
    {
        var cls = Cls();
        var student = Stu(cls, "A01", "苏晚晴");

        var result = FillInNumberResolver.Resolve("001", [student]);

        Assert.Equal(FillInNumberStatus.NoMatch, result.Status);
        Assert.Null(result.StudentId);
    }

    [Fact]
    public void NoMatch_WhenDigitsAbsentFromRoster()
    {
        var cls = Cls();
        var student = Stu(cls, "001", "苏晚晴");

        var result = FillInNumberResolver.Resolve("999", [student]);

        Assert.Equal(FillInNumberStatus.NoMatch, result.Status);
        Assert.Null(result.StudentId);
        Assert.Contains("没有学号为 999 的学生", result.FailureReason!);
    }

    [Fact]
    public void EmptyDigits_YieldsNoMatch_EvenWithEligibleStudents()
    {
        var cls = Cls();
        var student = Stu(cls, "001", "苏晚晴");

        foreach (var digits in new[] { "", "   ", "\t" })
        {
            var result = FillInNumberResolver.Resolve(digits, [student]);
            Assert.Equal(FillInNumberStatus.NoMatch, result.Status);
            Assert.Null(result.StudentId);
        }
    }

    [Fact]
    public void Resolve_DoesNotModifyRoster()
    {
        var cls = Cls();
        var a = Stu(cls, "001", "苏晚晴");
        var b = Stu(cls, "002", "陈屿");
        var students = new List<HomeworkStudent> { a, b };

        FillInNumberResolver.Resolve("001", students);
        FillInNumberResolver.Resolve("999", students);

        Assert.Equal(2, students.Count);
        Assert.Equal(a, students[0]);
        Assert.Equal(b, students[1]);
        Assert.Equal("001", students[0].Number);
        Assert.Equal("002", students[1].Number);
    }
}
