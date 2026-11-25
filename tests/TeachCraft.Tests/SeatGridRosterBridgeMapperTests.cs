using System.Linq;
using SeatGrid.Core.Cloud;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class SeatGridRosterBridgeMapperTests
{
    private static SharedRosterDocument Doc(bool height, bool vision, bool score, bool notes, bool group) => new()
    {
        V = 1,
        Kind = CloudDocumentNamespace.Roster.Kind,
        RosterId = "roster-1",
        Name = "合成八年级名单",
        Version = 4,
        Fields = new RosterFieldPresence(height, vision, score, notes, group),
        Students =
        {
            new SharedRosterStudent
            {
                Index = 1, Name = "苏晚晴", Gender = "女", Number = "S0001",
                Height = height ? 158 : null,
                Vision = vision ? 5.0 : null,
                Score = score ? 612.5 : null,
                Notes = notes ? "合成备注" : null,
                OriginalGroup = group ? "一组" : null,
            },
        },
    };

    [Fact]
    public void ToInput_PreservesVersionNameAndPresence()
    {
        var input = SeatGridRosterBridgeMapper.ToInput(Doc(true, false, true, false, true));

        Assert.Equal(4, input.Version);
        Assert.Equal("合成八年级名单", input.Name);
        Assert.Equal(new[] { "height", "score", "originalGroup" }, input.FieldsPresent);
        Assert.Single(input.Students);
        Assert.Equal("苏晚晴", input.Students[0].Name);
        Assert.Equal(158, input.Students[0].Height);
        Assert.Null(input.Students[0].Vision);
        Assert.Equal(612.5, input.Students[0].Score);
        Assert.Null(input.Students[0].Notes);
        Assert.Equal("一组", input.Students[0].OriginalGroup);
    }

    [Fact]
    public void ToInput_UnselectedValuesAreNull()
    {
        var input = SeatGridRosterBridgeMapper.ToInput(Doc(false, false, false, false, false));

        Assert.Empty(input.FieldsPresent);
        var student = Assert.Single(input.Students);
        Assert.Null(student.Height);
        Assert.Null(student.Vision);
        Assert.Null(student.Score);
        Assert.Null(student.Notes);
        Assert.Null(student.OriginalGroup);
        Assert.Equal("S0001", student.Number);
    }

    [Fact]
    public void ToDocument_RoundTripsThroughToInput()
    {
        var fields = new SeatGridRosterFields(true, false, true, false, true);
        var students = new[]
        {
            new SeatGridRosterStudent(1, "陈屿", "男", "S0002", 172, null, 590, null, "二组"),
            new SeatGridRosterStudent(2, "林知夏", "女", "S0003", 160, null, 601, null, "二组"),
        };

        var doc = SeatGridRosterBridgeMapper.ToDocument("合成名单", students, fields);
        Assert.Equal(CloudDocumentNamespace.Roster.Kind, doc.Kind);
        Assert.Equal("合成名单", doc.Name);
        Assert.True(doc.Fields.Height);
        Assert.False(doc.Fields.Vision);
        Assert.True(doc.Fields.Score);
        Assert.False(doc.Fields.Notes);
        Assert.True(doc.Fields.OriginalGroup);
        Assert.All(doc.Students, s => Assert.Null(s.Vision));
        Assert.All(doc.Students, s => Assert.Null(s.Notes));

        var input = SeatGridRosterBridgeMapper.ToInput(doc);
        Assert.Equal(new[] { "height", "score", "originalGroup" }, input.FieldsPresent);
        Assert.Equal(students.Select(s => s.Name), input.Students.Select(s => s.Name));
        Assert.Equal(students.Select(s => s.Number), input.Students.Select(s => s.Number));
    }

    [Fact]
    public void ToSnapshot_RoundTripsEmbeddedRoster()
    {
        var snapshot = new SharedRosterSnapshot
        {
            V = 1,
            Name = "合成名单快照",
            Version = 3,
            FieldsPresent = new[] { "height", "notes" },
            Students =
            {
                new SeatGridRosterStudent(1, "沈青禾", "女", "S0004", 165, null, null, "快照备注", null),
            },
        };

        var input = SeatGridRosterBridgeMapper.ToSnapshot(snapshot);
        Assert.Equal(3, input.Version);
        Assert.Equal("合成名单快照", input.Name);
        Assert.Equal(new[] { "height", "notes" }, input.FieldsPresent);
        Assert.Equal("沈青禾", Assert.Single(input.Students).Name);
        Assert.Equal("快照备注", input.Students[0].Notes);
        Assert.Null(input.Students[0].Score);
    }

    [Fact]
    public void FieldConversions_AreSymmetric()
    {
        var fields = new SeatGridRosterFields(true, true, false, true, false);
        var presence = SeatGridRosterBridgeMapper.ToPresence(fields);
        Assert.Equal(fields, SeatGridRosterBridgeMapper.ToFields(presence));
        Assert.Equal(new[] { "height", "vision", "notes" }, presence.PresentFieldNames);
    }
}
