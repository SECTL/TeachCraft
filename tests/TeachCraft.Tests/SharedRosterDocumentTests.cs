using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class SharedRosterDocumentTests
{
    [Fact]
    public void DefaultPresence_IsAllFalse()
    {
        var doc = new SharedRosterDocument();

        Assert.Equal(1, doc.V);
        Assert.Equal("teachcraft.roster.v1", doc.Kind);
        Assert.Equal(RosterFieldPresence.None, doc.Fields);
        Assert.False(doc.Fields.Height);
        Assert.False(doc.Fields.Vision);
        Assert.False(doc.Fields.Score);
        Assert.False(doc.Fields.Notes);
        Assert.False(doc.Fields.OriginalGroup);
        Assert.Empty(doc.Students);
    }

    [Fact]
    public void MissingOptionalFields_ListsAllFiveChineseLabels_InFixedOrder()
    {
        Assert.Equal(
            new[] { "身高", "视力", "成绩", "备注", "原小组" },
            RosterFieldPresence.None.MissingOptionalFields);
        Assert.Empty(RosterFieldPresence.All.MissingOptionalFields);
    }

    [Fact]
    public void PresentFieldNames_OnlyTrueKeys_InCanonicalOrder()
    {
        var presence = new RosterFieldPresence(true, false, true, false, true);

        Assert.Equal(new[] { "height", "score", "originalGroup" }, presence.PresentFieldNames);
        Assert.Empty(RosterFieldPresence.None.PresentFieldNames);
        Assert.Equal(
            new[] { "height", "vision", "score", "notes", "originalGroup" },
            RosterFieldPresence.All.PresentFieldNames);
    }

    [Fact]
    public void Codec_RoundTrip_PreservesEverything()
    {
        var updated = new DateTimeOffset(2026, 11, 24, 9, 30, 0, TimeSpan.Zero);
        var doc = new SharedRosterDocument
        {
            V = 1,
            Kind = "teachcraft.roster.v1",
            RosterId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
            Name = "示例中学八年级三班",
            Version = 7,
            UpdatedAtUtc = updated,
            Fields = RosterFieldPresence.All,
            Students = new List<SharedRosterStudent>
            {
                new()
                {
                    Index = 1, Name = "苏晚晴", Gender = "女", Number = "S001",
                    Height = 160.5, Vision = 4.8, Score = 590.5,
                    Notes = "靠前", OriginalGroup = "甲组",
                },
                new()
                {
                    Index = 2, Name = "陈屿", Gender = "男", Number = "S002",
                    Height = null, Vision = null, Score = null, Notes = null, OriginalGroup = null,
                },
            },
        };

        var json = SharedRosterCodec.Encode(doc);
        var decoded = SharedRosterCodec.Decode(json);

        Assert.Equal(doc.V, decoded.V);
        Assert.Equal(doc.Kind, decoded.Kind);
        Assert.Equal(doc.RosterId, decoded.RosterId);
        Assert.Equal(doc.Name, decoded.Name);
        Assert.Equal(doc.Version, decoded.Version);
        Assert.Equal(doc.UpdatedAtUtc, decoded.UpdatedAtUtc);
        Assert.Equal(doc.Fields, decoded.Fields);
        Assert.Equal(2, decoded.Students.Count);
        Assert.Equal(1, decoded.Students[0].Index);
        Assert.Equal("苏晚晴", decoded.Students[0].Name);
        Assert.Equal("女", decoded.Students[0].Gender);
        Assert.Equal("S001", decoded.Students[0].Number);
        Assert.Equal(160.5, decoded.Students[0].Height);
        Assert.Equal(4.8, decoded.Students[0].Vision);
        Assert.Equal(590.5, decoded.Students[0].Score);
        Assert.Equal("靠前", decoded.Students[0].Notes);
        Assert.Equal("甲组", decoded.Students[0].OriginalGroup);
        Assert.Null(decoded.Students[1].Height);
        Assert.Null(decoded.Students[1].Notes);
    }

    [Fact]
    public void Codec_RejectsVersionGreaterThanOne()
    {
        var json = "{\"v\":2,\"kind\":\"teachcraft.roster.v1\",\"students\":[]}";

        var ex = Assert.Throws<InvalidOperationException>(() => SharedRosterCodec.Decode(json));
        Assert.Contains("版本", ex.Message);
    }

    [Fact]
    public void Codec_ToleratesUnknownFields_AndMixedCasing()
    {
        var json = """
            {"V":1,"Kind":"teachcraft.roster.v1","RosterId":"r1","Name":"示例班",
             "Version":2,"UpdatedAtUtc":"2026-11-24T00:00:00+00:00",
             "Fields":{"height":true,"vision":false,"score":false,"notes":false,"original_group":false},
             "Students":[{"Index":1,"Name":"苏晚晴","Gender":"女","Number":"S1",
                          "Height":160,"Vision":null,"Score":null,"Notes":null,"OriginalGroup":null}],
             "future_field":{"nested":123}}
            """;

        var doc = SharedRosterCodec.Decode(json);

        Assert.Equal(1, doc.V);
        Assert.Equal("示例班", doc.Name);
        Assert.Equal(2, doc.Version);
        Assert.True(doc.Fields.Height);
        Assert.False(doc.Fields.Vision);
        Assert.Single(doc.Students);
        Assert.Equal("苏晚晴", doc.Students[0].Name);
        Assert.Equal(160, doc.Students[0].Height);
    }
}
