using System.Globalization;
using MeetRoster.Core.Models;

namespace MeetRoster.Core.Export;

/// <summary>
/// 卡片版式的一页：标题区 + 若干「日期 / 上午下午」分段，每段里是时段卡。
/// 与 <see cref="MeetPdfExporter"/> 的表格版共用同一份 <see cref="ClassExportSummary"/>，
/// 只是画法不同——这里照搬软件内赛程页「仅显示该班选手」的观感：左侧大号时间、
/// 彩色分类徽标、选手 chip、右侧人次。
/// </summary>
public sealed class MeetCardDocument
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Meta { get; init; }
    public required IReadOnlyList<MeetCardSection> Sections { get; init; }
    public required IReadOnlyList<string> Footnotes { get; init; }
    public string Scope { get; init; } = string.Empty;
}

/// <summary>卡片版式里的一个比赛日。</summary>
public sealed class MeetCardSection
{
    public required string DayLabel { get; init; }
    public required string DateText { get; init; }
    public required IReadOnlyList<MeetCardPeriod> Periods { get; init; }
}

/// <summary>一个上午 / 下午，含该时段的卡片。</summary>
public sealed class MeetCardPeriod
{
    public required string Period { get; init; }
    public required IReadOnlyList<MeetCardItem> Items { get; init; }

    public string English => Period == "上午" ? "MORNING" : "AFTERNOON";

    public string TimeRange => Items.Count switch
    {
        0 => string.Empty,
        1 => Items[0].Time,
        _ => $"{Items[0].Time} – {Items[^1].Time}",
    };
}

/// <summary>一张时段卡：同一时间同一项目的所有该班参赛记录合成一张。</summary>
public sealed class MeetCardItem
{
    public required string Time { get; init; }
    public required string EventName { get; init; }
    public required string CategoryLabel { get; init; }
    public required MeetEventCategory Category { get; init; }
    public string Grade { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;
    public string GroupSummary { get; init; } = string.Empty;
    public bool IsTeamEvent { get; init; }
    public required IReadOnlyList<MeetCardEntry> Entries { get; init; }

    public string EntryCountLabel => IsTeamEvent
        ? $"{Entries.Count} 支队伍"
        : $"{Entries.Count} 人次";

    public bool ShowGender => Gender is "男子" or "女子" or "混合";
    public bool ShowGrade => Grade.Length > 0;
    public bool ShowStage => Stage.Length > 0;
    public bool ShowGroupSummary => GroupSummary.Length > 0;
}

/// <summary>卡片里的一条选手 chip。</summary>
public sealed class MeetCardEntry
{
    public required string ClassName { get; init; }
    public required string AthleteName { get; init; }
    public string Lane { get; init; } = string.Empty;
    public string Bib { get; init; } = string.Empty;

    /// <summary>导出全部班级时 chip 要带班级前缀；单班导出不必重复。</summary>
    public bool ShowClass { get; init; } = true;
}

/// <summary>把导出的赛程行整理成卡片版式的结构（预览与 PDF 共用）。</summary>
public static class MeetCardBuilder
{
    public static MeetCardDocument Build(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var scope = options.IsAllClasses
            ? $"全部班级 · {summaries.Count} 个班"
            : summaries[0].Title;

        var entries = summaries
            .SelectMany(s => s.Entries)
            .OrderBy(l => DayIndex(project, l.DayName))
            .ThenBy(l => l.Period == "上午" ? 0 : 1)
            .ThenBy(l => l.Time, StringComparer.Ordinal)
            .ToList();

        var sections = new List<MeetCardSection>();
        foreach (var dayGroup in entries
                     .GroupBy(l => (l.DayName, l.DateText))
                     .OrderBy(g => DayIndex(project, g.Key.DayName)))
        {
            var periods = new List<MeetCardPeriod>();
            foreach (var periodGroup in dayGroup
                         .GroupBy(l => l.Period)
                         .OrderBy(g => g.Key == "上午" ? 0 : 1))
            {
                // 同一时间 + 同一项目 + 同一赛次合成一张卡，多个选手做成一排 chip。
                var items = periodGroup
                    .GroupBy(l => (l.Time, l.EventName, l.Stage))
                    .Select(g => BuildItem(g.Key.Time, g.Key.EventName, g.Key.Stage, g.ToList(),
                        showClass: options.IsAllClasses))
                    .ToList();

                periods.Add(new MeetCardPeriod { Period = periodGroup.Key, Items = items });
            }

            sections.Add(new MeetCardSection
            {
                DayLabel = dayGroup.Key.DayName,
                DateText = dayGroup.Key.DateText,
                Periods = periods,
            });
        }

        var footnotes = new List<string>();
        if (entries.Count == 0)
        {
            footnotes.Add(options.IsAllClasses
                ? "还没有赛程数据，或所选内容里没有赛程。"
                : "这个班在赛程里没有参赛记录。");
        }

        return new MeetCardDocument
        {
            Title = options.IsAllClasses ? project.Title : $"{summaries[0].Title} · 参赛日程",
            Subtitle = options.IsAllClasses ? "全部班级 · 秩序册整理" : "运动会秩序册整理",
            Meta = $"{sections.Count} 天 · {entries.Count} 条参赛记录 · "
                   + $"{summaries.Sum(s => s.AthleteCount)} 名运动员 · 本机生成",
            Sections = sections,
            Footnotes = footnotes,
            Scope = scope,
        };
    }

    private static MeetCardItem BuildItem(
        string time, string eventName, string stage, IReadOnlyList<ClassEntryLine> lines, bool showClass)
    {
        var first = lines[0];
        return new MeetCardItem
        {
            Time = time,
            EventName = eventName,
            CategoryLabel = first.CategoryLabel,
            Category = CategoryFromLabel(first.CategoryLabel),
            Grade = first.Grade,
            Gender = first.Gender,
            Stage = stage,
            Venue = VenueFor(eventName, first.CategoryLabel),
            GroupSummary = GroupSummary(first.GroupCount, first.AdvanceCount),
            IsTeamEvent = first.IsTeamEvent,
            Entries = lines
                .Select(l => new MeetCardEntry
                {
                    ClassName = l.ClassName,
                    AthleteName = l.AthleteLabel,
                    Lane = l.Lane,
                    Bib = l.Bib,
                    ShowClass = showClass,
                })
                .ToList(),
        };
    }

    private static MeetEventCategory CategoryFromLabel(string label) => label switch
    {
        "径赛" => MeetEventCategory.Track,
        "田赛" => MeetEventCategory.Field,
        "团体" => MeetEventCategory.Team,
        "趣味" => MeetEventCategory.Fun,
        "仪式" => MeetEventCategory.Ceremony,
        _ => MeetEventCategory.Other,
    };

    private static string VenueFor(string eventName, string categoryLabel) => categoryLabel switch
    {
        "径赛" => "田径场",
        "趣味" => "趣味赛区",
        "田赛" => eventName switch
        {
            "跳高" => "跳高区",
            "实心球" => "投掷区",
            "立定跳远" => "沙坑区",
            _ => "田赛区",
        },
        _ => "田径场",
    };

    private static string GroupSummary(string groupCount, string advanceCount)
    {
        var parts = new List<string>();
        if (groupCount.Length > 0) parts.Add($"{groupCount} 组");
        if (advanceCount.Length > 0) parts.Add($"取 {advanceCount} 名");
        return string.Join(" · ", parts);
    }

    private static int DayIndex(MeetProject project, string dayName)
    {
        for (var i = 0; i < project.Days.Count; i++)
        {
            if (string.Equals(project.Days[i].Name, dayName, StringComparison.Ordinal)) return i;
        }

        return int.MaxValue;
    }
}
