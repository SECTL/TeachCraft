using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SeatGrid.Core.Models;
using SeatGrid.Core.Seating;

namespace SeatGrid.App;

/// <summary>一条约束规则在界面上的展示行。</summary>
public sealed class ConstraintRowViewModel
{
    public required string Kind { get; init; }        // lock / mutex / deskmate / avoid-deskmate / zone
    public required string Label { get; init; }       // 「张三 ↔ 李四」「张三 · 只在前 2 排」
    public required string Detail { get; init; }      // ToolTip
    public Guid? StudentA { get; init; }
    public Guid? StudentB { get; init; }
    public StudentPair? Pair { get; init; }
    public Guid? ZoneStudent { get; init; }
}

/// <summary>
/// 排座约束的界面状态：锁定、互斥、结对、上期同桌、区域与排座说明。
/// 从主 ViewModel 里抽出来，避免 1087 行的 SeatGridViewModel 继续膨胀。
/// </summary>
public sealed partial class SeatConstraintsViewModel : ObservableObject
{
    private readonly List<StudentViewModel> _students;

    public SeatConstraintsViewModel(IReadOnlyList<StudentViewModel> students)
    {
        _students = students.ToList();
        RefreshRows();
    }

    public ObservableCollection<ConstraintRowViewModel> Rules { get; } = new();

    /// <summary>已锁定的学生 Id → 座位。锁定的是「学生-座位绑定」。</summary>
    public Dictionary<Guid, SeatPosition> Pins { get; } = new();

    public List<StudentPair> Mutex { get; } = new();

    public List<StudentPair> Deskmates { get; } = new();

    public List<StudentPair> AvoidDeskmates { get; } = new();

    public Dictionary<Guid, StudentZone> Zones { get; } = new();

    [ObservableProperty] private IReadOnlyList<string> _issues = Array.Empty<string>();

    [ObservableProperty] private bool _issuesExpanded;

    public bool HasIssues => Issues.Count > 0;
    public bool HasRules => Rules.Count > 0;
    public string IssuesTitle => $"排座说明 · {Issues.Count} 条";
    public string IssuesChevron => IssuesExpanded ? "▴" : "▾";

    partial void OnIssuesExpandedChanged(bool value) => OnPropertyChanged(nameof(IssuesChevron));
    public int PinnedCount => Pins.Count;
    public string PinnedSummary => PinnedCount == 0 ? "没有锁定座位" : $"已锁定 {PinnedCount} 个座位";

    /// <summary>把当前约束打成 Core 引擎要的形状。</summary>
    public ArrangeConstraints Build(SeatChartLayout layout) => new()
    {
        Pins = new Dictionary<Guid, SeatPosition>(Pins),
        Mutex = Mutex.ToList(),
        Deskmates = Deskmates.ToList(),
        AvoidDeskmates = AvoidDeskmates.ToList(),
        Zones = Zones.ToDictionary(kv => kv.Key, kv => kv.Value.Normalize(layout)),
    };

    /// <summary>从 Core 读回的约束灌回界面状态。</summary>
    public void Apply(ArrangeConstraints? constraints)
    {
        Pins.Clear();
        Mutex.Clear();
        Deskmates.Clear();
        AvoidDeskmates.Clear();
        Zones.Clear();

        if (constraints is not null)
        {
            foreach (var (id, seat) in constraints.Pins) Pins[id] = seat;
            Mutex.AddRange(constraints.Mutex);
            Deskmates.AddRange(constraints.Deskmates);
            AvoidDeskmates.AddRange(constraints.AvoidDeskmates);
            foreach (var (id, zone) in constraints.Zones) Zones[id] = zone;
        }

        RefreshRows();
    }

    /// <summary>排座完成后写入报告。</summary>
    public void SetIssues(IEnumerable<string> issues)
    {
        Issues = issues.ToList();
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssuesTitle));
    }

    public void TogglePin(StudentViewModel student, SeatPosition seat)
    {
        if (Pins.TryGetValue(student.Id, out var current) && current == seat) Pins.Remove(student.Id);
        else Pins[student.Id] = seat;

        RefreshRows();
        NotifyCounts();
    }

    public bool IsPinned(Guid id) => Pins.ContainsKey(id);

    public bool IsPinnedSeat(SeatPosition seat) => Pins.ContainsValue(seat);

    public void UnpinAll()
    {
        Pins.Clear();
        RefreshRows();
        NotifyCounts();
    }

    /// <summary>布局缩小或删除学生时，把指向已不存在的座位 / 学生的锁清掉。</summary>
    public int DropInvalidPins(SeatChartLayout layout, IReadOnlyCollection<Guid> liveStudentIds)
    {
        var removed = 0;
        foreach (var (id, seat) in Pins.ToList())
        {
            if (layout.Contains(seat) && liveStudentIds.Contains(id)) continue;
            Pins.Remove(id);
            removed++;
        }

        if (removed > 0)
        {
            RefreshRows();
            NotifyCounts();
        }

        return removed;
    }

    public void RemoveRule(ConstraintRowViewModel row)
    {
        switch (row.Kind)
        {
            case "lock" when row.StudentA is Guid id:
                Pins.Remove(id);
                break;
            case "mutex" when row.Pair is { } pair:
                Mutex.RemoveAll(p => p == pair);
                break;
            case "deskmate" when row.Pair is { } pair:
                Deskmates.RemoveAll(p => p == pair);
                break;
            case "avoid-deskmate" when row.Pair is { } pair:
                AvoidDeskmates.RemoveAll(p => p == pair);
                break;
            case "zone" when row.ZoneStudent is Guid id:
                Zones.Remove(id);
                break;
        }

        RefreshRows();
        NotifyCounts();
    }

    public void AddPairRule(StudentViewModel a, StudentViewModel b, bool mustBeDeskmates)
    {
        if (a.Id == b.Id) return;
        var pair = StudentPair.Create(a.Id, b.Id);

        Mutex.RemoveAll(p => p == pair);
        Deskmates.RemoveAll(p => p == pair);
        AvoidDeskmates.RemoveAll(p => p == pair);

        if (mustBeDeskmates) Deskmates.Add(pair);
        else Mutex.Add(pair);

        RefreshRows();
        NotifyCounts();
    }

    public void SetZone(StudentViewModel student, StudentZone zone)
    {
        if (zone.IsEmpty) Zones.Remove(student.Id);
        else Zones[student.Id] = zone;

        RefreshRows();
        NotifyCounts();
    }

    public void RefreshRows()
    {
        Rules.Clear();

        var byId = _students.ToDictionary(s => s.Id);

        foreach (var (id, seat) in Pins.OrderBy(kv => kv.Value.Group).ThenBy(kv => kv.Value.Row))
        {
            if (!byId.TryGetValue(id, out var student)) continue;
            Rules.Add(new ConstraintRowViewModel
            {
                Kind = "lock",
                Label = $"{student.Name} · {seat.Group + 1}组{seat.Row + 1}排{seat.Column + 1}列",
                Detail = "已锁定：重新排座时这位同学不动",
                StudentA = id,
            });
        }

        void AddPair(string kind, string symbol, string detail, IEnumerable<StudentPair> pairs)
        {
            foreach (var pair in pairs)
            {
                if (!byId.TryGetValue(pair.A, out var a) || !byId.TryGetValue(pair.B, out var b)) continue;
                Rules.Add(new ConstraintRowViewModel
                {
                    Kind = kind,
                    Label = $"{a.Name} {symbol} {b.Name}",
                    Detail = detail,
                    StudentA = pair.A,
                    StudentB = pair.B,
                    Pair = pair,
                });
            }
        }

        AddPair("mutex", "↔", "绝不相邻：前后左右都不挨着", Mutex);
        AddPair("deskmate", "=", "必须同桌：坐同一张双人桌", Deskmates);
        AddPair("avoid-deskmate", "≠", "上期同桌：尽量不重复安排", AvoidDeskmates);

        foreach (var (id, zone) in Zones)
        {
            if (!byId.TryGetValue(id, out var student)) continue;
            Rules.Add(new ConstraintRowViewModel
            {
                Kind = "zone",
                Label = $"{student.Name} · {DescribeZone(zone)}",
                Detail = "区域限制：允许区域内还要减去禁止区域",
                StudentA = id,
                ZoneStudent = id,
            });
        }

        OnPropertyChanged(nameof(HasRules));
    }

    private static string DescribeZone(StudentZone zone)
    {
        var parts = new List<string>();
        foreach (var rect in zone.Allowed.Take(2))
            parts.Add($"第{rect.RowFrom + 1}–{rect.RowTo + 1}排");
        foreach (var rect in zone.Denied.Take(2))
            parts.Add($"避开第{rect.RowFrom + 1}–{rect.RowTo + 1}排");
        return parts.Count == 0 ? "区域" : string.Join(" / ", parts);
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(PinnedCount));
        OnPropertyChanged(nameof(PinnedSummary));
    }
}
