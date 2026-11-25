using System.Collections.Immutable;
using SeatGrid.Core;
using SeatGrid.Core.Cloud;
using SeatGrid.Core.Export;
using SeatGrid.Core.Models;
using SeatGrid.Core.Seating;

namespace TeachCraft.Tests;

public sealed class SeatGridCloudMapperTests
{
    // 全部为合成姓名，不含任何真实师生信息。
    private static readonly string[] Names =
    {
        "苏晚晴", "陈屿", "林知夏", "沈青禾", "陆明川", "顾南乔", "叶临溪", "许清和", "周砚", "温以宁",
        "江照野", "程未晞", "宋知微", "唐予安", "韩清越", "谢云舟", "白若溪", "罗景行", "何书瑶", "郭亦辰",
        "高念初", "马星阑", "徐听澜", "孙慕言", "朱南风", "胡月白", "秦子墨", "梁思远", "赵一诺", "黄嘉树",
    };

    private static (SeatGridConfig Config, SeatGridRosterInput Roster) Fixture()
    {
        var students = new List<Student>();
        for (var i = 0; i < Names.Length; i++)
        {
            students.Add(new Student
            {
                Name = Names[i],
                Gender = i % 2 == 0 ? Gender.Female : Gender.Male,
                Number = $"S{i + 1:D4}",
                Height = 150 + i,
                Vision = 4.5 + (i % 6) * 0.1,
                Score = 500 + i * 3,
                Notes = $"备注{i + 1}",
                OriginalGroup = $"原组{i % 4 + 1}",
            });
        }

        var canvas = SeatChartCanvas.Empty
            .WithDisabled(new SeatPosition(0, 5, 1), true)
            .WithRowCorridor(2, 0.5f)
            .WithColumnCorridor(1, 0.3f)
            .WithGroupCorridor(1, 0.4f)
            .WithDoors(ImmutableArray.Create(new RoomDoor(RoomEdge.Front, 0.25, "前门")));

        var settings = new SeatChartSettings
        {
            Title = "示例中学八年级三班座位表",
            Groups = 4,
            Rows = 6,
            Columns = 2,
            Canvas = canvas,
            MentorEnabled = true,
            MentorSeat = MentorSeat.FrontRow,
            ArrangeMode = ArrangeMode.Height,
            WithinGroup = WithinGroupOrder.Descending,
        };

        var seats = settings.GetLayout().FillOrder().Take(students.Count).ToList();
        var assignments = new Dictionary<SeatPosition, Student>();
        for (var i = 0; i < students.Count; i++) assignments[seats[i]] = students[i];

        var constraints = new ArrangeConstraints
        {
            Pins = new Dictionary<Guid, SeatPosition> { [students[0].Id] = seats[0] },
            Mutex = new List<StudentPair> { StudentPair.Create(students[1].Id, students[2].Id) },
            Deskmates = new List<StudentPair> { StudentPair.Create(students[3].Id, students[4].Id) },
            AvoidDeskmates = new List<StudentPair> { StudentPair.Create(students[5].Id, students[6].Id) },
            Zones = new Dictionary<Guid, StudentZone>
            {
                [students[7].Id] = new StudentZone(
                    new List<SeatRect> { new(0, 1, 0, 2, 0, 1) },
                    new List<SeatRect> { new(2, 3, 3, 5, 0, 1) }),
            },
            Seed = 42,
            MentorEnabled = true,
            MentorSeat = MentorSeat.FrontRow,
        };

        var config = new SeatGridConfig(settings, students, assignments, new List<string>(), constraints);
        var roster = new SeatGridRosterInput(
            3,
            "示例中学八年级三班",
            new[] { "height", "vision", "score", "notes", "originalGroup" },
            SeatGridRosterProjection.FromStudents(students, new SeatGridRosterFields(true, true, true, true, true)));
        return (config, roster);
    }

    private static List<string> PairNames(
        IEnumerable<StudentPair> pairs, IReadOnlyDictionary<Guid, Student> byId) => pairs
        .Select(p => string.Join("+", new[] { byId[p.A].Name, byId[p.B].Name }.OrderBy(n => n, StringComparer.Ordinal)))
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void RoundTrip_ThirtyStudents_PreservesEverythingButIds()
    {
        var (config, roster) = Fixture();

        var doc = SeatGridCloudMapper.ToDocument(
            config, "示例中学八年级三班座位表", "roster-001", 3, roster, "1.45.0-test");
        var decoded = SeatGridCloudCodec.Decode(SeatGridCloudCodec.Encode(doc));
        var result = SeatGridCloudMapper.FromDocument(decoded, roster, usedSnapshot: false);

        Assert.Equal(SeatGridCloudMapper.Kind, decoded.Kind);
        Assert.Equal("roster-001", decoded.RosterId);
        Assert.Empty(result.Warnings);

        Assert.Equal(30, result.Config.Students.Count);
        Assert.Equal(config.Students.Select(s => s.Name), result.Config.Students.Select(s => s.Name));
        Assert.Equal(config.Students.Select(s => s.Number), result.Config.Students.Select(s => s.Number));
        Assert.Equal(config.Students.Select(s => s.Height), result.Config.Students.Select(s => s.Height));
        Assert.Equal(config.Students.Select(s => s.Vision), result.Config.Students.Select(s => s.Vision));
        Assert.Equal(config.Students.Select(s => s.Score), result.Config.Students.Select(s => s.Score));
        Assert.Equal(config.Students.Select(s => s.Notes), result.Config.Students.Select(s => s.Notes));
        Assert.Equal(config.Students.Select(s => s.OriginalGroup), result.Config.Students.Select(s => s.OriginalGroup));

        var oldIds = config.Students.Select(s => s.Id).ToHashSet();
        Assert.All(result.Config.Students, s => Assert.DoesNotContain(s.Id, oldIds));

        Assert.Equal(config.Assignments.Count, result.Config.Assignments.Count);
        foreach (var (seat, student) in config.Assignments)
            Assert.Equal(student.Name, result.Config.Assignments[seat].Name);

        var oldById = config.Students.ToDictionary(s => s.Id);
        var newById = result.Config.Students.ToDictionary(s => s.Id);
        var oldConstraints = config.Constraints!;
        var newConstraints = result.Config.Constraints!;

        Assert.All(newConstraints.Pins.Keys, id => Assert.True(newById.ContainsKey(id)));
        Assert.Equal(
            oldConstraints.Pins.Keys.Select(id => oldById[id].Name).OrderBy(n => n, StringComparer.Ordinal),
            newConstraints.Pins.Keys.Select(id => newById[id].Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(PairNames(oldConstraints.Mutex, oldById), PairNames(newConstraints.Mutex, newById));
        Assert.Equal(PairNames(oldConstraints.Deskmates, oldById), PairNames(newConstraints.Deskmates, newById));
        Assert.Equal(PairNames(oldConstraints.AvoidDeskmates, oldById), PairNames(newConstraints.AvoidDeskmates, newById));

        Assert.Equal(oldConstraints.Zones.Count, newConstraints.Zones.Count);
        foreach (var (oldId, oldZone) in oldConstraints.Zones)
        {
            var name = oldById[oldId].Name;
            var newId = newConstraints.Zones.Keys.First(id => newById[id].Name == name);
            Assert.Equal(oldZone.Allowed, newConstraints.Zones[newId].Allowed);
            Assert.Equal(oldZone.Denied, newConstraints.Zones[newId].Denied);
        }

        Assert.Equal(oldConstraints.Seed, newConstraints.Seed);
        Assert.Equal(oldConstraints.MentorEnabled, newConstraints.MentorEnabled);
        Assert.Equal(oldConstraints.MentorSeat, newConstraints.MentorSeat);

        Assert.True(config.Settings.Canvas.Equals(result.Config.Settings.Canvas));
        Assert.Equal(config.Settings.Title, result.Config.Settings.Title);
        Assert.Equal(config.Settings.Groups, result.Config.Settings.Groups);
        Assert.Equal(config.Settings.Rows, result.Config.Settings.Rows);
        Assert.Equal(config.Settings.Columns, result.Config.Settings.Columns);
        Assert.Equal(config.Settings.MentorEnabled, result.Config.Settings.MentorEnabled);
        Assert.Equal(config.Settings.MentorSeat, result.Config.Settings.MentorSeat);
        Assert.Equal(config.Settings.ArrangeMode, result.Config.Settings.ArrangeMode);
        Assert.Equal(config.Settings.WithinGroup, result.Config.Settings.WithinGroup);
    }

    [Fact]
    public void FromDocument_VersionMismatch_WarnsAndRemapsByName()
    {
        var (config, roster) = Fixture();
        var doc = SeatGridCloudMapper.ToDocument(config, "座位表", "r", 3, roster, "v");

        var swapped = roster.Students.ToList();
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        var current = new SeatGridRosterInput(5, roster.Name, roster.FieldsPresent, swapped);

        var result = SeatGridCloudMapper.FromDocument(doc, current, usedSnapshot: false);

        Assert.Contains(result.Warnings,
            w => w.Contains("座位表关联名单版本 3，当前名单版本 5；已按序号+姓名重排约束。"));
        var seat = config.Assignments.First(kv => kv.Value.Id == config.Students[0].Id).Key;
        Assert.Equal(config.Students[0].Name, result.Config.Assignments[seat].Name);
    }

    [Fact]
    public void FromDocument_DuplicateNames_IndexWinsAndAmbiguousDrops()
    {
        var students = new List<SeatGridRosterStudent>
        {
            new(1, "苏晚晴", "女", "S1", null, null, null, null, null),
            new(2, "苏晚晴", "女", "S2", null, null, null, null, null),
            new(3, "陈屿", "男", "S3", null, null, null, null, null),
        };
        var roster = new SeatGridRosterInput(1, "班", Array.Empty<string>(), students);
        var doc = new SeatGridCloudDocument
        {
            RosterVersion = 1,
            RosterSnapshot = new SharedRosterSnapshot
            {
                Version = 1,
                Students = students.Append(new SeatGridRosterStudent(
                    99, "苏晚晴", "女", "S99", null, null, null, null, null)).ToList(),
            },
            Assignments = new List<SeatGridAssignmentDto>
            {
                new() { Index = 1, Group = 0, Row = 0, Column = 0 },
                new() { Index = 99, Group = 0, Row = 0, Column = 1 },
            },
        };

        var result = SeatGridCloudMapper.FromDocument(doc, roster, usedSnapshot: false);

        Assert.Single(result.Config.Assignments);
        Assert.Equal("S1", result.Config.Assignments[new SeatPosition(0, 0, 0)].Number);
        Assert.Contains(result.Warnings, w => w.Contains("同名"));
    }

    [Fact]
    public void FromDocument_UsedSnapshot_RestoresEvenWhenLatestRosterDiffers()
    {
        var (config, roster) = Fixture();
        var doc = SeatGridCloudMapper.ToDocument(config, "座位表", "r", 3, roster, "v");
        var latest = new SeatGridRosterInput(9, "别的班", Array.Empty<string>(), new[]
        {
            new SeatGridRosterStudent(1, "无关同学", "男", "X1", null, null, null, null, null),
        });

        var result = SeatGridCloudMapper.FromDocument(doc, latest, usedSnapshot: true);

        Assert.True(result.UsedSnapshot);
        Assert.Equal(30, result.Config.Students.Count);
        Assert.Equal(config.Students.Select(s => s.Name), result.Config.Students.Select(s => s.Name));
        Assert.Equal(config.Assignments.Count, result.Config.Assignments.Count);
    }

    [Fact]
    public void FromDocument_NoRosterAndNoSnapshot_Throws()
    {
        var doc = new SeatGridCloudDocument { RosterVersion = 1 };
        var empty = new SeatGridRosterInput(1, string.Empty, Array.Empty<string>(),
            Array.Empty<SeatGridRosterStudent>());

        var ex = Assert.Throws<SeatGridException>(
            () => SeatGridCloudMapper.FromDocument(doc, empty, usedSnapshot: false));
        Assert.Contains("名单", ex.Message);
    }

    [Fact]
    public void FromDocument_MissingOptionalFields_WarnsAndLeavesNulls()
    {
        var (config, roster) = Fixture();
        var doc = SeatGridCloudMapper.ToDocument(config, "座位表", "r", 3, roster, "v");
        var limitedStudents = SeatGridRosterProjection.FromStudents(
            config.Students, new SeatGridRosterFields(false, false, true, false, false));
        var limited = new SeatGridRosterInput(3, roster.Name, new[] { "score" }, limitedStudents);

        var result = SeatGridCloudMapper.FromDocument(doc, limited, usedSnapshot: false);

        Assert.Contains(result.Warnings, w => w.Contains("身高"));
        Assert.Contains(result.Warnings, w => w.Contains("视力"));
        Assert.Contains(result.Warnings, w => w.Contains("备注"));
        Assert.Contains(result.Warnings, w => w.Contains("原小组"));
        Assert.All(limitedStudents, s =>
        {
            Assert.Null(s.Height);
            Assert.Null(s.Vision);
            Assert.Null(s.Notes);
            Assert.Null(s.OriginalGroup);
        });
        Assert.All(result.Config.Students, s =>
        {
            Assert.Null(s.Height);
            Assert.Null(s.Vision);
            Assert.Equal(string.Empty, s.Notes);
            Assert.Equal(string.Empty, s.OriginalGroup);
            Assert.NotNull(s.Score);
        });
    }

    [Fact]
    public void ToDocument_ExamSnapshot_ThrowsExactMessage()
    {
        var (config, roster) = Fixture();
        config.Settings.LayoutOverride = new SeatChartLayout(1, 5, 6);

        var ex = Assert.Throws<SeatGridException>(() => SeatGridCloudMapper.ToDocument(
            config, "座位表", "r", 1, roster, "v"));
        Assert.Equal(
            "考场快照不能保存到云端：考场是独立快照，不是日常座位。请先返回日常座位，再上传。",
            ex.Message);
    }

    [Fact]
    public void Decode_FutureVersion_Throws()
    {
        var ex = Assert.Throws<SeatGridException>(() => SeatGridCloudCodec.Decode("{\"v\":2,\"kind\":\"seatgrid.daily\"}"));
        Assert.Contains("版本", ex.Message);
    }

    [Fact]
    public void ToDocument_OnlySelectedFields_ExcludesOtherSentinelsFromJson()
    {
        var students = new List<Student>
        {
            new()
            {
                Name = "苏晚晴", Gender = Gender.Female, Number = "S1",
                Height = 98765.43, Vision = 87654.32, Score = 76543.21,
                Notes = "SENTINEL_NOTES_QQQ", OriginalGroup = "SENTINEL_GROUP_QQQ",
            },
        };
        var projected = SeatGridRosterProjection.FromStudents(
            students, new SeatGridRosterFields(false, false, true, false, false));
        var input = new SeatGridRosterInput(1, "班", new[] { "score" }, projected);
        var config = new SeatGridConfig(
            new SeatChartSettings(), students, new Dictionary<SeatPosition, Student>(), new List<string>());

        var doc = SeatGridCloudMapper.ToDocument(config, "座位表", "r", 1, input, "v");
        var json = SeatGridCloudCodec.Encode(doc);

        Assert.Contains("76543.21", json);
        Assert.DoesNotContain("98765.43", json);
        Assert.DoesNotContain("87654.32", json);
        Assert.DoesNotContain("SENTINEL_NOTES_QQQ", json);
        Assert.DoesNotContain("SENTINEL_GROUP_QQQ", json);
        Assert.Equal(new[] { "score" }, doc.RosterSnapshot!.FieldsPresent);
    }

    [Fact]
    public void Encode_OnlyRequiredFields_OmitsNullOptionalKeysAndDecodesBackToNull()
    {
        var doc = new SeatGridCloudDocument
        {
            V = 1,
            Kind = SeatGridCloudMapper.Kind,
            Name = "合成座位表",
            RosterId = "r1",
            RosterVersion = 1,
            RosterSnapshot = new SharedRosterSnapshot
            {
                Version = 1,
                FieldsPresent = Array.Empty<string>(),
                Students = new List<SeatGridRosterStudent>
                {
                    new(1, "苏晚晴", "女", "S1", null, null, null, null, null),
                },
            },
        };

        var json = SeatGridCloudCodec.Encode(doc);

        Assert.DoesNotContain("\"height\":null", json);
        Assert.DoesNotContain("\"notes\":null", json);
        Assert.DoesNotContain("\"original_group\":null", json);

        var decoded = SeatGridCloudCodec.Decode(json);
        var student = Assert.Single(decoded.RosterSnapshot!.Students);
        Assert.Null(student.Height);
        Assert.Null(student.Notes);
        Assert.Null(student.OriginalGroup);
    }
}
