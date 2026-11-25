using Homework.Core.Roster;

namespace TeachCraft.Tests;

/// <summary>粘贴解析：四种输入形态、逐行报错与批量规模。全部使用合成姓名与班级。</summary>
public sealed class HomeworkPasteTests
{
    [Fact]
    public void Parse_NameOnly_UsesDefaultClass()
    {
        var result = RosterPasteParser.Parse("苏晚晴\n陈屿\n", "八年级1班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Single(result.Classes);
        Assert.Equal("八年级1班", result.Classes[0].Name);
        Assert.All(result.Students, student =>
        {
            Assert.Equal(string.Empty, student.Number);
            Assert.Equal(result.Classes[0].Id, student.ClassId);
        });
    }

    [Fact]
    public void Parse_NumberSpaceName()
    {
        var result = RosterPasteParser.Parse("01 苏晚晴\n02 陈屿", "八年级1班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal("苏晚晴", result.Students[0].Name);
    }

    [Fact]
    public void Parse_NumberCommaName()
    {
        var result = RosterPasteParser.Parse("01,苏晚晴\n02,陈屿", "八年级1班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal("苏晚晴", result.Students[0].Name);
    }

    [Fact]
    public void Parse_ClassCommaNumberCommaName()
    {
        var result = RosterPasteParser.Parse("八年级1班,01,苏晚晴\n九年级11班,02,陈屿", "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Equal(["八年级1班", "九年级11班"], result.Classes.Select(item => item.Name).ToArray());
        Assert.Equal(result.Classes[0].Id, result.Students[0].ClassId);
        Assert.Equal(result.Classes[1].Id, result.Students[1].ClassId);
    }

    [Fact]
    public void Parse_TabSeparatedExcelPaste()
    {
        var result = RosterPasteParser.Parse("八年级1班\t01\t苏晚晴\n八年级1班\t02\t陈屿", "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal("苏晚晴", result.Students[0].Name);
    }

    [Fact]
    public void Parse_ChineseCommaAndCrLf()
    {
        var result = RosterPasteParser.Parse("八年级1班，01，苏晚晴\r\n八年级1班，02，陈屿", "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Equal("苏晚晴", result.Students[0].Name);
    }

    [Fact]
    public void Parse_BlankLinesIgnored_AndLineNumbersAreOneBased()
    {
        var result = RosterPasteParser.Parse("苏晚晴\n\n01,02,03,陈屿\n许砚", "八年级1班");

        Assert.Equal(2, result.Students.Count);
        Assert.Single(result.Errors);
        Assert.Contains("第 3 行", result.Errors[0]);
    }

    [Fact]
    public void Parse_DuplicateNumberWithinClass_ReportedAsError()
    {
        var result = RosterPasteParser.Parse(
            "八年级1班,01,苏晚晴\n八年级1班,01,陈屿\n九年级11班,01,许砚", "默认班");

        Assert.Equal(2, result.Students.Count);
        Assert.Single(result.Errors);
        Assert.Contains("第 2 行", result.Errors[0]);
        Assert.Contains("01", result.Errors[0]);
    }

    [Fact]
    public void Parse_MissingName_ReportedAsError()
    {
        var result = RosterPasteParser.Parse("01,\n02,陈屿", "八年级1班");

        Assert.Single(result.Students);
        Assert.Single(result.Errors);
        Assert.Contains("第 1 行", result.Errors[0]);
    }

    [Fact]
    public void Parse_BlankClassInThreeFields_FallsBackToDefault()
    {
        var result = RosterPasteParser.Parse(",01,苏晚晴", "八年级1班");

        Assert.Single(result.Students);
        Assert.Equal("八年级1班", result.Classes[0].Name);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_TrimsAndCollapsesWhitespace()
    {
        var result = RosterPasteParser.Parse("  01  ,  苏  晚晴  ", "八年级1班");

        Assert.Single(result.Students);
        Assert.Equal("01", result.Students[0].Number);
        Assert.Equal("苏 晚晴", result.Students[0].Name);
    }

    [Fact]
    public void Parse_EmptyText_ReturnsEmpty()
    {
        var result = RosterPasteParser.Parse("", "八年级1班");

        Assert.Empty(result.Students);
        Assert.Empty(result.Classes);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_TwoHundredLines_AllParsed()
    {
        var lines = Enumerable.Range(1, 200)
            .Select(index => $"八年级1班,{index:D3},合成学生{index}")
            .ToArray();

        var result = RosterPasteParser.Parse(string.Join("\n", lines), "默认班");

        Assert.Equal(200, result.Students.Count);
        Assert.Empty(result.Errors);
        Assert.Single(result.Classes);
        Assert.Equal("001", result.Students[0].Number);
        Assert.Equal("200", result.Students[^1].Number);
        Assert.All(result.Students, student => Assert.NotEqual(Guid.Empty, student.Id));
    }
}
