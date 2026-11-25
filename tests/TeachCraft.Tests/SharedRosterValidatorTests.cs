using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class SharedRosterValidatorTests
{
    [Theory]
    [InlineData("height", "身高")]
    [InlineData("vision", "视力")]
    [InlineData("score", "成绩")]
    [InlineData("notes", "备注")]
    [InlineData("originalGroup", "原小组")]
    public void RejectsUnselectedButPopulatedField(string field, string label)
    {
        var doc = MakeDoc(student =>
        {
            switch (field)
            {
                case "height": student.Height = 171.5; break;
                case "vision": student.Vision = 5.2; break;
                case "score": student.Score = 88.5; break;
                case "notes": student.Notes = "SENTINEL_NOTE"; break;
                case "originalGroup": student.OriginalGroup = "SENTINEL_GROUP"; break;
            }
        });

        var ex = Assert.Throws<ArgumentException>(() => SharedRosterValidator.Validate(doc));
        Assert.Contains(label, ex.Message);
    }

    [Fact]
    public void RejectsEmptyRoster()
    {
        var doc = new SharedRosterDocument();

        var ex = Assert.Throws<InvalidOperationException>(() => SharedRosterValidator.Validate(doc));
        Assert.Equal(CloudErrorText.RosterEmpty, ex.Message);
    }

    [Fact]
    public void RejectsEmptyName()
    {
        var doc = MakeDoc(student => student.Name = "   ");

        var ex = Assert.Throws<ArgumentException>(() => SharedRosterValidator.Validate(doc));
        Assert.Contains("姓名", ex.Message);
    }

    [Fact]
    public void RejectsDuplicateName()
    {
        var doc = MakeDoc();
        doc.Students.Add(new SharedRosterStudent
        {
            Index = 2, Name = "苏晚晴", Gender = "男", Number = "S002",
        });

        var ex = Assert.Throws<ArgumentException>(() => SharedRosterValidator.Validate(doc));
        Assert.Equal(CloudErrorText.RosterNameDuplicate, ex.Message);
    }

    [Theory]
    [InlineData("其他", "未知")]
    [InlineData("M", "未知")]
    [InlineData("", "未知")]
    [InlineData("女", "女")]
    [InlineData("男", "男")]
    [InlineData("未知", "未知")]
    public void NormalizesUnknownGender_InsteadOfThrowing(string input, string expected)
    {
        var doc = MakeDoc(student => student.Gender = input);

        SharedRosterValidator.Validate(doc);

        Assert.Equal(expected, doc.Students[0].Gender);
    }

    [Fact]
    public void EmptyOptionalValues_DoNotThrow_RegardlessOfFlag()
    {
        var selected = MakeDoc(student =>
        {
            student.Height = null;
            student.Vision = null;
            student.Score = null;
            student.Notes = "";
            student.OriginalGroup = "";
        }, RosterFieldPresence.All);
        SharedRosterValidator.Validate(selected);

        var unselected = MakeDoc(student =>
        {
            student.Height = null;
            student.Vision = null;
            student.Score = null;
            student.Notes = "";
            student.OriginalGroup = "";
        }, RosterFieldPresence.None);
        SharedRosterValidator.Validate(unselected);

        Assert.Null(unselected.Students[0].Height);
    }

    [Fact]
    public void AllFieldsSelected_PopulatedValues_DoNotThrow()
    {
        var doc = MakeDoc(student =>
        {
            student.Height = 171.5;
            student.Vision = 5.2;
            student.Score = 88.5;
            student.Notes = "备注";
            student.OriginalGroup = "甲组";
        }, RosterFieldPresence.All);

        SharedRosterValidator.Validate(doc);

        Assert.Equal(171.5, doc.Students[0].Height);
    }

    private static SharedRosterDocument MakeDoc(
        Action<SharedRosterStudent>? populate = null,
        RosterFieldPresence? fields = null)
    {
        var student = new SharedRosterStudent
        {
            Index = 1, Name = "苏晚晴", Gender = "女", Number = "S001",
        };
        populate?.Invoke(student);
        return new SharedRosterDocument
        {
            Fields = fields ?? RosterFieldPresence.None,
            Students = new List<SharedRosterStudent> { student },
        };
    }
}
