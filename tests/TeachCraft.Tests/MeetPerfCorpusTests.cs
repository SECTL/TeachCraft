using MeetRoster.Core.Import;
using MeetRoster.Core.Models;

namespace TeachCraft.Tests;

/// <summary>
/// 「60 个班」合成秩序册语料（<see cref="MeetPerfCorpus"/>）的自检。
/// 语料是性能基准的输入，必须先证明它真的能被 <see cref="OrderBookImporter"/> 读成一份完整的工程，
/// 否则基准量的就是「读取失败有多快」。
/// 全部数据为虚构姓名与合成学校，不含任何真实信息。
/// </summary>
public sealed class MeetPerfCorpusTests
{
    [Fact]
    public void WriteOrderBook_ImportsIntoSixtyClasses()
    {
        WithCorpus(directory =>
        {
            var path = MeetPerfCorpus.WriteOrderBook(directory, classes: 60);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);

            var result = OrderBookImporter.Import(path);

            Assert.True(result.HasRoster);
            Assert.True(result.HasSchedule);
            Assert.Equal(60, result.Project.Classes.Count);
            Assert.Equal(60, result.ClassNames.Count);
            // 每个班都读到了 30 人 → 全班 1800 名运动员。
            Assert.Equal(60 * MeetPerfCorpus.AthletesPerClass, result.AthleteCount);
            Assert.All(result.Project.Classes,
                c => Assert.Equal(MeetPerfCorpus.AthletesPerClass, c.Athletes.Count));
        });
    }

    [Fact]
    public void WriteOrderBook_ProducesAthletesAndScheduleDays()
    {
        WithCorpus(directory =>
        {
            var result = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(directory));

            // 至少一个班有运动员，且班级分组与年级一致。
            var withAthletes = result.Project.Classes.Where(c => c.Athletes.Count > 0).ToList();
            Assert.NotEmpty(withAthletes);
            Assert.All(withAthletes, c => Assert.False(string.IsNullOrWhiteSpace(c.Group)));
            // 名单里的年级 / 班级块头必须被读成「{年级}{班号}班」。
            Assert.All(result.Project.Classes,
                c => Assert.Matches(@"^[七八九]年级\d+班$", c.Name));

            // 两个比赛日（2026-11-11 / 11-12）。
            Assert.Equal(2, result.Project.Days.Count);
            Assert.All(result.Project.Days, d => Assert.NotEmpty(d.Slots));
            Assert.Equal(new DateOnly(2026, 11, 11), result.Project.Days[0].Date);
            Assert.Equal(new DateOnly(2026, 11, 12), result.Project.Days[1].Date);

            // 每个班至少落上一个单项，否则分组表的「班级列只写 N班」就没被拼对。
            Assert.All(result.Project.Classes, c => Assert.Contains(c.Athletes,
                a => a.IndividualEvent1.Length > 0 || a.IndividualEvent2.Length > 0));
        });
    }

    [Fact]
    public void WriteOrderBook_IsReproducible()
    {
        WithCorpus(first =>
        {
            WithCorpus(second =>
            {
                var a = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(first));
                var b = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(second));

                Assert.Equal(ClassAndAthleteFingerprint(a.Project), ClassAndAthleteFingerprint(b.Project));
            });
        });
    }

    [Fact]
    public void WriteOrderBook_GroupingCoversEveryScheduledEntryWithoutUnmatchedNames()
    {
        WithCorpus(directory =>
        {
            var result = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(directory));

            // 分组表里每个人的姓名 / 班级都必须能在名单里找到；对不上的会被导入器记成提醒。
            Assert.DoesNotContain(result.Issues, i =>
                i.Message.Contains("找不到对应姓名", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Issues, i =>
                i.Message.Contains("在名单中找不到", StringComparison.Ordinal));

            // 项目分组表的班级列只写「N班」，导入器必须按块头拼成全名——否则所有条目都会被判成陌生班级。
            var entries = result.Project.Days.SelectMany(d => d.Slots).SelectMany(s => s.Entries).ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.Contains(result.ClassNames, name => name == e.ClassName));
        });
    }

    [Fact]
    public void WriteOrderBook_LongDistanceHeatSplitAcrossLanePagesIsReadInFull()
    {
        WithCorpus(directory =>
        {
            var result = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(directory));

            // 1000 米 / 800 米按真实秩序册的做法排成「单组跨页」：每页 8 道，续页重新给一行道次。
            // 导入器必须把三页读全（旧实现只读第一页，34 人的 800 米只读到 16 人）。
            var slots = result.Project.Days.SelectMany(d => d.Slots).ToList();
            var longDistance = slots.Where(s => s.EventName is "1000米" or "800米").ToList();
            Assert.Equal(6, longDistance.Count); // 3 个年级 × 男女各 1 项
            Assert.All(longDistance, s => Assert.Equal(24, s.Entries.Count));
            Assert.All(longDistance, s => Assert.All(s.Entries,
                e => Assert.Contains("第 1 组", e.Lane, StringComparison.Ordinal)));

            // 续页的道次必须延续下去（第 9 道起），不能整组重新编号。
            Assert.Contains(longDistance.SelectMany(s => s.Entries),
                e => e.Lane.Contains("第 9 道", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void WriteOrderBook_TeamEventsStayOnTheClassNotOnAthletes()
    {
        WithCorpus(directory =>
        {
            var result = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(directory));

            // 团体项目按班登记：秩序册不点名队员，不能把项目伪造成「全班都跑接力」。
            Assert.All(result.Project.Classes, c => Assert.NotEmpty(c.TeamEvents));
            Assert.All(result.Project.Classes, c => Assert.All(c.Athletes,
                a => Assert.Equal(string.Empty, a.TeamEvent1)));

            var teamEntries = result.Project.Days
                .SelectMany(d => d.Slots)
                .Where(s => s.EventName.Contains("接力", StringComparison.Ordinal))
                .SelectMany(s => s.Entries)
                .ToList();
            Assert.NotEmpty(teamEntries);
            Assert.All(teamEntries, e => Assert.Contains("队", e.AthleteName, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void WriteOrderBook_UsesOnlyFictionalAndDistinctNames()
    {
        WithCorpus(directory =>
        {
            var result = OrderBookImporter.Import(MeetPerfCorpus.WriteOrderBook(directory));

            var names = result.Project.Classes.SelectMany(c => c.Athletes).Select(a => a.Name).ToList();
            Assert.Equal(60 * MeetPerfCorpus.AthletesPerClass, names.Count);
            // 每个姓名都不重复：合成姓名池是「50 个姓 × 60 个名」的全排列采样。
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            // 姓名一律是三字合成名（单字姓 + 双字名），不出现单字名或数字占位。
            Assert.All(names, n => Assert.Matches(@"^[一-龥]{3}$", n));
            // 号码布也全局唯一（导入器会把重复号码记成提醒）。
            var bibs = result.Project.Classes.SelectMany(c => c.Athletes).Select(a => a.Bib).ToList();
            Assert.Equal(bibs.Count, bibs.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(result.Issues,
                i => i.Message.Contains("号码布重复", StringComparison.Ordinal));
        });
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>在临时目录里跑一段测试，结束时无论成败都删掉目录（生成物一律不入库）。</summary>
    private static void WithCorpus(Action<string> body)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MeetPerfCorpus.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            body(directory);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* 清理失败不影响测试结论 */ }
        }
    }

    /// <summary>「班级名 + 运动员姓名」序列：用来比对两次生成是否一致（可复现）。</summary>
    private static string ClassAndAthleteFingerprint(MeetProject project) =>
        string.Join("\n", project.Classes.Select(c =>
            c.Name + ":" + string.Join("、", c.Athletes.Select(a => a.Name))));
}
