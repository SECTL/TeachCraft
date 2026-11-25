using System;
using System.Collections.Generic;
using System.Linq;
using SeatGrid.Core.Export;
using SeatGrid.Core.Models;
using SeatGrid.Core.Seating;

namespace SeatGrid.Core.Cloud;

/// <summary>一次云端读取的结果。</summary>
public sealed record SeatGridCloudResult(
    SeatGridConfig Config,
    IReadOnlyList<string> Warnings,
    bool UsedSnapshot);

/// <summary>
/// 归位日常座位表与云端文档之间的映射。只使用 SeatGrid.Core 自己的类型与中立输入；
/// 共享名单到 <see cref="SeatGridRosterInput"/> 的转换由宿主桥接层完成。
/// </summary>
public static class SeatGridCloudMapper
{
    public const int CurrentVersion = 1;
    public const string Kind = "seatgrid.daily";

    private const string ExamSnapshotMessage =
        "考场快照不能保存到云端：考场是独立快照，不是日常座位。请先返回日常座位，再上传。";

    /// <summary>把日常座位配置打包成云端文档。考场快照（LayoutOverride 非空）一律拒绝。</summary>
    public static SeatGridCloudDocument ToDocument(
        SeatGridConfig config,
        string name,
        string rosterId,
        int rosterVersion,
        SeatGridRosterInput snapshotInput,
        string appVersion)
    {
        if (config.Settings.LayoutOverride is not null)
            throw new SeatGridException(ExamSnapshotMessage);

        var indexOf = new Dictionary<Guid, int>();
        var nameOf = new Dictionary<Guid, string>();
        for (var i = 0; i < config.Students.Count; i++)
        {
            indexOf[config.Students[i].Id] = i + 1;
            nameOf[config.Students[i].Id] = config.Students[i].Name;
        }

        int IndexOf(Guid id) => indexOf.GetValueOrDefault(id);
        string NameOf(Guid id) => nameOf.GetValueOrDefault(id, string.Empty);

        var assignments = config.Assignments
            .Select(pair => new SeatGridAssignmentDto
            {
                Index = IndexOf(pair.Value.Id),
                Group = pair.Key.Group,
                Row = pair.Key.Row,
                Column = pair.Key.Column,
            })
            .OrderBy(a => a.Index).ThenBy(a => a.Group).ThenBy(a => a.Row).ThenBy(a => a.Column)
            .ToList();

        var constraints = new SeatGridConstraintsDto();
        if (config.Constraints is { } source)
        {
            constraints.Seed = source.Seed;
            constraints.MentorEnabled = source.MentorEnabled;
            constraints.MentorSeat = source.MentorSeat;

            foreach (var (id, seat) in source.Pins)
                constraints.Pins.Add(new SeatGridPinEntry
                {
                    Index = IndexOf(id),
                    Name = NameOf(id),
                    Group = seat.Group + 1,
                    Row = seat.Row + 1,
                    Column = seat.Column + 1,
                });

            foreach (var pair in source.Mutex)
                constraints.Mutex.Add(Pair(pair, IndexOf, NameOf));
            foreach (var pair in source.Deskmates)
                constraints.Deskmates.Add(Pair(pair, IndexOf, NameOf));
            foreach (var pair in source.AvoidDeskmates)
                constraints.AvoidDeskmates.Add(Pair(pair, IndexOf, NameOf));

            foreach (var (id, zone) in source.Zones)
            {
                foreach (var rect in zone.Allowed)
                    constraints.ZoneAllow.Add(Zone(id, rect, IndexOf, NameOf));
                foreach (var rect in zone.Denied)
                    constraints.ZoneDeny.Add(Zone(id, rect, IndexOf, NameOf));
            }
        }

        return new SeatGridCloudDocument
        {
            V = CurrentVersion,
            Kind = Kind,
            Name = name,
            RosterId = rosterId,
            RosterVersion = rosterVersion,
            RosterSnapshot = new SharedRosterSnapshot
            {
                V = CurrentVersion,
                Name = snapshotInput.Name,
                Version = snapshotInput.Version,
                FieldsPresent = snapshotInput.FieldsPresent.ToArray(),
                Students = snapshotInput.Students.ToList(),
            },
            Settings = SettingsFrom(config.Settings),
            Assignments = assignments,
            Constraints = constraints,
            CanvasJson = SeatChartCanvasCodec.Encode(config.Settings.Canvas),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AppVersion = appVersion,
        };
    }

    /// <summary>
    /// 从云端文档还原日常座位。优先用内嵌快照（usedSnapshot）恢复名单；否则用当前共享名单，
    /// 并按「序号 + 姓名」重排座位与约束。找不到名单时明确报错，绝不产出一份空座位表。
    /// </summary>
    public static SeatGridCloudResult FromDocument(
        SeatGridCloudDocument doc,
        SeatGridRosterInput roster,
        bool usedSnapshot)
    {
        var warnings = new List<string>();
        var useSnapshot = usedSnapshot && doc.RosterSnapshot is not null;

        IReadOnlyList<SeatGridRosterStudent> source;
        if (useSnapshot)
            source = doc.RosterSnapshot!.Students;
        else
            source = roster?.Students ?? Array.Empty<SeatGridRosterStudent>();

        if (source.Count == 0)
            throw new SeatGridException(
                "找不到可用的名单：当前共享名单为空，且这份云端座位表没有内嵌名单快照。");

        var students = SeatGridRosterProjection.ToStudents(source, warnings);

        // 快照里每个序号对应的姓名，用来在「当前名单」里做序号 + 姓名双校验。
        var snapshotNames = new Dictionary<int, string>();
        if (doc.RosterSnapshot is not null)
            foreach (var item in doc.RosterSnapshot.Students)
                snapshotNames[item.Index] = item.Name;

        var assignments = new Dictionary<SeatPosition, Student>();
        foreach (var item in doc.Assignments)
        {
            var student = Resolve(item.Index, NameAt(item.Index), students, warnings);
            if (student is null) continue;

            var seat = new SeatPosition(item.Group, item.Row, item.Column);
            if (assignments.TryGetValue(seat, out var existing) && existing.Id != student.Id)
            {
                warnings.Add($"座位（{seat}）被多名学生指向，已保留先到的一位。");
                continue;
            }

            assignments[seat] = student;
        }

        var constraints = ConstraintsFrom(doc.Constraints, students, warnings);
        var settings = SettingsTo(doc.Settings, doc.CanvasJson);

        if (roster is not null && roster.Version != doc.RosterVersion)
            warnings.Add(
                $"座位表关联名单版本 {doc.RosterVersion}，当前名单版本 {roster.Version}；已按序号+姓名重排约束。");

        if (doc.RosterSnapshot is not null && roster is not null)
        {
            var present = new HashSet<string>(roster.FieldsPresent, StringComparer.OrdinalIgnoreCase);
            foreach (var field in doc.RosterSnapshot.FieldsPresent)
            {
                if (present.Contains(field)) continue;
                warnings.Add($"云端座位表保存了「{FieldLabel(field)}」字段，当前名单没有提供，已留空。");
            }
        }

        var config = new SeatGridConfig(settings, students, assignments, warnings, constraints);
        return new SeatGridCloudResult(config, warnings, useSnapshot);

        string? NameAt(int index) =>
            snapshotNames.TryGetValue(index, out var value) ? value : null;
    }

    // ------------------------------ 内部映射 ------------------------------

    private static SeatGridSettingsDto SettingsFrom(SeatChartSettings s) => new()
    {
        Title = s.Title,
        BoldTitle = s.BoldTitle,
        HeadTeacher = s.HeadTeacher,
        Monitor = s.Monitor,
        View = s.View,
        Groups = s.Groups,
        Rows = s.Rows,
        Columns = s.Columns,
        CellScalePercent = s.CellScalePercent,
        PodiumGap = s.PodiumGap,
        NameFontSize = s.NameFontSize,
        RowGapPercent = s.RowGapPercent,
        ColumnGapPercent = s.ColumnGapPercent,
        GroupGapPercent = s.GroupGapPercent,
        TierRowGrowthPercent = s.TierRowGrowthPercent,
        ShowDoors = s.ShowDoors,
        PodiumLabel = s.PodiumLabel,
        PodiumWidthPercent = s.PodiumWidthPercent,
        PodiumHeight = s.PodiumHeight,
        BoldNames = s.BoldNames,
        HideEmptySeats = s.HideEmptySeats,
        MarkFemales = s.MarkFemales,
        SpaceTwoCharNames = s.SpaceTwoCharNames,
        ShowGroupRowHints = s.ShowGroupRowHints,
        SeatBackground = s.SeatBackground,
        EmptySeatBackground = s.EmptySeatBackground,
        SeatText = s.SeatText,
        SeatBorder = s.SeatBorder,
        PodiumBackground = s.PodiumBackground,
        PodiumText = s.PodiumText,
        GroupLabel = s.GroupLabel,
        RowLabel = s.RowLabel,
        FemaleMark = s.FemaleMark,
        ArrangeMode = s.ArrangeMode,
        WithinGroup = s.WithinGroup,
        MentorEnabled = s.MentorEnabled,
        MentorSeat = s.MentorSeat,
    };

    private static SeatChartSettings SettingsTo(SeatGridSettingsDto d, string canvasJson)
    {
        var settings = new SeatChartSettings
        {
            Title = d.Title,
            BoldTitle = d.BoldTitle,
            HeadTeacher = d.HeadTeacher,
            Monitor = d.Monitor,
            View = d.View,
            Groups = d.Groups,
            Rows = d.Rows,
            Columns = d.Columns,
            CellScalePercent = d.CellScalePercent,
            PodiumGap = d.PodiumGap,
            NameFontSize = d.NameFontSize,
            RowGapPercent = d.RowGapPercent,
            ColumnGapPercent = d.ColumnGapPercent,
            GroupGapPercent = d.GroupGapPercent,
            TierRowGrowthPercent = d.TierRowGrowthPercent,
            ShowDoors = d.ShowDoors,
            PodiumLabel = d.PodiumLabel,
            PodiumWidthPercent = d.PodiumWidthPercent,
            PodiumHeight = d.PodiumHeight,
            BoldNames = d.BoldNames,
            HideEmptySeats = d.HideEmptySeats,
            MarkFemales = d.MarkFemales,
            SpaceTwoCharNames = d.SpaceTwoCharNames,
            ShowGroupRowHints = d.ShowGroupRowHints,
            SeatBackground = d.SeatBackground,
            EmptySeatBackground = d.EmptySeatBackground,
            SeatText = d.SeatText,
            SeatBorder = d.SeatBorder,
            PodiumBackground = d.PodiumBackground,
            PodiumText = d.PodiumText,
            GroupLabel = d.GroupLabel,
            RowLabel = d.RowLabel,
            FemaleMark = d.FemaleMark,
            ArrangeMode = d.ArrangeMode,
            WithinGroup = d.WithinGroup,
            MentorEnabled = d.MentorEnabled,
            MentorSeat = d.MentorSeat,
            Canvas = SeatChartCanvasCodec.Decode(canvasJson),
        };
        // 刻意不调用 Normalize()：文档里的值就是 ToDocument 原样写出的，再夹取 / 补齐走廊
        // 会让画布与座位表不再逐字节一致（例如 RowCorridors 被补到 Rows 长度）。
        return settings;
    }

    private static SeatGridPairEntry Pair(
        StudentPair pair, Func<Guid, int> indexOf, Func<Guid, string> nameOf) => new()
    {
        IndexA = indexOf(pair.A),
        NameA = nameOf(pair.A),
        IndexB = indexOf(pair.B),
        NameB = nameOf(pair.B),
    };

    private static SeatGridZoneEntry Zone(
        Guid id, SeatRect rect, Func<Guid, int> indexOf, Func<Guid, string> nameOf) => new()
    {
        Index = indexOf(id),
        Name = nameOf(id),
        GroupFrom = rect.GroupFrom + 1,
        GroupTo = rect.GroupTo + 1,
        RowFrom = rect.RowFrom + 1,
        RowTo = rect.RowTo + 1,
        ColumnFrom = rect.ColumnFrom + 1,
        ColumnTo = rect.ColumnTo + 1,
    };

    private static ArrangeConstraints? ConstraintsFrom(
        SeatGridConstraintsDto dto,
        IReadOnlyList<Student> students,
        List<string> warnings)
    {
        var pins = new Dictionary<Guid, SeatPosition>();
        var mutex = new List<StudentPair>();
        var deskmates = new List<StudentPair>();
        var avoid = new List<StudentPair>();
        var allowed = new Dictionary<Guid, List<SeatRect>>();
        var denied = new Dictionary<Guid, List<SeatRect>>();

        foreach (var entry in dto.Pins)
        {
            var student = Resolve(entry.Index, entry.Name, students, warnings);
            if (student is null) continue;
            pins[student.Id] = new SeatPosition(entry.Group - 1, entry.Row - 1, entry.Column - 1);
        }

        foreach (var entry in dto.Mutex)
            AddPair(entry, mutex);
        foreach (var entry in dto.Deskmates)
            AddPair(entry, deskmates);
        foreach (var entry in dto.AvoidDeskmates)
            AddPair(entry, avoid);

        foreach (var entry in dto.ZoneAllow)
            AddZone(entry, allowed);
        foreach (var entry in dto.ZoneDeny)
            AddZone(entry, denied);

        if (pins.Count == 0 && mutex.Count == 0 && deskmates.Count == 0 &&
            avoid.Count == 0 && allowed.Count == 0 && denied.Count == 0)
            return null;

        var zones = new Dictionary<Guid, StudentZone>();
        foreach (var id in allowed.Keys.Union(denied.Keys))
            zones[id] = new StudentZone(
                allowed.GetValueOrDefault(id, new List<SeatRect>()),
                denied.GetValueOrDefault(id, new List<SeatRect>()));

        return new ArrangeConstraints
        {
            Pins = pins,
            Mutex = mutex,
            Deskmates = deskmates,
            AvoidDeskmates = avoid,
            Zones = zones,
            Seed = dto.Seed,
            MentorEnabled = dto.MentorEnabled,
            MentorSeat = dto.MentorSeat,
        };

        void AddPair(SeatGridPairEntry entry, List<StudentPair> target)
        {
            var a = Resolve(entry.IndexA, entry.NameA, students, warnings);
            var b = Resolve(entry.IndexB, entry.NameB, students, warnings);
            if (a is not null && b is not null && a.Id != b.Id)
                target.Add(StudentPair.Create(a.Id, b.Id));
        }

        void AddZone(SeatGridZoneEntry entry, Dictionary<Guid, List<SeatRect>> target)
        {
            var student = Resolve(entry.Index, entry.Name, students, warnings);
            if (student is null) return;
            var rect = new SeatRect(
                entry.GroupFrom - 1, entry.GroupTo - 1,
                entry.RowFrom - 1, entry.RowTo - 1,
                entry.ColumnFrom - 1, entry.ColumnTo - 1);
            if (!target.TryGetValue(student.Id, out var list))
                target[student.Id] = list = new List<SeatRect>();
            list.Add(rect);
        }
    }

    /// <summary>
    /// 序号在范围内且姓名一致 → 命中；否则按姓名唯一匹配；再不行就丢弃并告警。
    /// 姓名来自内嵌快照（若存在），这样名单顺序变了也能把约束挂回原来那个人。
    /// </summary>
    private static Student? Resolve(
        int index, string? expectedName, IReadOnlyList<Student> students, List<string> warnings)
    {
        if (index >= 1 && index <= students.Count)
        {
            var candidate = students[index - 1];
            if (expectedName is null || string.Equals(candidate.Name, expectedName, StringComparison.Ordinal))
                return candidate;
        }

        if (!string.IsNullOrEmpty(expectedName))
        {
            var matches = students
                .Where(s => string.Equals(s.Name, expectedName, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                warnings.Add($"「{expectedName}」在名单里有 {matches.Count} 个同名，无法确定对应关系，已忽略该条。");
                return null;
            }
        }

        warnings.Add(
            $"名单里找不到序号 {index}{(expectedName is null ? string.Empty : $"（{expectedName}）")}对应的学生，相关座位或约束已忽略。");
        return null;
    }

    private static string FieldLabel(string field) => field.ToLowerInvariant() switch
    {
        "height" => "身高",
        "vision" => "视力",
        "score" => "成绩",
        "notes" => "备注",
        "originalgroup" or "original_group" => "原小组",
        _ => field,
    };
}
