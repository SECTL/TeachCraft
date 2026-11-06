using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Export;

public sealed record SeatGridConfig(
    SeatChartSettings Settings,
    List<Student> Students,
    Dictionary<SeatPosition, Student> Assignments,
    List<string> Warnings);

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

    public static void Export(
        string path,
        SeatChartSettings settings,
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<SeatPosition, Student> assignments)
    {
        using var workbook = new XLWorkbook();
        WriteSettingsSheet(workbook, settings);
        WriteRosterSheet(workbook, students, assignments, withIndex: false);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        workbook.SaveAs(path);
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
            return new SeatGridConfig(settings, students, assignments, warnings);
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
