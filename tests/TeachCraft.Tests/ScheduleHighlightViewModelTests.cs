using MeetRoster.App;

namespace TeachCraft.Tests;

/// <summary>
/// T13 高亮增量更新的 **VM 级** 守卫。
/// <para>
/// 为什么单独一个文件：条目级（<see cref="ScheduleEntryViewModel"/>）的状态更新
/// 由 <c>ScheduleHighlightTests</c> 覆盖，但真正的分叉在 ViewModel 里——
/// 「只换高亮班级」走就地更新，「仅显示该班」走重建。**时段卡片底色**
/// （<c>HasClassHighlight</c>）与「N 条高亮」标签原先只在构造时算一次，
/// 就地更新如果漏了它们，卡片底色会停在旧班级上——这是用户能直接看到的问题，
/// 而且条目级的测试永远抓不到。
/// </para>
/// <para>
/// 这里用真实的导入路径构造 VM，不碰 UI 线程。
/// </para>
/// </summary>
public sealed class ScheduleHighlightViewModelTests
{
    [Fact]
    public async Task SwitchingHighlightClass_UpdatesSlotLevelState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SlotHighlight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var corpus = MeetPerfCorpus.WriteOrderBook(dir, classes: 4);
            var vm = new MeetRosterViewModel();
            await vm.ImportOrderBookAsync(corpus);

            vm.SelectSection("schedule");
            vm.SelectDay(vm.ScheduleDays.First(d => !d.IsAllDays));

            var slots = vm.ScheduleGroups.SelectMany(g => g.Slots).ToList();
            Assert.NotEmpty(slots);

            // 记录基线：此时还没有高亮。
            Assert.All(slots, s => Assert.False(s.HasClassHighlight));

            var first = vm.AllClasses.First();
            vm.ScheduleHighlightClass = first;

            var lit = slots.Where(s => s.HasClassHighlight).ToList();
            Assert.NotEmpty(lit);
            Assert.All(lit, s => Assert.NotEmpty(s.HighlightedEntryCountLabel));

            // 换到另一个班：时段级状态必须跟着换，不能停在第一个班。
            var second = vm.AllClasses.First(c => c.Name != first.Name);
            vm.ScheduleHighlightClass = second;

            var litAfter = vm.ScheduleGroups.SelectMany(g => g.Slots)
                .Where(s => s.HasClassHighlight).ToList();
            Assert.NotEmpty(litAfter);
            Assert.All(litAfter, s => Assert.Contains(
                s.Entries, e => e.IsHighlighted && e.ClassName == second.Name));

            // 清除高亮：卡片底色必须全部退掉。
            vm.ScheduleHighlightClass = null;
            Assert.All(
                vm.ScheduleGroups.SelectMany(g => g.Slots),
                s => Assert.False(s.HasClassHighlight));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 同一时段里有**两个高亮班**时，从一个切到另一个，标签必须跟着变。
    /// <para>
    /// 这是上一版修复漏掉的那一半：布尔值一直是 true，<c>SetProperty</c> 提前返回，
    /// 挂在它后面的标签通知就不会发。预赛一个组塞几个班是常态，不是边角情况。
    /// </para>
    /// <para>
    /// 判定不看标签的最终值（那需要跑完整导入），而是直接验证「切到另一个班时
    /// 标签确实发了通知」——没有通知，绑定就不会重算，界面就停在旧数量上。
    /// </para>
    /// </summary>
    [Fact]
    public async Task SwitchingBetweenTwoHighlightedClasses_RefreshesCountLabel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SlotHighlight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var corpus = MeetPerfCorpus.WriteOrderBook(dir, classes: 60);
            var vm = new MeetRosterViewModel();
            await vm.ImportOrderBookAsync(corpus);

            vm.SelectSection("schedule");
            vm.SelectDay(vm.ScheduleDays.First(d => !d.IsAllDays));

            // 找一个同时含两个不同班条目的时段——60 班语料里这种很多。
            var multi = vm.ScheduleGroups.SelectMany(g => g.Slots)
                .FirstOrDefault(s => s.Entries
                    .Select(e => e.ClassName).Distinct(StringComparer.Ordinal).Count() >= 2);
            Assert.NotNull(multi);

            var classes = multi!.Entries
                .Select(e => e.ClassName).Distinct(StringComparer.Ordinal).Take(2).ToList();
            var first = vm.AllClasses.First(c => c.Name == classes[0]);
            var second = vm.AllClasses.First(c => c.Name == classes[1]);

            vm.ScheduleHighlightClass = first;

            // 先确认这条时段确实被点亮了，否则后面「true→true」的前提不成立。
            Assert.True(multi.HasClassHighlight);
            var labelBefore = multi.HighlightedEntryCountLabel;
            Assert.NotEmpty(labelBefore);

            var notified = new List<string?>();
            multi.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            // 切到同一时段里的另一个班：布尔值仍是 true，但标签内容必须重算。
            vm.ScheduleHighlightClass = second;

            Assert.True(multi.HasClassHighlight);
            Assert.Contains(nameof(ScheduleSlotViewModel.HighlightedEntryCountLabel), notified);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SwitchingHighlightClass_ReusesEntryInstances()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SlotHighlight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var corpus = MeetPerfCorpus.WriteOrderBook(dir, classes: 4);
            var vm = new MeetRosterViewModel();
            await vm.ImportOrderBookAsync(corpus);

            vm.SelectSection("schedule");
            vm.SelectDay(vm.ScheduleDays.First(d => !d.IsAllDays));

            var first = vm.AllClasses.First();
            vm.ScheduleHighlightClass = first;

            var before = vm.ScheduleGroups.SelectMany(g => g.Slots)
                .SelectMany(s => s.Entries).ToList();
            Assert.NotEmpty(before);

            vm.ScheduleHighlightClass = vm.AllClasses.First(c => c.Name != first.Name);

            var after = vm.ScheduleGroups.SelectMany(g => g.Slots)
                .SelectMany(s => s.Entries).ToList();

            // 同一批对象：证明走的是就地更新而不是重建。
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
                Assert.Same(before[i], after[i]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
