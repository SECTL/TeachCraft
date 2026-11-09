using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MeetRoster.Core.Models;

namespace MeetRoster.Core.Import;

public sealed class OrderBookImportResult
{
    public required MeetProject Project { get; init; }
    public required IReadOnlyList<RegistrationIssue> Issues { get; init; }
    public required IReadOnlyList<string> ClassNames { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? SourcePath { get; init; }
    /// <summary>是否读到了「竞赛日程」；没有日程时只导入名单与报名。</summary>
    public bool HasSchedule { get; init; }
    /// <summary>是否读到了「代表队名单」；没有名单时只有赛程。</summary>
    public bool HasRoster { get; init; }
    public int AthleteCount { get; init; }
}

/// <summary>
/// 秩序册一次导入：一份工作簿里同时有「代表队名单」（号码 + 姓名 + 性别）、
/// 「竞赛日程」与「项目分组表」（谁报了哪一项、第几组第几道）。
/// 与报名表导入的区别：报名表按班一人一份，秩序册是全校一本；这里把三者合成
/// 同一个 <see cref="MeetProject"/>，让赛程、高亮与导出都能直接用。
/// </summary>
public static class OrderBookImporter
{
    private static readonly Regex BlockHeader = new(
        @"^(?<grade>七|八|九|高一|高二|高三)年级?\s*(?<number>\d+)\s*班",
        RegexOptions.Compiled);

    public static OrderBookImportResult Import(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("找不到秩序册文件：" + path);

        using var stream = WorkbookIO.OpenReadShared(path);
        using var workbook = WorkbookIO.OpenWorkbook(path, stream);
        var issues = new List<RegistrationIssue>();

        var project = new MeetProject
        {
            Title = TitleFromFileName(path),
            Venue = "田径场",
            Note = "由秩序册导入：号码、姓名与项目来自「代表队名单 + 项目分组表」；团体项目按班登记，不落到个人。",
        };

        var rosterSheet = FindSheet(workbook, "代表队名单") ?? FindSheet(workbook, "名单");
        var hasRoster = rosterSheet is not null && ReadRoster(rosterSheet, project, issues);
        if (!hasRoster)
        {
            // 没有代表队名单时，退到「参赛队统计」只建班级，避免整份文件读不出来。
            var statsSheet = FindSheet(workbook, "参赛队统计") ?? FindSheet(workbook, "统计");
            if (statsSheet is not null) ReadTeamStatistics(statsSheet, project);
        }

        var days = new List<MeetDay>();
        var hasSchedule = false;
        try
        {
            var schedule = ScheduleWorkbookImporter.Import(path);
            days = schedule.Days.ToList();
            hasSchedule = days.Count > 0;
            issues.AddRange(schedule.Issues);
        }
        catch (InvalidDataException ex)
        {
            issues.Add(new RegistrationIssue(ex.Message, false));
        }

        ApplyRegistration(project, days, issues);

        foreach (var day in days)
            project.Days.Add(day);

        FillEventCatalog(project);
        NoteMissingGrouping(project, days, issues);

        var classNames = project.Classes
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var athleteCount = project.Classes.Sum(c => c.Athletes.Count);
        if (athleteCount == 0 && !hasSchedule)
            throw new InvalidDataException(
                "这份工作簿里没有读到「代表队名单」或「竞赛日程」。秩序册需要至少包含其中一张表。");

        issues.Add(new RegistrationIssue(
            hasRoster && hasSchedule
                ? $"已读取 {project.Classes.Count} 个班 · {athleteCount} 名运动员 · {days.Count} 天赛程。"
                : hasRoster
                    ? $"已读取 {project.Classes.Count} 个班 · {athleteCount} 名运动员；没有找到「竞赛日程」，赛程页将为空。"
                    : "没有找到「代表队名单」，只导入了赛程与分组名单。",
            false));

        return new OrderBookImportResult
        {
            Project = project,
            Issues = Deduplicate(issues),
            ClassNames = classNames,
            Title = project.Title,
            SourcePath = Path.GetFullPath(path),
            HasSchedule = hasSchedule,
            HasRoster = hasRoster,
            AthleteCount = athleteCount,
        };
    }

    /// <summary>只读工作簿里的表名，供 UI 在导入前判断这是不是一份秩序册。</summary>
    public static bool LooksLikeOrderBook(string path)
    {
        try
        {
            using var stream = WorkbookIO.OpenReadShared(path);
            using var workbook = WorkbookIO.OpenWorkbook(path, stream);
            return workbook.Worksheets.Any(w =>
                       w.Name.Contains("代表队名单", StringComparison.Ordinal) ||
                       w.Name.Contains("竞赛日程", StringComparison.Ordinal)) &&
                   workbook.Worksheets.Any(w =>
                       w.Name.Contains("项目分组表", StringComparison.Ordinal) ||
                       w.Name.Contains("代表队名单", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------- 代表队名单 -----------------------------

    private static bool ReadRoster(IXLWorksheet sheet, MeetProject project, List<RegistrationIssue> issues)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var headers = new List<(int Row, string Grade, int Number)>();
        for (var row = 1; row <= lastRow; row++)
        {
            var text = Clean(sheet.Cell(row, 1).GetString());
            var match = BlockHeader.Match(text);
            if (match.Success)
            {
                headers.Add((row, NormalizeGrade(match.Groups["grade"].Value),
                    int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)));
            }
        }

        if (headers.Count == 0) return false;

        var placeholders = 0;
        for (var index = 0; index < headers.Count; index++)
        {
            var (startRow, grade, number) = headers[index];
            var endRow = index + 1 < headers.Count ? headers[index + 1].Row : lastRow + 1;
            var meetClass = new MeetClass
            {
                Name = $"{grade}{number}班",
                Group = grade,
            };

            var gender = string.Empty;
            for (var row = startRow + 1; row < endRow; row++)
            {
                var first = Clean(sheet.Cell(row, 1).GetString());
                if (first.StartsWith("领队", StringComparison.Ordinal))
                {
                    meetClass.LeaderContact = FieldValue(sheet.Cell(row, 2));
                    continue;
                }

                if (first.StartsWith("教练", StringComparison.Ordinal))
                {
                    meetClass.Coach = FieldValue(sheet.Cell(row, 2));
                    continue;
                }

                if (first.StartsWith("工作人员", StringComparison.Ordinal))
                {
                    meetClass.Staff = FieldValue(sheet.Cell(row, 2));
                    continue;
                }

                if (first is "男子" or "女子")
                {
                    gender = first;
                    // 同一个单元格后面可能直接跟着一对号码 + 姓名。
                    placeholders += ReadAthletePairs(sheet, row, 2, gender, meetClass);
                    continue;
                }

                if (gender.Length == 0) continue;
                placeholders += ReadAthletePairs(sheet, row, 2, gender, meetClass);
            }

            if (meetClass.Athletes.Count > 0)
                project.Classes.Add(meetClass);
        }

        if (placeholders > 0)
            issues.Add(new RegistrationIssue(
                $"名单里有 {placeholders} 处“例：张三”占位，已跳过。", false));

        var duplicateBibs = project.Classes
            .SelectMany(c => c.Athletes.Select(a => a.Bib))
            .Where(b => b.Length > 0)
            .GroupBy(b => b, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateBibs.Count > 0)
            issues.Add(new RegistrationIssue(
                $"有 {duplicateBibs.Count} 个号码布重复（{string.Join("、", duplicateBibs.Take(5))}），请核对名单。", false));

        return project.Classes.Count > 0;
    }

    /// <summary>读取一行里成对的「号码 + 姓名」，返回跳过的占位行数。</summary>
    private static int ReadAthletePairs(
        IXLWorksheet sheet, int row, int firstColumn, string gender, MeetClass meetClass)
    {
        var placeholders = 0;
        for (var column = firstColumn; column <= 9; column += 2)
        {
            var name = Clean(sheet.Cell(row, column + 1).GetString());
            if (name.Length == 0) continue;

            if (name.StartsWith("例", StringComparison.Ordinal) ||
                name.Contains("张三", StringComparison.Ordinal))
            {
                placeholders++;
                continue;
            }

            var bib = Clean(sheet.Cell(row, column).GetString());
            meetClass.Athletes.Add(new Athlete
            {
                Name = name,
                Bib = bib,
                Gender = gender,
            });
        }

        return placeholders;
    }

    /// <summary>没有代表队名单时，至少按「参赛队统计」建出班级（无运动员）。</summary>
    private static void ReadTeamStatistics(IXLWorksheet sheet, MeetProject project)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 1; row <= lastRow; row++)
        {
            var unit = Clean(sheet.Cell(row, 2).GetString());
            var group = NormalizeGrade(Clean(sheet.Cell(row, 3).GetString()));
            if (unit.Length == 0 || group.Length == 0) continue;
            if (!unit.EndsWith("班", StringComparison.Ordinal)) continue;

            var name = unit.StartsWith(group, StringComparison.Ordinal) ? unit : group + unit;
            if (project.Classes.Any(c => c.Name == name)) continue;
            project.Classes.Add(new MeetClass { Name = name, Group = group });
        }
    }

    // ----------------------------- 报名（日程 + 分组表） -----------------------------

    private static void ApplyRegistration(MeetProject project, List<MeetDay> days, List<RegistrationIssue> issues)
    {
        var byName = project.Classes.ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
        var byAthlete = new Dictionary<(string Class, string Name), Athlete>();
        var byBib = new Dictionary<string, Athlete>(StringComparer.Ordinal);
        foreach (var meetClass in project.Classes)
        {
            foreach (var athlete in meetClass.Athletes)
            {
                byAthlete[(meetClass.Name, athlete.Name)] = athlete;
                if (athlete.Bib.Length > 0) byBib.TryAdd(athlete.Bib, athlete);
            }
        }

        var unknownClasses = new HashSet<string>(StringComparer.Ordinal);
        var unknownAthletes = new HashSet<string>(StringComparer.Ordinal);
        var placeholders = 0;
        var genderMismatch = new HashSet<string>(StringComparer.Ordinal);
        var overflow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var day in days)
        {
            foreach (var slot in day.Slots)
            {
                if (slot.Category == MeetEventCategory.Ceremony) continue;

                var teamEvent = IsTeamEvent(slot.EventName);
                foreach (var entry in slot.Entries)
                {
                    if (entry.AthleteName.StartsWith("例", StringComparison.Ordinal))
                    {
                        placeholders++;
                        continue;
                    }

                    if (!byName.TryGetValue(entry.ClassName, out var meetClass))
                    {
                        unknownClasses.Add(entry.ClassName);
                        continue;
                    }

                    if (teamEvent)
                    {
                        if (!meetClass.TeamEvents.Contains(slot.EventName))
                            meetClass.TeamEvents.Add(slot.EventName);
                        continue;
                    }

                    var athlete = ResolveAthlete(entry, meetClass, byAthlete, byBib);
                    if (athlete is null)
                    {
                        unknownAthletes.Add($"{entry.ClassName} {entry.AthleteName}");
                        continue;
                    }

                    if (athlete.Bib.Length == 0 && entry.Bib.Length > 0) athlete.Bib = entry.Bib;

                    if (slot.Gender is "男子" or "女子" &&
                        athlete.Gender is "男子" or "女子" &&
                        athlete.Gender != slot.Gender)
                    {
                        genderMismatch.Add($"{athlete.Name}（名单为{athlete.Gender}，分组表在{slot.Gender}组）");
                    }

                    AddIndividualEvent(athlete, slot.EventName, overflow);
                }
            }
        }

        // 团体项目只记在班级上：秩序册按班列「第 1 组：10班 5班 …」，并不点名哪四名队员，
        // 把项目写到每个人头上会让报名表显示「全班都跑 4x100」，也会虚增项次。
        if (placeholders > 0)
            issues.Add(new RegistrationIssue($"分组表里有 {placeholders} 处“例：张三”占位，已跳过。", false));
        if (unknownClasses.Count > 0)
            issues.Add(new RegistrationIssue(
                $"分组表里有 {unknownClasses.Count} 个班级在名单中找不到（{string.Join("、", unknownClasses.Take(5))}），这些条目已跳过。", false));
        if (unknownAthletes.Count > 0)
            issues.Add(new RegistrationIssue(
                $"分组表里有 {unknownAthletes.Count} 条记录在名单中找不到对应姓名（{string.Join("、", unknownAthletes.Take(5))}），已跳过。", false));
        if (genderMismatch.Count > 0)
            issues.Add(new RegistrationIssue(
                $"名单与分组表的性别不一致：{string.Join("、", genderMismatch.Take(5))}。", false));
        if (overflow.Count > 0)
            issues.Add(new RegistrationIssue(
                $"{string.Join("、", overflow.Take(5))} 报了超过 2 个单项，只保留前两项。", false));
    }

    private static Athlete? ResolveAthlete(
        MeetEntry entry,
        MeetClass meetClass,
        Dictionary<(string Class, string Name), Athlete> byAthlete,
        Dictionary<string, Athlete> byBib)
    {
        if (byAthlete.TryGetValue((meetClass.Name, entry.AthleteName), out var athlete)) return athlete;

        // 姓名有空格 / 全角差异时按号码布兜底。
        if (entry.Bib.Length > 0 && byBib.TryGetValue(entry.Bib, out var byNumber)) return byNumber;

        var loose = meetClass.Athletes.FirstOrDefault(a =>
            string.Equals(Squeeze(a.Name), Squeeze(entry.AthleteName), StringComparison.Ordinal));
        return loose;
    }

    private static void AddIndividualEvent(Athlete athlete, string eventName, HashSet<string> overflow)
    {
        if (eventName.Length == 0) return;
        if (athlete.IndividualEvent1 == eventName || athlete.IndividualEvent2 == eventName) return;

        if (athlete.IndividualEvent1.Length == 0)
        {
            athlete.IndividualEvent1 = eventName;
            return;
        }

        if (athlete.IndividualEvent2.Length == 0)
        {
            athlete.IndividualEvent2 = eventName;
            return;
        }

        overflow.Add(athlete.Name);
    }

    /// <summary>
    /// 竞赛日程里有些项目在分组表中还没有名单（预赛晋级后的决赛常见）。
    /// 这不是错误，但要如实说明，免得老师以为导入漏了数据。
    /// </summary>
    private static void NoteMissingGrouping(
        MeetProject project, IReadOnlyList<MeetDay> days, List<RegistrationIssue> issues)
    {
        if (project.Classes.Count == 0) return;

        var empty = days
            .SelectMany(d => d.Slots)
            .Where(s => s.Category != MeetEventCategory.Ceremony && s.Entries.Count == 0)
            .Select(s => $"{s.Grade}{s.Gender}{s.EventName}{s.Stage}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (empty.Count == 0) return;
        issues.Add(new RegistrationIssue(
            $"有 {empty.Count} 个时段在分组表里还没有名单（多为待晋级产生的决赛），已保留时段但无参赛记录："
            + string.Join("、", empty.Take(4)) + (empty.Count > 4 ? " 等" : string.Empty),
            false));
    }

    private static void FillEventCatalog(MeetProject project)
    {
        foreach (var name in MeetCatalog.IndividualEvents)
            project.Events.Add(new MeetEvent { Name = name, Category = MeetCatalog.IndividualCategories[name] });

        foreach (var name in MeetCatalog.TeamEvents)
            project.Events.Add(new MeetEvent { Name = name, Gender = "混合", Category = MeetEventCategory.Team });
    }

    // ----------------------------- 工具方法 -----------------------------

    private static string TitleFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        stem = Regex.Replace(stem, @"[（(][^）)]*[）)]", string.Empty).Trim();
        stem = Regex.Replace(stem, @"(秩序册|秩序表|总秩序册)$", string.Empty).Trim();
        return stem.Length > 0 ? stem : "运动会秩序册";
    }

    private static IXLWorksheet? FindSheet(XLWorkbook workbook, string keyword) =>
        workbook.Worksheets.FirstOrDefault(w =>
            w.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool IsTeamEvent(string eventName) =>
        eventName.Contains("接力", StringComparison.Ordinal) ||
        eventName.Contains("跳绳", StringComparison.Ordinal);

    private static string NormalizeGrade(string value)
    {
        var text = Clean(value);
        // 正则里「年」是可选的（有的秩序册写「七 1班」），所以这里要能吃裸年级字。
        if (text.StartsWith("七年级", StringComparison.Ordinal) || text == "七年级" || text == "七") return "七年级";
        if (text.StartsWith("八年级", StringComparison.Ordinal) || text == "八年级" || text == "八") return "八年级";
        if (text.StartsWith("九年级", StringComparison.Ordinal) || text == "九年级" || text == "九") return "九年级";
        if (text.StartsWith("高一", StringComparison.Ordinal)) return "高一年级";
        if (text.StartsWith("高二", StringComparison.Ordinal)) return "高二年级";
        if (text.StartsWith("高三", StringComparison.Ordinal)) return "高三年级";
        return text;
    }

    /// <summary>
    /// 读「领队 / 教练 / 工作人员」后面的值：把模板占位「（不填）」当空，
    /// 并把被 Excel 压成科学计数法的手机号还原成 11 位数字（1.7876832726E10 → 17876832726）。
    /// </summary>
    private static string FieldValue(IXLCell cell)
    {
        var text = Clean(cell.GetString());
        if (text is "（不填）" or "(不填)" or "不填") return string.Empty;

        if (text.Contains('E', StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            var digits = number.ToString("F0", CultureInfo.InvariantCulture);
            if (digits.Length == 11 && digits[0] == '1') return digits;
        }

        return text;
    }

    private static string Squeeze(string value) =>
        (value ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormKC)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static string Clean(string value) =>
        (value ?? string.Empty).Replace("　", " ", StringComparison.Ordinal).Trim();

    private static IReadOnlyList<RegistrationIssue> Deduplicate(List<RegistrationIssue> issues) =>
        issues
            .GroupBy(i => (i.Message, i.IsError))
            .Select(g => g.First())
            .ToList();
}
