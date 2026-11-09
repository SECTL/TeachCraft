using MeetRoster.Core.Models;

namespace MeetRoster.Core.Export;

/// <summary>PDF 版式：表格手册（原版）或赛程卡（照搬软件内「仅显示该班选手」的观感）。</summary>
public enum MeetPdfLayout
{
    /// <summary>纸墨表格：概览 → 名单 → 单项 → 日程，适合归档与打印。</summary>
    Handbook,

    /// <summary>赛程卡：每个时段一张卡，左侧时间、彩色分类徽标、选手 chip，适合发群与张贴。</summary>
    Card,
}

/// <summary>Excel 样式：软件整理的四页工作簿，或在导入的原表上直接加底色。</summary>
public enum MeetExcelStyle
{
    /// <summary>软件整理：班级报名 / 单项名单 / 赛程 / 班级汇总四页。</summary>
    Workbook,

    /// <summary>原表高亮：复制导入的原工作簿，只在「代表队名单 / 项目分组表」里给该班条目涂底色。</summary>
    OriginalHighlight,
}

/// <summary>导出对象：某个班、或整份秩序册。</summary>
public sealed class MeetExportOptions
{
    /// <summary>要导出的班级名；为空表示全部班级。</summary>
    public string? ClassName { get; init; }

    /// <summary>Excel 样式；PDF 不使用。</summary>
    public MeetExcelStyle ExcelStyle { get; init; } = MeetExcelStyle.Workbook;

    /// <summary>PDF 版式；Excel 不使用。</summary>
    public MeetPdfLayout Layout { get; init; } = MeetPdfLayout.Handbook;

    /// <summary>赛程里只列该班选手（勾选班级时生效）。</summary>
    public bool OnlyClassEntries { get; init; } = true;

    /// <summary>赛程里给该班条目加高亮底色。</summary>
    public bool HighlightClass { get; init; } = true;

    // Excel 分页开关
    public bool IncludeRoster { get; init; } = true;
    public bool IncludeEvents { get; init; } = true;
    public bool IncludeSchedule { get; init; } = true;
    public bool IncludeClassSummary { get; init; } = true;

    public bool IsAllClasses => string.IsNullOrWhiteSpace(ClassName);
}

/// <summary>导出文件名的统一规则：班级 + 用途 + 扩展名。</summary>
public static class MeetExportNaming
{
    public static string Build(MeetProject project, MeetExportOptions options, string extension)
    {
        var stem = options.IsAllClasses
            ? Safe(project.Title) + "·全部班级"
            : Safe(options.ClassName!) + "参赛手册";
        return stem + extension;
    }

    public static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (value ?? string.Empty)
            .Select(c => invalid.Contains(c) ? '-' : c)
            .ToArray();
        var text = new string(chars).Trim().Trim('.');
        return text.Length == 0 ? "运动会" : text;
    }
}

/// <summary>一个班在赛程里的某一条参赛记录，按日期与时间排好序。</summary>
public sealed class ClassEntryLine
{
    /// <summary>参赛班级全名（如「九年级6班」）；卡片版式的 chip 要显示它。</summary>
    public required string ClassName { get; init; }
    public required string DayName { get; init; }
    public required string DateText { get; init; }
    public required string Period { get; init; }
    public required string Time { get; init; }
    public required string Grade { get; init; }
    public required string Gender { get; init; }
    public required string EventName { get; init; }
    public required string Stage { get; init; }
    public string CategoryLabel { get; init; } = string.Empty;
    public string AthleteName { get; init; } = string.Empty;
    public string Lane { get; init; } = string.Empty;
    public string Bib { get; init; } = string.Empty;
    public string GroupCount { get; init; } = string.Empty;
    public string AdvanceCount { get; init; } = string.Empty;
    public string EntryCountText { get; init; } = string.Empty;
    /// <summary>是否团体项目（接力 / 跳绳）：卡片版式据此把「人次」写成「支队伍」。</summary>
    public bool IsTeamEvent { get; init; }
    /// <summary>团体项目该班的报名队员（来自报名表）；空表示只知道「某班有队」。</summary>
    public string TeamMembers { get; init; } = string.Empty;
    public bool TeamMembersFromRegistration { get; init; }

    /// <summary>
    /// 参赛人那一栏该显示什么：团体项目拿到报名表队员就显示队员，
    /// 否则退回秩序册的占位标签（「4x100 接力队」）。
    /// </summary>
    public string AthleteLabel =>
        TeamMembersFromRegistration && TeamMembers.Length > 0 ? TeamMembers : AthleteName;
}

/// <summary>导出用的班级汇总：人数、项次与报名项目清单。</summary>
public sealed class ClassExportSummary
{
    public required MeetClass Class { get; init; }
    public required IReadOnlyList<Athlete> Athletes { get; init; }
    public required IReadOnlyList<ClassEntryLine> Entries { get; init; }

    public int AthleteCount => Athletes.Count;
    public int MaleCount => Athletes.Count(a => a.Gender == "男子");
    public int FemaleCount => Athletes.Count(a => a.Gender == "女子");
    public int IndividualCount => Athletes.Sum(a =>
        NonEmpty(a.IndividualEvent1) + NonEmpty(a.IndividualEvent2));
    public int TeamCount => Class.TeamEvents.Count;
    public int EntryCount => Entries.Count;

    public string Title => Class.Name;
    public string GroupLabel => string.IsNullOrWhiteSpace(Class.Group) ? "—" : Class.Group;

    /// <summary>该班报名的单项，按项目目录顺序排列，附带参赛人姓名。</summary>
    public IReadOnlyList<(string Event, string Gender, IReadOnlyList<string> Names)> EventRoster()
    {
        var list = new List<(string, string, IReadOnlyList<string>)>();
        foreach (var eventName in MeetCatalog.IndividualEvents)
        {
            foreach (var gender in MeetCatalog.Genders)
            {
                var names = Athletes
                    .Where(a => a.Gender == gender &&
                                (a.IndividualEvent1 == eventName || a.IndividualEvent2 == eventName))
                    .Select(a => a.Name)
                    .ToList();
                if (names.Count > 0) list.Add((eventName, gender, names));
            }
        }

        return list;
    }

    private static int NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? 0 : 1;
}

/// <summary>把工程数据整理成导出需要的形状（xlsx 与 PDF 共用，保证两份文件内容一致）。</summary>
public static class MeetExportBuilder
{
    public static IReadOnlyList<ClassExportSummary> Build(MeetProject project, MeetExportOptions options)
    {
        var targets = options.IsAllClasses
            ? project.Classes.ToList()
            : project.Classes
                .Where(c => string.Equals(c.Name, options.ClassName, StringComparison.Ordinal))
                .ToList();

        if (targets.Count == 0 && !options.IsAllClasses)
            targets = project.Classes.Take(1).ToList();

        return targets
            .Select(meetClass => BuildClass(project, meetClass, options))
            .ToList();
    }

    public static ClassExportSummary BuildClass(MeetProject project, MeetClass meetClass, MeetExportOptions options)
    {
        var athletes = meetClass.Athletes
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .ToList();

        var lines = new List<ClassEntryLine>();
        foreach (var day in project.Days)
        {
            foreach (var slot in day.Slots)
            {
                if (slot.Category == MeetEventCategory.Ceremony) continue;
                foreach (var entry in slot.Entries)
                {
                    if (!string.Equals(entry.ClassName, meetClass.Name, StringComparison.Ordinal)) continue;
                    lines.Add(new ClassEntryLine
                    {
                        ClassName = meetClass.Name,
                        DayName = day.Name,
                        DateText = day.DateText,
                        Period = slot.Period,
                        Time = slot.Time,
                        Grade = slot.Grade,
                        Gender = slot.Gender,
                        EventName = slot.EventName,
                        Stage = slot.Stage,
                        CategoryLabel = CategoryLabel(slot.Category),
                        AthleteName = entry.AthleteName,
                        Lane = entry.Lane,
                        Bib = entry.Bib,
                        GroupCount = slot.GroupCount,
                        AdvanceCount = slot.AdvanceCount,
                        EntryCountText = slot.EntryCountText,
                        IsTeamEvent = IsTeamEvent(slot.EventName),
                        TeamMembers = entry.TeamMembers,
                        TeamMembersFromRegistration = entry.TeamMembersFromRegistration,
                    });
                }
            }
        }

        return new ClassExportSummary
        {
            Class = meetClass,
            Athletes = athletes,
            Entries = lines,
        };
    }

    /// <summary>全部班级汇总用的一行。</summary>
    public static IReadOnlyList<(MeetClass Class, int Male, int Female, int Entries)> BuildClassTotals(
        MeetProject project)
    {
        return project.Classes
            .Select(c =>
            {
                var athletes = c.Athletes.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
                var entries = project.Days
                    .SelectMany(d => d.Slots)
                    .SelectMany(s => s.Entries)
                    .Count(e => string.Equals(e.ClassName, c.Name, StringComparison.Ordinal));
                return (c, athletes.Count(a => a.Gender == "男子"), athletes.Count(a => a.Gender == "女子"), entries);
            })
            .ToList();
    }

    public static string CategoryLabel(MeetEventCategory category) => category switch
    {
        MeetEventCategory.Track => "径赛",
        MeetEventCategory.Field => "田赛",
        MeetEventCategory.Fun => "趣味",
        MeetEventCategory.Team => "团体",
        MeetEventCategory.Ceremony => "仪式",
        _ => "其他",
    };

    public static bool IsTeamEvent(string eventName) =>
        eventName.Contains("接力", StringComparison.Ordinal) ||
        eventName.Contains("跳绳", StringComparison.Ordinal);
}
