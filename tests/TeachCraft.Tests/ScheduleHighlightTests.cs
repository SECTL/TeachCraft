using MeetRoster.App;
using MeetRoster.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// T13 高亮增量更新的守卫。
/// <para>
/// 改动要点：<see cref="ScheduleEntryViewModel"/> 的高亮状态从「构造时定型、只读」
/// 改成「可变 + 发通知」，于是切换高亮班级时可以就地改色，不必重建整棵赛程树
/// （2000+ 条目在非虚拟化树上等于整屏重画）。
/// </para>
/// <para>
/// 但「仅显示该班」需要增删条目，仍必须重建。
/// <para>
/// **本文件只覆盖条目级状态**；VM 里「重建 vs 就地」的分叉由
/// <c>ScheduleHighlightViewModelTests</c> 覆盖（那里用真实导入路径构造 VM，
/// 能抓到时段级派生状态没跟着更新的问题）。
/// </para>
/// </para>
/// <para>
/// 说明：<c>MeetRosterViewModel.LoadProject</c> 是私有的，从测试侧构造不出
/// 「已载入赛程」的 VM；因此这里直接测 VM 层依赖的两个事实——
/// ① <c>ApplyHighlight</c> 就地改状态、对象引用不变；② 语义与构造时一致。
/// VM 里的分支选择由代码审查与走查覆盖（见 task.md 的说明）。
/// </para>
/// </summary>
public sealed class ScheduleHighlightTests
{
    private static MeetEntry Entry(string className) => new()
    {
        ClassName = className,
        AthleteName = "商砚清",
        Group = "七年级",
        Bib = "4001",
        Lane = "1",
    };

    [Fact]
    public void ApplyHighlight_ChangesStateInPlace_WithoutNewInstance()
    {
        var vm = new ScheduleEntryViewModel(Entry("七年级1班"));
        Assert.False(vm.IsHighlighted);
        Assert.Equal(1.0, vm.Opacity);

        vm.ApplyHighlight("七年级1班");
        Assert.True(vm.IsHighlighted);
        Assert.Equal(1.0, vm.Opacity);

        // 换成别的班：本条不亮，且被淡化。
        vm.ApplyHighlight("七年级2班");
        Assert.False(vm.IsHighlighted);
        Assert.Equal(0.7, vm.Opacity);

        // 清除：全部恢复中性。
        vm.ApplyHighlight(null);
        Assert.False(vm.IsHighlighted);
        Assert.Equal(1.0, vm.Opacity);
    }

    [Fact]
    public void ApplyHighlight_MatchesConstructorSemantics()
    {
        // 构造时传入的高亮班级与事后 ApplyHighlight 必须得到同样状态，
        // 否则「首次进入赛程页」与「切换高亮」会呈现两种观感。
        foreach (var target in new[] { null, "七年级1班", "七年级2班" })
        {
            var viaCtor = new ScheduleEntryViewModel(Entry("七年级1班"), target);
            var viaMethod = new ScheduleEntryViewModel(Entry("七年级1班"));
            viaMethod.ApplyHighlight(target);

            Assert.Equal(viaCtor.IsHighlighted, viaMethod.IsHighlighted);
            Assert.Equal(viaCtor.Opacity, viaMethod.Opacity);
        }
    }

    [Fact]
    public void ApplyHighlight_RaisesPropertyChanged()
    {
        // 没有通知的话，就地改状态不会刷新界面——这正是「增量更新」的前提。
        // 用一个「先高亮、再切到别的班」的序列，保证两个字段都真的发生了变化。
        var vm = new ScheduleEntryViewModel(Entry("七年级1班"));
        vm.ApplyHighlight("七年级1班");

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplyHighlight("七年级2班");

        Assert.Contains(nameof(ScheduleEntryViewModel.IsHighlighted), changed);
        Assert.Contains(nameof(ScheduleEntryViewModel.Opacity), changed);
    }

    [Fact]
    public void ApplyHighlight_DoesNotNotify_WhenOpacityUnchanged()
    {
        // 高亮本班时 Opacity 本来就是 1.0，SetProperty 因值未变而不发通知——
        // 这是想要的：无谓通知会让绑定多刷一次。只发真正变化的那条。
        var vm = new ScheduleEntryViewModel(Entry("七年级1班"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplyHighlight("七年级1班");

        Assert.Contains(nameof(ScheduleEntryViewModel.IsHighlighted), changed);
        Assert.DoesNotContain(nameof(ScheduleEntryViewModel.Opacity), changed);
    }

    [Fact]
    public void ApplyHighlight_IsIdempotent_ForRepeatedSameClass()
    {
        // 连续两次相同高亮不应产生多余通知（避免无谓重绘）。
        var vm = new ScheduleEntryViewModel(Entry("七年级1班"), "七年级1班");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplyHighlight("七年级1班");

        Assert.Empty(changed);
    }
}
