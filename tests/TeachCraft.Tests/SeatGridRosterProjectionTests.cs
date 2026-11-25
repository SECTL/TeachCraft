using SeatGrid.Core.Cloud;
using SeatGrid.Core.Models;

namespace TeachCraft.Tests;

public sealed class SeatGridRosterProjectionTests
{
    [Fact]
    public void FromStudents_SelectsOnlyChosenFields_AndNormalizesGender()
    {
        var students = new List<Student>
        {
            new() { Name = "苏晚晴", Gender = Gender.Female, Number = "S1", Height = 160, Vision = 4.8, Score = 590, Notes = "近视", OriginalGroup = "甲组" },
            new() { Name = "陈屿", Gender = Gender.Male, Number = "S2", Height = 175, Vision = 5.0, Score = 610, Notes = "", OriginalGroup = "乙组" },
            new() { Name = "林知夏", Gender = Gender.Unknown, Number = "S3", Height = 165, Vision = 4.9, Score = 580, Notes = "", OriginalGroup = "" },
        };

        var result = SeatGridRosterProjection.FromStudents(
            students, new SeatGridRosterFields(false, false, true, false, false));

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(s => s.Index));
        Assert.Equal(new[] { "女", "男", "未知" }, result.Select(s => s.Gender));
        Assert.Equal(new[] { "S1", "S2", "S3" }, result.Select(s => s.Number));
        Assert.All(result, s =>
        {
            Assert.Null(s.Height);
            Assert.Null(s.Vision);
            Assert.Null(s.Notes);
            Assert.Null(s.OriginalGroup);
        });
        Assert.Equal(new double?[] { 590, 610, 580 }, result.Select(s => s.Score));
    }

    [Fact]
    public void FromStudents_AllFields_PopulatesEverything()
    {
        var students = new List<Student>
        {
            new() { Name = "沈青禾", Gender = Gender.Female, Number = "S9", Height = 158, Vision = 4.7, Score = 601, Notes = "座位靠前", OriginalGroup = "三组" },
        };

        var result = SeatGridRosterProjection.FromStudents(
            students, new SeatGridRosterFields(true, true, true, true, true));

        var item = Assert.Single(result);
        Assert.Equal(158, item.Height);
        Assert.Equal(4.7, item.Vision);
        Assert.Equal(601, item.Score);
        Assert.Equal("座位靠前", item.Notes);
        Assert.Equal("三组", item.OriginalGroup);
    }

    [Fact]
    public void ToStudents_CreatesNewIds_AndWarnsOnEmptyAndDuplicateNames()
    {
        var items = new List<SeatGridRosterStudent>
        {
            new(1, "苏晚晴", "女", "S1", 160, 4.8, 590, "", "甲组"),
            new(2, "苏晚晴", "女", "S2", 161, 4.9, 591, "", "甲组"),
            new(3, "", "男", "S3", null, null, null, null, null),
        };
        var warnings = new List<string>();

        var students = SeatGridRosterProjection.ToStudents(items, warnings);

        Assert.Equal(3, students.Count);
        Assert.Equal(3, students.Select(s => s.Id).Distinct().Count());
        Assert.Equal("苏晚晴", students[0].Name);
        Assert.Equal(Gender.Female, students[0].Gender);
        Assert.Equal(160, students[0].Height);
        Assert.Equal("甲组", students[0].OriginalGroup);
        Assert.Equal(string.Empty, students[2].Name);
        Assert.Contains(warnings, w => w.Contains("空姓名"));
        Assert.Contains(warnings, w => w.Contains("重名"));
    }

    [Theory]
    [InlineData("示例中学八年级三班座位表.xlsx", null, "示例中学八年级三班")]
    [InlineData("八年级三班学生名单.xlsx", null, "八年级三班")]
    [InlineData("七年级1班花名册", null, "七年级1班")]
    [InlineData("九年级2班名单", null, "九年级2班")]
    [InlineData("随便一个文件.xlsx", "八年级五班", "八年级五班")]
    [InlineData(null, null, "班级名单")]
    [InlineData("座位表.xlsx", null, "班级名单")]
    public void Suggest_StripsSuffixes_AndFallsBack(string? importSource, string? title, string expected)
    {
        Assert.Equal(expected, SeatGridRosterName.Suggest(importSource, title));
    }
}
