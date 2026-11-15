using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SeatGrid.Core.Models;
using SeatGrid.Core.Seating;

namespace SeatGrid.Core.Export;

public sealed record SeatGridConfig(
    SeatChartSettings Settings,
    List<Student> Students,
    Dictionary<SeatPosition, Student> Assignments,
    List<string> Warnings,
    ArrangeConstraints? Constraints = null);

/// <summary>
/// 「导出配置 / 导入配置」使用的 xlsx：设置项 + 名单 + 座位。
/// 「座位设置」表 A1 固定带有 SeatGrid Config v1 标记，改结构时要同步升级标记。
/// 「导出 Excel」的座位表也附一张隐藏的「座位设置」，因此同样能导回继续修改；
/// 没有这张表的旧版座位表，按名单页的组 / 排 / 列还原座位并推算布局。
/// </summary>
public static class SeatGridConfigIO
{
    private const string Marker = "SeatGrid Config v1";
    internal const string SettingsSheetName = "座位设置";
    internal const string RosterSheetName = "名单";
    internal const string ChartSheetName = "座位表";
    internal const string ConstraintSheetName = "座位约束";

    public static void Export(
        string path,
        SeatChartSettings settings,
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<SeatPosition, Student> assignments,
        ArrangeConstraints? constraints = null)
    {
        using var workbook = new XLWorkbook();
        WriteSettingsSheet(workbook, settings);
        WriteRosterSheet(workbook, students, assignments, withIndex: false);
        if (constraints is { IsEmpty: false })
            WriteConstraintSheet(workbook, constraints, students);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        workbook.SaveAs(path);
    }

    /// <summary>
    /// 写「座位约束」表（隐藏）。12 列：类型 / 学生A / 学生B / 组起止 / 排起止 / 列起止 / 说明 / 名单序号。
    /// 学生用「名单序号 + 姓名」双校验定位——Student.Id 每次导入都重新生成，绝不能当持久键。
    /// </summary>
    internal static IXLWorksheet WriteConstraintSheet(
        XLWorkbook workbook, ArrangeConstraints constraints, IReadOnlyList<Student> students)
    {
        var sheet = workbook.Worksheets.Add(ConstraintSheetName);
        var indexOf = new Dictionary<Guid, int>();
        for (var i = 0; i < students.Count; i++) indexOf[students[i].Id] = i + 1;
        var nameOf = students.ToDictionary(s => s.Id, s => s.Name);

        string[] headers =
        {
            "类型", "学生A", "学生B", "组起", "组止", "排起", "排止", "列起", "列止", "说明", "序号A", "序号B",
        };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4F2EC");
        }

        var row = 1;
        void Add(string type, Guid? a, Guid? b, string note, SeatRect? rect = null)
        {
            row++;
            sheet.Cell(row, 1).Value = type;
            if (a is Guid idA)
            {
                sheet.Cell(row, 2).Value = nameOf.GetValueOrDefault(idA, string.Empty);
                sheet.Cell(row, 11).Value = indexOf.GetValueOrDefault(idA, 0);
            }

            if (b is Guid idB)
            {
                sheet.Cell(row, 3).Value = nameOf.GetValueOrDefault(idB, string.Empty);
                sheet.Cell(row, 12).Value = indexOf.GetValueOrDefault(idB, 0);
            }

            if (rect is SeatRect r)
            {
                sheet.Cell(row, 4).Value = r.GroupFrom + 1;
                sheet.Cell(row, 5).Value = r.GroupTo + 1;
                sheet.Cell(row, 6).Value = r.RowFrom + 1;
                sheet.Cell(row, 7).Value = r.RowTo + 1;
                sheet.Cell(row, 8).Value = r.ColumnFrom + 1;
                sheet.Cell(row, 9).Value = r.ColumnTo + 1;
            }

            sheet.Cell(row, 10).Value = note;
        }

        foreach (var (id, seat) in constraints.Pins)
            Add("lock", id, null, "锁定座位",
                new SeatRect(seat.Group, seat.Group, seat.Row, seat.Row, seat.Column, seat.Column));

        foreach (var pair in constraints.Mutex) Add("mutex", pair.A, pair.B, "绝不相邻");
        foreach (var pair in constraints.Deskmates) Add("deskmate", pair.A, pair.B, "必须同桌");
        foreach (var pair in constraints.AvoidDeskmates) Add("avoid-deskmate", pair.A, pair.B, "上期同桌");

        foreach (var (id, zone) in constraints.Zones)
        {
            foreach (var rect in zone.Allowed) Add("zone-allow", id, null, "允许区域", rect);
            foreach (var rect in zone.Denied) Add("zone-deny", id, null, "禁止区域", rect);
        }

        sheet.Hide();
        return sheet;
    }

    /// <summary>写「座位设置」表（A1 为版本标记）。座位表导出也调用它，并把它隐藏起来。</summary>
    internal static IXLWorksheet WriteSettingsSheet(XLWorkbook workbook, SeatChartSettings settings)
    {
        var source = settings.Clone();
        source.Normalize();

        var sheet = workbook.Worksheets.Add(SettingsSheetName);

        sheet.Range(1, 1, 1, 2).Merge();
        var markerCell = sheet.Cell(1, 1);
        markerCell.Value = Marker;
        markerCell.Style.Font.FontName = "Microsoft YaHei";
        markerCell.Style.Font.Bold = true;
        markerCell.Style.Font.FontSize = 13;
        markerCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F7F4EB");
        markerCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        sheet.Cell(3, 1).Value = "设置项";
        sheet.Cell(3, 2).Value = "值";
        sheet.Range(3, 1, 3, 2).Style.Font.Bold = true;
        sheet.Range(3, 1, 3, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#F4F2EC");

        var row = 3;

        void Add(string key, XLCellValue value)
        {
            row++;
            var keyCell = sheet.Cell(row, 1);
            var valueCell = sheet.Cell(row, 2);
            keyCell.Value = key;
            valueCell.Value = value;
            keyCell.Style.Font.FontName = "Microsoft YaHei";
            valueCell.Style.Font.FontName = "Microsoft YaHei";
            keyCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            valueCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            keyCell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E7E3D8");
            valueCell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E7E3D8");
        }

        Add("View", source.View == ViewMode.Teacher ? "teacher" : "student");
        Add("Groups", source.Groups);
        Add("Rows", source.Rows);
        Add("Columns", source.Columns);
        Add("CellScalePercent", source.CellScalePercent);
        Add("PodiumGap", source.PodiumGap);
        Add("NameFontSize", source.NameFontSize);
        Add("Title", source.Title);
        Add("BoldTitle", source.BoldTitle);
        Add("HeadTeacher", source.HeadTeacher);
        Add("Monitor", source.Monitor);
        Add("BoldNames", source.BoldNames);
        Add("HideEmptySeats", source.HideEmptySeats);
        Add("MarkFemales", source.MarkFemales);
        Add("SpaceTwoCharNames", source.SpaceTwoCharNames);
        Add("ShowGroupRowHints", source.ShowGroupRowHints);
        Add("SeatBackground", source.SeatBackground);
        Add("EmptySeatBackground", source.EmptySeatBackground);
        Add("SeatText", source.SeatText);
        Add("SeatBorder", source.SeatBorder);
        Add("PodiumBackground", source.PodiumBackground);
        Add("PodiumText", source.PodiumText);
        Add("GroupLabel", source.GroupLabel);
        Add("RowLabel", source.RowLabel);
        Add("FemaleMark", source.FemaleMark);
        Add("ArrangeMode", source.ArrangeMode.ToString());
        Add("WithinGroupOrder", source.WithinGroup.ToString());
        Add("Canvas", SeatChartCanvasCodec.Encode(source.Canvas));
        Add("RowGapPercent", source.RowGapPercent);
        Add("ColumnGapPercent", source.ColumnGapPercent);
        Add("GroupGapPercent", source.GroupGapPercent);
        Add("TierRowGrowthPercent", source.TierRowGrowthPercent);
        Add("ShowDoors", source.ShowDoors);
        Add("PodiumLabel", source.PodiumLabel);
        Add("PodiumWidthPercent", source.PodiumWidthPercent);
        Add("PodiumHeight", source.PodiumHeight);

        sheet.Column(1).Width = 26;
        sheet.Column(2).Width = 34;
        return sheet;
    }

    /// <summary>
    /// 写「名单」表，组 / 排 / 列为 1 基物理座位；未排座的学生这三列留空。
    /// 座位表导出加 withIndex，在最前多一列「序号」方便打印核对。
    /// </summary>
    internal static void WriteRosterSheet(
        XLWorkbook workbook,
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<SeatPosition, Student> assignments,
        bool withIndex)
    {
        var roster = workbook.Worksheets.Add(RosterSheetName);
        var fields = new[] { "姓名", "性别", "学号", "身高", "视力", "成绩", "原小组", "备注", "组", "排", "列" };
        var headers = withIndex ? fields.Prepend("序号").ToArray() : fields;

        for (var i = 0; i < headers.Length; i++)
        {
            var cell = roster.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.FontName = "Microsoft YaHei";
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4F2EC");
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#E7E3D8");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var seatLookup = assignments
            .GroupBy(pair => pair.Value.Id)
            .ToDictionary(g => g.Key, g => g.First().Key);

        var offset = withIndex ? 1 : 0;
        for (var index = 0; index < students.Count; index++)
        {
            var student = students[index];
            var rowIndex = index + 2;
            var seated = seatLookup.TryGetValue(student.Id, out var seat);
            void Set(int field, XLCellValue value) => roster.Cell(rowIndex, field + offset).Value = value;

            if (withIndex) roster.Cell(rowIndex, 1).Value = index + 1;
            Set(1, student.Name);
            Set(2, student.GenderLabel);
            Set(3, student.Number);
            Set(4, student.Height is null ? string.Empty : student.Height.Value.ToString("0.#"));
            Set(5, student.Vision is null ? string.Empty : student.Vision.Value.ToString("0.0"));
            Set(6, student.Score is null ? string.Empty : student.Score.Value.ToString("0.##"));
            Set(7, student.OriginalGroup);
            Set(8, student.Notes);
            // TryGetValue 失败时 seat 是 (0,0,0)：直接写会把未排座的学生写成「1 组 1 排 1 列」。
            Set(9, seated ? seat.Group + 1 : string.Empty);
            Set(10, seated ? seat.Row + 1 : string.Empty);
            Set(11, seated ? seat.Column + 1 : string.Empty);
        }

        roster.Columns().AdjustToContents();
        roster.SheetView.FreezeRows(1);
    }

    public static SeatGridConfig Import(string path)
    {
        if (!File.Exists(path))
            throw new SeatGridException("找不到配置文件，请重新选择。");

        using var workbook = new XLWorkbook(path);
        return ReadWorkbook(workbook)
               ?? throw new SeatGridException("这不是「归位」导出的配置文件或座位表，请选择正确的 xlsx。");
    }

    /// <summary>
    /// 「导入名单」先用它探一下：是本软件导出的配置或座位表就整体还原（名单 + 座位 + 设置），
    /// 否则返回 null，交给普通名单识别。打不开的文件也返回 null，由名单识别给出具体错误。
    /// </summary>
    public static SeatGridConfig? TryImport(string path)
    {
        if (!File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return null;

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(path);
        }
        catch (Exception)
        {
            return null;
        }

        using (workbook)
            return ReadWorkbook(workbook);
    }

    private static SeatGridConfig? ReadWorkbook(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == SettingsSheetName)
                    ?? workbook.Worksheets.FirstOrDefault();
        if (sheet is null) return null;

        var warnings = new List<string>();
        if (HasMarker(sheet))
        {
            var settings = ReadSettings(sheet);
            var (students, assignments) = ReadRoster(workbook, settings.GetLayout(), warnings, accept: null);
            var constraints = ReadConstraints(workbook, students, settings.GetLayout(), warnings);
            return new SeatGridConfig(settings, students, assignments, warnings, constraints);
        }

        if (InferLegacyChartSettings(workbook, warnings) is { } legacy)
        {
            var (students, assignments) = ReadRoster(workbook, legacy.Settings.GetLayout(), warnings, legacy.Accept);
            return new SeatGridConfig(legacy.Settings, students, assignments, warnings);
        }

        return null;
    }

    private static bool HasMarker(IXLWorksheet sheet) =>
        string.Equals(sheet.Cell(1, 1).GetFormattedString().Trim(), Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读「座位约束」表。学生按「序号 + 姓名」双校验定位；序号对不上就按姓名唯一匹配；
    /// 仍找不到就整行丢弃并 warning——绝不把规则挂到错的人头上。
    /// </summary>
    private static ArrangeConstraints? ReadConstraints(
        XLWorkbook workbook, IReadOnlyList<Student> students, SeatChartLayout layout, List<string> warnings)
    {
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == ConstraintSheetName);
        if (sheet is null) return null;

        var byIndex = students.ToList();
        var byName = new Dictionary<string, List<Student>>(StringComparer.Ordinal);
        foreach (var student in students)
        {
            if (!byName.TryGetValue(student.Name, out var list))
                byName[student.Name] = list = new List<Student>();
            list.Add(student);
        }

        Student? Resolve(int indexCell, string name)
        {
            if (indexCell >= 1 && indexCell <= byIndex.Count)
            {
                var candidate = byIndex[indexCell - 1];
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) return candidate;
            }

            return byName.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;
        }

        var pins = new Dictionary<Guid, SeatPosition>();
        var mutex = new List<StudentPair>();
        var deskmates = new List<StudentPair>();
        var avoid = new List<StudentPair>();
        var zones = new Dictionary<Guid, StudentZone>();
        var allowed = new Dictionary<Guid, List<SeatRect>>();
        var denied = new Dictionary<Guid, List<SeatRect>>();

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 2; row <= lastRow; row++)
        {
            var type = sheet.Cell(row, 1).GetFormattedString().Trim();
            if (type.Length == 0) continue;

            var nameA = sheet.Cell(row, 2).GetFormattedString().Trim();
            var nameB = sheet.Cell(row, 3).GetFormattedString().Trim();
            var indexA = (int)(Number(sheet, row, 11) ?? 0);
            var indexB = (int)(Number(sheet, row, 12) ?? 0);

            var studentA = nameA.Length == 0 ? null : Resolve(indexA, nameA);
            var studentB = nameB.Length == 0 ? null : Resolve(indexB, nameB);

            if (nameA.Length > 0 && studentA is null)
            {
                warnings.Add($"「{nameA}」已不在名单里，相关约束已忽略。");
                continue;
            }

            if (nameB.Length > 0 && studentB is null)
            {
                warnings.Add($"「{nameB}」已不在名单里，相关约束已忽略。");
                continue;
            }

            switch (type)
            {
                case "lock" when studentA is not null:
                {
                    var rect = ReadRect(sheet, row, layout);
                    if (rect is not null)
                        pins[studentA.Id] = new SeatPosition(rect.Value.GroupFrom, rect.Value.RowFrom,
                            rect.Value.ColumnFrom);
                    break;
                }

                case "mutex" when studentA is not null && studentB is not null:
                    mutex.Add(StudentPair.Create(studentA.Id, studentB.Id));
                    break;

                case "deskmate" when studentA is not null && studentB is not null:
                    deskmates.Add(StudentPair.Create(studentA.Id, studentB.Id));
                    break;

                case "avoid-deskmate" when studentA is not null && studentB is not null:
                    avoid.Add(StudentPair.Create(studentA.Id, studentB.Id));
                    break;

                case "zone-allow" when studentA is not null:
                    if (ReadRect(sheet, row, layout) is { } allowRect)
                    {
                        if (!allowed.TryGetValue(studentA.Id, out var list))
                            allowed[studentA.Id] = list = new List<SeatRect>();
                        list.Add(allowRect);
                    }

                    break;

                case "zone-deny" when studentA is not null:
                    if (ReadRect(sheet, row, layout) is { } denyRect)
                    {
                        if (!denied.TryGetValue(studentA.Id, out var list))
                            denied[studentA.Id] = list = new List<SeatRect>();
                        list.Add(denyRect);
                    }

                    break;
            }
        }

        foreach (var id in allowed.Keys.Union(denied.Keys))
        {
            zones[id] = new StudentZone(
                allowed.GetValueOrDefault(id, new List<SeatRect>()),
                denied.GetValueOrDefault(id, new List<SeatRect>()));
        }

        if (pins.Count == 0 && mutex.Count == 0 && deskmates.Count == 0 && avoid.Count == 0 && zones.Count == 0)
            return null;

        return new ArrangeConstraints
        {
            Pins = pins,
            Mutex = mutex,
            Deskmates = deskmates,
            AvoidDeskmates = avoid,
            Zones = zones,
        };
    }

    /// <summary>读 1 基的组/排/列六格并转成 0 基矩形；全空返回 null。</summary>
    private static SeatRect? ReadRect(IXLWorksheet sheet, int row, SeatChartLayout layout)
    {
        var groupFrom = Number(sheet, row, 4);
        var groupTo = Number(sheet, row, 5);
        var rowFrom = Number(sheet, row, 6);
        var rowTo = Number(sheet, row, 7);
        var columnFrom = Number(sheet, row, 8);
        var columnTo = Number(sheet, row, 9);

        if (groupFrom is null && rowFrom is null && columnFrom is null) return null;

        var rect = new SeatRect(
            (int)(groupFrom ?? 1) - 1, (int)(groupTo ?? groupFrom ?? 1) - 1,
            (int)(rowFrom ?? 1) - 1, (int)(rowTo ?? rowFrom ?? 1) - 1,
            (int)(columnFrom ?? 1) - 1, (int)(columnTo ?? columnFrom ?? 1) - 1);

        return rect.Normalize(layout);
    }

    private static SeatChartSettings ReadSettings(IXLWorksheet sheet)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

        for (var row = 3; row <= lastRow; row++)
        {
            var key = sheet.Cell(row, 1).GetFormattedString().Trim();
            if (key.Length == 0 || key == "设置项") continue;
            values[key] = sheet.Cell(row, 2).GetFormattedString().Trim();
        }

        var settings = new SeatChartSettings
        {
            View = String.Equals(Get(values, "View"), "student", StringComparison.OrdinalIgnoreCase)
                ? ViewMode.Student
                : ViewMode.Teacher,
            Groups = GetInt(values, "Groups", 4),
            Rows = GetInt(values, "Rows", 6),
            Columns = GetInt(values, "Columns", 2),
            CellScalePercent = GetInt(values, "CellScalePercent", 100),
            PodiumGap = GetInt(values, "PodiumGap", 16),
            NameFontSize = GetInt(values, "NameFontSize", 14),
            Title = Get(values, "Title", "班级座位表"),
            BoldTitle = GetBool(values, "BoldTitle", true),
            HeadTeacher = Get(values, "HeadTeacher"),
            Monitor = Get(values, "Monitor"),
            BoldNames = GetBool(values, "BoldNames", false),
            HideEmptySeats = GetBool(values, "HideEmptySeats", false),
            MarkFemales = GetBool(values, "MarkFemales", true),
            SpaceTwoCharNames = GetBool(values, "SpaceTwoCharNames", true),
            ShowGroupRowHints = GetBool(values, "ShowGroupRowHints", true),
            SeatBackground = Get(values, "SeatBackground", "#FFFFFF"),
            EmptySeatBackground = Get(values, "EmptySeatBackground", "#FAF9F5"),
            SeatText = Get(values, "SeatText", "#141413"),
            SeatBorder = Get(values, "SeatBorder", "#E7E3D8"),
            PodiumBackground = Get(values, "PodiumBackground", "#F4F2EC"),
            PodiumText = Get(values, "PodiumText", "#85806F"),
            GroupLabel = Get(values, "GroupLabel", "#C6613F"),
            RowLabel = Get(values, "RowLabel", "#85806F"),
            FemaleMark = Get(values, "FemaleMark", "#C6613F"),
            ArrangeMode = GetEnum(values, "ArrangeMode", ArrangeMode.RosterOrder),
            WithinGroup = GetEnum(values, "WithinGroupOrder", WithinGroupOrder.Fold),
            Canvas = SeatChartCanvasCodec.Decode(Get(values, "Canvas")),
            RowGapPercent = GetInt(values, "RowGapPercent", 7),
            ColumnGapPercent = GetInt(values, "ColumnGapPercent", 7),
            GroupGapPercent = GetInt(values, "GroupGapPercent", 28),
            TierRowGrowthPercent = GetInt(values, "TierRowGrowthPercent", 0),
            ShowDoors = GetBool(values, "ShowDoors", true),
            PodiumLabel = Get(values, "PodiumLabel", "讲　台"),
            PodiumWidthPercent = GetInt(values, "PodiumWidthPercent", 55),
            PodiumHeight = GetInt(values, "PodiumHeight", 40),
        };
        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// 读「名单」表与座位。accept 用来过滤可疑座位：旧版导出把未排座的学生写成 1 组 1 排 1 列，
    /// 被 accept 拒掉的座位按未排座处理、不报警告。
    /// </summary>
    private static (List<Student> Students, Dictionary<SeatPosition, Student> Assignments) ReadRoster(
        XLWorkbook workbook, SeatChartLayout layout, List<string> warnings, Func<string, SeatPosition, bool>? accept)
    {
        var students = new List<Student>();
        var assignments = new Dictionary<SeatPosition, Student>();

        var roster = workbook.Worksheets.FirstOrDefault(w => w.Name == RosterSheetName);
        if (roster is not null)
        {
            var rosterLastRow = roster.LastRowUsed()?.RowNumber() ?? 0;
            var columns = ReadRosterHeader(roster);

            if (columns.Name < 0)
                throw new SeatGridException("配置文件里的「名单」表缺少姓名列。");

            for (var row = 2; row <= rosterLastRow; row++)
            {
                var name = roster.Cell(row, columns.Name).GetFormattedString().Trim();
                if (name.Length == 0) continue;

                var student = new Student
                {
                    Name = name,
                    Gender = ParseGender(roster.Cell(row, columns.Gender).GetFormattedString()),
                    Number = Text(roster, row, columns.Number),
                    Height = Number(roster, row, columns.Height, 0.1),
                    Vision = Number(roster, row, columns.Vision, 0.1),
                    Score = Number(roster, row, columns.Score, 0.01),
                    OriginalGroup = Text(roster, row, columns.OriginalGroup),
                    Notes = Text(roster, row, columns.Notes),
                };
                students.Add(student);

                var group = Number(roster, row, columns.Group, 1);
                var seatRow = Number(roster, row, columns.Row, 1);
                var column = Number(roster, row, columns.Column, 1);

                if (group is null || seatRow is null || column is null) continue;

                var position = new SeatPosition((int)group.Value - 1, (int)seatRow.Value - 1, (int)column.Value - 1);
                if (accept is not null && !accept(name, position)) continue;
                if (!layout.Contains(position))
                {
                    warnings.Add($"{name} 的座位（{position}）超出了当前布局，已保留为未安排。");
                    continue;
                }

                if (assignments.ContainsKey(position))
                {
                    warnings.Add($"{name} 的座位（{position}）与其他同学重复，已保留为未安排。");
                    continue;
                }

                assignments[position] = student;
            }
        }

        return (students, assignments);
    }

    /// <summary>
    /// 旧版「导出 Excel」没有「座位设置」表：认出座位表 + 带组 / 排 / 列的名单页，
    /// 从座位表推算布局、视角、标题、班主任与班长。旧版没存配色与字号，这些用默认值并提示。
    /// 返回的 Accept 只放行真的出现在座位表上的「1 组 1 排 1 列」，挡掉旧版把未排座写成 1/1/1 的学生。
    /// </summary>
    private static (SeatChartSettings Settings, Func<string, SeatPosition, bool> Accept)? InferLegacyChartSettings(
        XLWorkbook workbook, List<string> warnings)
    {
        var chart = workbook.Worksheets.FirstOrDefault(w => w.Name == ChartSheetName);
        var roster = workbook.Worksheets.FirstOrDefault(w => w.Name == RosterSheetName);
        if (chart is null || roster is null) return null;

        var columns = ReadRosterHeader(roster);
        if (columns.Name < 0 || columns.Group < 0 || columns.Row < 0 || columns.Column < 0) return null;

        var settings = new SeatChartSettings();
        var title = chart.Cell(1, 1).GetFormattedString().Trim();
        if (title.Length > 0) settings.Title = title;
        settings.BoldTitle = chart.Cell(1, 1).Style.Font.Bold;
        foreach (var part in chart.Cell(2, 1).GetFormattedString().Split('　', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("班主任：", StringComparison.Ordinal)) settings.HeadTeacher = part["班主任：".Length..].Trim();
            else if (part.StartsWith("班长：", StringComparison.Ordinal)) settings.Monitor = part["班长：".Length..].Trim();
        }

        int? podiumRow = null;
        var groupLabels = 0;
        var rowLabels = 0;
        var columnsPerGroup = 0;
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in chart.CellsUsed().Where(c => c.Address.RowNumber >= 3))
        {
            var text = Normalize(cell.GetFormattedString());
            if (text.Length == 0) continue;

            if (text == "讲台") podiumRow = cell.Address.RowNumber;
            else if (Regex.IsMatch(text, @"^第\d+组$"))
            {
                groupLabels++;
                columnsPerGroup = Math.Max(columnsPerGroup, cell.IsMerged() ? cell.MergedRange().ColumnCount() : 1);
            }
            else if (Regex.IsMatch(text, @"^第\d+排$")) rowLabels++;
            else shown.Add(text);
        }

        // 讲台在第 3 行（标题、信息行之后紧接着）即学生视角，否则在座位下方为教师视角。
        settings.View = podiumRow == 3 ? ViewMode.Student : ViewMode.Teacher;
        settings.ShowGroupRowHints = groupLabels > 0;

        int Max(int column) => roster.RowsUsed().Skip(1)
            .Select(r => (int)(Number(roster, r.RowNumber(), column) ?? 0)).DefaultIfEmpty(0).Max();
        settings.Groups = Math.Max(groupLabels, Max(columns.Group));
        settings.Rows = Math.Max(rowLabels, Max(columns.Row));
        settings.Columns = Math.Max(columnsPerGroup, Max(columns.Column));
        settings.Normalize();

        warnings.Add("这是旧版导出的座位表：已按名单页还原座位；配色、字号与显示选项当时没有保存，已用默认值。");
        if (groupLabels == 0)
            warnings.Add("组数、排数与每组人数按名单推算：如果最后几排或最右的组原本是空座位，请在右侧面板补回。");

        var origin = new SeatPosition(0, 0, 0);
        return (settings, (name, position) => position != origin || shown.Contains(Normalize(name)));
    }

    private static RosterColumns ReadRosterHeader(IXLWorksheet sheet)
    {
        var columns = new RosterColumns();
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (var column = 1; column <= lastColumn; column++)
        {
            var header = Normalize(sheet.Cell(1, column).GetFormattedString());
            if (header.Length == 0) continue;

            if (columns.Name < 0 && (header.Contains("姓名") || header == "名字")) columns.Name = column;
            else if (columns.Gender < 0 && header.Contains("性别")) columns.Gender = column;
            else if (columns.Number < 0 && (header.Contains("学号") || header.Contains("编号"))) columns.Number = column;
            else if (columns.Height < 0 && header.Contains("身高")) columns.Height = column;
            else if (columns.Vision < 0 && header.Contains("视力")) columns.Vision = column;
            else if (columns.Score < 0 && (header.Contains("成绩") || header.Contains("总分"))) columns.Score = column;
            else if (columns.OriginalGroup < 0 && (header.Contains("原小组") || header == "小组" || header.Contains("组别"))) columns.OriginalGroup = column;
            else if (columns.Notes < 0 && header.Contains("备注")) columns.Notes = column;
            else if (columns.Group < 0 && header == "组") columns.Group = column;
            else if (columns.Row < 0 && header == "排") columns.Row = column;
            else if (columns.Column < 0 && (header == "列" || header == "座位列")) columns.Column = column;
        }

        return columns;
    }

    private static string Text(IXLWorksheet sheet, int row, int column) =>
        column < 0 ? string.Empty : sheet.Cell(row, column).GetFormattedString().Trim();

    private static double? Number(IXLWorksheet sheet, int row, int column, double? _) =>
        Number(sheet, row, column);

    private static double? Number(IXLWorksheet sheet, int row, int column)
    {
        if (column < 0) return null;

        var text = sheet.Cell(row, column).GetFormattedString().Trim().Replace(',', '.');
        if (text.Length == 0) return null;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static Gender ParseGender(string value) => value.Trim() switch
    {
        "男" or "男生" or "M" or "m" => Gender.Male,
        "女" or "女生" or "F" or "f" => Gender.Female,
        _ => Gender.Unknown,
    };

    private static string Normalize(string value)
    {
        var text = string.Join(string.Empty, value.Where(c => !char.IsWhiteSpace(c)));
        return text.Replace("（", string.Empty).Replace("）", string.Empty)
            .Replace("(", string.Empty).Replace(")", string.Empty)
            .Replace(":", string.Empty).Replace("：", string.Empty)
            .Trim();
    }

    private static string Get(Dictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    private static int GetInt(Dictionary<string, string> values, string key, int fallback) =>
        int.TryParse(Get(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
    {
        var value = Get(values, key);
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value switch
        {
            "1" or "是" => true,
            "0" or "否" => false,
            _ => fallback,
        };
    }

    /// <summary>读枚举：按名字解析（不依赖数值，避免枚举顺序变化时错位），失败回落默认值。</summary>
    private static T GetEnum<T>(Dictionary<string, string> values, string key, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(Get(values, key), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    private sealed class RosterColumns
    {
        public int Name { get; set; } = -1;
        public int Gender { get; set; } = -1;
        public int Number { get; set; } = -1;
        public int Height { get; set; } = -1;
        public int Vision { get; set; } = -1;
        public int Score { get; set; } = -1;
        public int OriginalGroup { get; set; } = -1;
        public int Notes { get; set; } = -1;
        public int Group { get; set; } = -1;
        public int Row { get; set; } = -1;
        public int Column { get; set; } = -1;
    }
}
