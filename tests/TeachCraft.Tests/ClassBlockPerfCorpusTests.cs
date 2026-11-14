using ClassBlock.Core.Import;
using TeachCraft.App.Diagnostics;

namespace TeachCraft.Tests;

/// <summary>
/// T4 语料自检：确认合成总表真的被识别成 20 个班、每班 7 节课，
/// 否则 <c>--perf-bench</c> 里「时序 · 读取总表」那一行是空跑的假数字
/// （竞名那边就踩过这个坑：语料列位写错，classes=0 却照样输出耗时）。
/// </summary>
public sealed class ClassBlockBenchTests
{
    [Fact]
    public void Corpus_IsRecognizedAsTwentyClasses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClassBlockBench",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 注册一次就会生成语料；直接调 Register 后手动跑第一个基准。
            ClassBlockBench.Register();
            var gen = PerfBench.BusinessBenchmarks.First(b => b.Name == "时序 · 生成 20 班语料");
            gen.Action();

            // 找到测试期间生成的语料文件（临时目录下最新的「示例中学总表.xlsx」）。
            var root = Path.Combine(Path.GetTempPath(), "TeachCraft.PerfBench");
            var corpus = Directory.GetFiles(root, "示例中学总表.xlsx", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            Assert.NotNull(corpus);

            var master = MasterScheduleImporter.Import(corpus!);

            Assert.Equal(20, master.Classes.Count);
            Assert.All(master.Classes, c => Assert.Equal(7, c.Days[0].Periods.Count));
            Assert.All(master.Classes, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));

            // 早读备注必须被读到，否则基准里那条路径根本没被覆盖。
            Assert.Equal("周二、周四 语文", master.Info.EarlyReadingRule);

            // 班主任与午学也要真的读到。
            Assert.Equal(20, master.HeadTeachers.Count);
            Assert.Equal(20, master.LunchStudy.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }

            // 这条测试直接调基准动作，绕过了 PerfBench.RunBusinessBenchmarks 的
            // 统一清理，所以得自己把语料目录收掉——否则跑一次测试就在 %TEMP%
            // 里留一份 20 班语料，「基准跑完零残留」的检查会被它带偏。
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "TeachCraft.PerfBench");
                if (Directory.Exists(root))
                {
                    foreach (var sub in Directory.GetDirectories(root))
                    {
                        if (Directory.GetFiles(sub, "示例中学总表.xlsx", SearchOption.AllDirectories).Length > 0)
                            Directory.Delete(sub, recursive: true);
                    }
                }
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
