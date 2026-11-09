using MeetRoster.Core.Models;

namespace MeetRoster.Core.Scheduling;

/// <summary>
/// 按当前报名数据刷新赛程中的参赛记录。当前版本只负责把名单映射到示例时段，
/// 赛程生成规则与班级高亮留待后续接入。
/// </summary>
public static class MeetScheduleBuilder
{
    public static void RefreshEntries(MeetProject project)
    {
        // 按 (性别, 项目) 建一次索引，之后每个时段只查字典——
        // 复杂度从 O(Slots × Classes × Athletes) 降到 O(Classes × Athletes + Slots × k)（Plan 3.3）。
        var individualIndex =
            new Dictionary<(string Gender, string Event), List<(MeetClass Class, Athlete Athlete)>>();
        var teamRegistered = new Dictionary<string, List<MeetClass>>(StringComparer.Ordinal);

        foreach (var meetClass in project.Classes)
        {
            foreach (var athlete in meetClass.Athletes)
            {
                if (string.IsNullOrWhiteSpace(athlete.Name)) continue;

                foreach (var ev in new[] { athlete.IndividualEvent1, athlete.IndividualEvent2 })
                {
                    if (string.IsNullOrEmpty(ev)) continue;
                    var key = (athlete.Gender, ev);
                    if (!individualIndex.TryGetValue(key, out var list)) individualIndex[key] = list = [];
                    list.Add((meetClass, athlete));
                }

                foreach (var ev in new[] { athlete.TeamEvent1, athlete.TeamEvent2, athlete.TeamEvent3 })
                {
                    if (string.IsNullOrEmpty(ev)) continue;
                    if (!teamRegistered.TryGetValue(ev, out var classes)) teamRegistered[ev] = classes = [];
                    if (!classes.Contains(meetClass)) classes.Add(meetClass);
                }
            }
        }

        foreach (var day in project.Days)
        {
            foreach (var slot in day.Slots)
            {
                slot.Entries.Clear();
                if (slot.Category == MeetEventCategory.Ceremony) continue;

                if (IsTeamEvent(slot.EventName))
                {
                    FillTeamEntries(project, slot, teamRegistered);
                    continue;
                }

                FillIndividualEntries(slot, individualIndex);
            }
        }
    }

    private static void FillIndividualEntries(
        MeetSlot slot,
        Dictionary<(string Gender, string Event), List<(MeetClass Class, Athlete Athlete)>> index)
    {
        if (!index.TryGetValue((slot.Gender, slot.EventName), out var matches)) return;

        var order = 1;
        foreach (var (meetClass, athlete) in matches)
        {
            slot.Entries.Add(new MeetEntry
            {
                ClassName = meetClass.Name,
                AthleteName = athlete.Name,
                Lane = slot.Category == MeetEventCategory.Track
                    ? $"第 {order++} 道"
                    : $"第 {order++} 组",
            });
        }
    }

    private static void FillTeamEntries(
        MeetProject project,
        MeetSlot slot,
        Dictionary<string, List<MeetClass>> teamRegistered)
    {
        var requiresRegistration = slot.EventName == "4x100米接力";
        var group = 1;
        var ordered = requiresRegistration
            ? teamRegistered.TryGetValue(slot.EventName, out var classes) ? classes : []
            : project.Classes;

        foreach (var meetClass in ordered)
        {
            // 报名表里勾了团体项目的人，才是真正上场的那几位。秩序册只按班列队、不点名队员，
            // 所以能拿到名单时就用名单替换占位标签，拿不到才退回「4x100 接力队」这种说法。
            var members = TeamMembersOf(meetClass, slot.EventName);
            slot.Entries.Add(new MeetEntry
            {
                ClassName = meetClass.Name,
                AthleteName = members.Count > 0 ? string.Join("、", members) : TeamLabel(slot.EventName),
                Lane = $"第 {group++} 组",
                TeamMembers = string.Join("、", members),
                TeamMembersFromRegistration = members.Count > 0,
            });
        }
    }

    /// <summary>
    /// 只补团体项目的队员，不重建整份赛程。
    /// 秩序册导入的时段带着原始组次 / 道次，整表重算会把它们抹掉；
    /// 报名表后到时只需把「4x100 接力队」这类占位换成真实队员。
    /// 返回补上的条目数。
    /// </summary>
    public static int ApplyTeamMembers(MeetProject project)
    {
        var byName = project.Classes.ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
        var updated = 0;

        foreach (var day in project.Days)
        {
            foreach (var slot in day.Slots)
            {
                if (!IsTeamEvent(slot.EventName)) continue;

                var entries = new List<MeetEntry>(slot.Entries.Count);
                foreach (var entry in slot.Entries)
                {
                    if (!byName.TryGetValue(entry.ClassName, out var meetClass))
                    {
                        entries.Add(entry);
                        continue;
                    }

                    var members = TeamMembersOf(meetClass, slot.EventName);
                    if (members.Count == 0)
                    {
                        // 没报名就保持原样（含之前补过的队员，交给调用方决定是否清空）。
                        entries.Add(entry);
                        continue;
                    }

                    var text = string.Join("、", members);
                    if (text == entry.TeamMembers && entry.TeamMembersFromRegistration)
                    {
                        entries.Add(entry);
                        continue;
                    }

                    entries.Add(new MeetEntry
                    {
                        ClassName = entry.ClassName,
                        AthleteName = text,
                        Lane = entry.Lane,
                        Bib = entry.Bib,
                        Group = entry.Group,
                        TeamMembers = text,
                        TeamMembersFromRegistration = true,
                    });
                    updated++;
                }

                slot.Entries.Clear();
                slot.Entries.AddRange(entries);
            }
        }

        return updated;
    }

    /// <summary>
    /// 该班在某个团体项目上报名的人名。只取真填了名字的运动员，
    /// 并按报名表列序（团体项目 1→3）稳定输出，避免同一份数据两次生成顺序不同。
    /// </summary>
    private static List<string> TeamMembersOf(MeetClass meetClass, string eventName)
    {
        var names = new List<string>();
        foreach (var athlete in meetClass.Athletes)
        {
            if (string.IsNullOrWhiteSpace(athlete.Name)) continue;
            if (athlete.TeamEvent1 != eventName &&
                athlete.TeamEvent2 != eventName &&
                athlete.TeamEvent3 != eventName) continue;
            if (!names.Contains(athlete.Name, StringComparer.Ordinal)) names.Add(athlete.Name);
        }

        return names;
    }

    private static string TeamLabel(string eventName) => eventName switch
    {
        "4x100米接力" => "4x100 接力队",
        "20*50米男女混合接力" => "混合接力队",
        "一分钟跳绳" => "跳绳队",
        _ => "代表队",
    };

    private static bool IsTeamEvent(string eventName) =>
        eventName.Contains("接力", StringComparison.Ordinal) ||
        eventName.Contains("跳绳", StringComparison.Ordinal);
}
