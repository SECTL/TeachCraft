using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class SharedRosterProjectionTests
{
    [Fact]
    public void SentinelPrivacy_UnselectedFieldsNeverReachSerializedJson()
    {
        var student = new SharedRosterStudent
        {
            Index = 1,
            Name = "苏晚晴",
            Gender = "女",
            Number = "S001",
            Height = 171.5,
            Vision = 5.2,
            Score = 88.5,
            Notes = "SENTINEL_NOTE",
            OriginalGroup = "SENTINEL_GROUP",
        };
        var selection = SharedRosterProjection.SelectionFromFlags(
            height: false, vision: false, score: true, notes: false, originalGroup: false);

        var projected = SharedRosterProjection.Apply(new[] { student }, selection);
        var doc = new SharedRosterDocument
        {
            Fields = selection.ToPresence(),
            Students = projected.ToList(),
        };
        var json = SharedRosterCodec.Encode(doc);

        Assert.Contains("88.5", json);
        Assert.DoesNotContain("171.5", json);
        Assert.DoesNotContain("5.2", json);
        Assert.DoesNotContain("SENTINEL_NOTE", json);
        Assert.DoesNotContain("SENTINEL_GROUP", json);
        Assert.DoesNotContain("\"height\":null", json);
        Assert.DoesNotContain("\"vision\":null", json);
        Assert.DoesNotContain("\"notes\":null", json);
        Assert.DoesNotContain("\"original_group\":null", json);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var studentJson = parsed.RootElement.GetProperty("students")[0];
        Assert.False(studentJson.TryGetProperty("height", out _));
        Assert.False(studentJson.TryGetProperty("vision", out _));
        Assert.False(studentJson.TryGetProperty("notes", out _));
        Assert.False(studentJson.TryGetProperty("original_group", out _));

        Assert.False(doc.Fields.Height);
        Assert.False(doc.Fields.Vision);
        Assert.True(doc.Fields.Score);
        Assert.False(doc.Fields.Notes);
        Assert.False(doc.Fields.OriginalGroup);

        var item = Assert.Single(projected);
        Assert.Equal(1, item.Index);
        Assert.Equal("苏晚晴", item.Name);
        Assert.Equal("女", item.Gender);
        Assert.Equal("S001", item.Number);
        Assert.Null(item.Height);
        Assert.Null(item.Vision);
        Assert.Equal(88.5, item.Score);
        Assert.Null(item.Notes);
        Assert.Null(item.OriginalGroup);

        // 投影必须返回新对象，不能就地改输入。
        Assert.NotSame(student, item);
        Assert.Equal(171.5, student.Height);
        Assert.Equal("SENTINEL_NOTE", student.Notes);
    }

    [Fact]
    public void SelectionFromFlags_MapsEveryFlagInOrder()
    {
        var selection = SharedRosterProjection.SelectionFromFlags(
            height: true, vision: false, score: true, notes: false, originalGroup: true);

        Assert.True(selection.Height);
        Assert.False(selection.Vision);
        Assert.True(selection.Score);
        Assert.False(selection.Notes);
        Assert.True(selection.OriginalGroup);
    }

    [Fact]
    public void ToPresence_FromPresence_RoundTrip()
    {
        var presence = new RosterFieldPresence(true, false, true, false, true);

        var selection = RosterFieldSelection.FromPresence(presence);

        Assert.Equal(presence, selection.ToPresence());
        Assert.Equal(RosterFieldSelection.Minimal, RosterFieldSelection.FromPresence(RosterFieldPresence.None));

        var allSelected = new RosterFieldSelection(true, true, true, true, true);
        Assert.Equal(RosterFieldPresence.All, allSelected.ToPresence());
        Assert.Equal(allSelected, RosterFieldSelection.FromPresence(RosterFieldPresence.All));
    }

    [Fact]
    public void Apply_AllFields_PreservesEveryValue()
    {
        var student = new SharedRosterStudent
        {
            Index = 2, Name = "陈屿", Gender = "男", Number = "S002",
            Height = 175, Vision = 5.0, Score = 610, Notes = "靠窗", OriginalGroup = "乙组",
        };

        var item = Assert.Single(SharedRosterProjection.Apply(
            new[] { student }, RosterFieldSelection.FromPresence(RosterFieldPresence.All)));

        Assert.Equal(175, item.Height);
        Assert.Equal(5.0, item.Vision);
        Assert.Equal(610, item.Score);
        Assert.Equal("靠窗", item.Notes);
        Assert.Equal("乙组", item.OriginalGroup);
    }
}
