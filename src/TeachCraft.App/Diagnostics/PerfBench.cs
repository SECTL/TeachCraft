using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TeachCraft.App.Diagnostics;

/// <summary>
/// 性能基准走查通道（--perf-bench &lt;md路径&gt;）：把若干段可重复的操作用
/// <see cref="Measure"/> 采样若干次，再用 <see cref="RenderReport"/> 输出一份
/// Markdown 报告（环境段 + 每项一行 p50 / p95 / min / max）。
///
/// 它只做「在本机跑一遍、把数字留下来」这一件事，不参与真实业务流程，
/// 也不联网、不写仓库：输出路径完全由命令行参数决定。
///
/// 为了证明这条通道现在就是通的（否则报告只有表头、无从验证），
/// <see cref="RenderReport"/> 会在还没有任何采样时先跑两项自检基准
/// （见 <see cref="RunSelfChecks"/>）。那两项只是线性累加与整数排序，
/// 用来验证计时、百分位与渲染三件事，不代表任何业务场景的性能。
/// </summary>
public static class PerfBench
{
    /// <summary>
    /// 自检基准的固定种子：同一台机器上多次运行得到同一份输入，
    /// 排序耗时才有可比性（换种子会因为数据分布不同而飘）。
    /// </summary>
    private const int SelfCheckSeed = 20261110;

    /// <summary>预热空转的循环次数。</summary>
    private const int SelfCheckLoopCount = 200_000;

    /// <summary>自检排序的元素个数。</summary>
    private const int SelfCheckSortCount = 10_000;

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static readonly object Gate = new();
    private static readonly List<Measurement> Measurements = new();

    /// <summary>自检基准是否已经跑过：只跑一次，重复出报告不会把自检行越堆越多。</summary>
    private static bool _selfChecksDone;

    /// <summary>
    /// 清空已登记的采样，让一份报告只包含「本轮」跑过的项目。
    /// <para>
    /// 为什么需要它：<see cref="Measurements"/> 是进程级静态表。本通道现在只跑自检项，
    /// 所以重复出报告没有影响；但一旦后续把竞名导入 / 时序导入等业务基准也接进来
    /// （见 <c>docs/performance-audit/task.md</c> 的 T3 / T4），同一个进程里先后跑两组基准
    /// 就会把两组的行混在一张表里，读起来无从分辨。
    /// </para>
    /// <para>
    /// 自检门闩 <see cref="_selfChecksDone"/> 会一起放回 false，让下一次 <see cref="RenderReport"/>
    /// 重新跑自检——否则清空后的报告会是一张只有表头、没有数据行的空表。
    /// 连续调用 <see cref="RenderReport"/> 仍然只会得到一份自检行，不会越堆越多。
    /// </para>
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Measurements.Clear();

            // 一并放下自检门闩：否则清空后再出报告会得到一张只有表头、没有任何行的空表
            // （自检项被跳过，而新的业务采样还没登记）。
            _selfChecksDone = false;
        }
    }

    /// <summary>
    /// 采集 <paramref name="iterations"/> 次 <paramref name="action"/> 的耗时（毫秒），
    /// 结果挂到 <paramref name="name"/> 名下，等 <see cref="RenderReport"/> 统一渲染。
    /// 第一次采样可能包含 JIT 开销，属正常现象。
    /// </summary>
    /// <param name="name">报告里的「项目」名，中文即可。</param>
    /// <param name="action">被测量的一段同步操作；不接收参数、不返回结果。</param>
    /// <param name="iterations">样本数，至少 1 次。</param>
    public static void Measure(string name, Action action, int iterations = 5)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        // 走查通道宁可估个数字，也不要因为参数写错整条报告失败。
        var count = Math.Max(1, iterations);
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }

        lock (Gate)
        {
            Measurements.Add(new Measurement(name, samples));
        }
    }

    /// <summary>
    /// 输出 Markdown 报告：先环境段（机器与运行时），再一张「项目 | 样本数 | p50 | p95 | min | max」表。
    /// 还没有任何采样时会先跑内置自检基准，保证报告不为空。
    /// </summary>
    public static string RenderReport()
    {
        EnsureSelfChecks();

        Measurement[] snapshot;
        lock (Gate)
        {
            snapshot = Measurements.ToArray();
        }

        var report = new StringBuilder();
        report.AppendLine("# 知微 · TeachCraft 性能基准（--perf-bench）");
        report.AppendLine();
        report.AppendLine("## 环境");
        report.AppendLine();
        report.AppendLine($"- 采集时间：{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        report.AppendLine($"- 构建配置：{(IsDebugBuild ? "Debug（数字仅供参考，请用 Release 复测）" : "Release")}");
        report.AppendLine($"- 逻辑核数：{Environment.ProcessorCount}");
        report.AppendLine($"- GC 可用内存（TotalAvailableMemoryBytes）：{AvailableMemoryMb():F0} MB");
        report.AppendLine($"- 操作系统：{RuntimeInformation.OSDescription}");
        report.AppendLine($"- 进程架构：{RuntimeInformation.ProcessArchitecture}");
        report.AppendLine();
        report.AppendLine("## 结果");
        report.AppendLine();
        report.AppendLine("| 项目 | 样本数 | p50 (ms) | p95 (ms) | min | max |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var measurement in snapshot)
        {
            var sorted = (double[])measurement.Samples.Clone();
            Array.Sort(sorted);
            var min = sorted[0];
            var max = sorted[^1];
            report.AppendLine(
                $"| {measurement.Name} | {sorted.Length} | {Percentile(sorted, 0.50):F3} | " +
                $"{Percentile(sorted, 0.95):F3} | {min:F3} | {max:F3} |");
        }

        report.AppendLine();
        report.AppendLine("说明：预热-空转与排序-10000 是通道自检项，用来证明计时、百分位与渲染可用，");
        report.AppendLine("不是业务基准，不要拿它们的数字比较版本或机器。");
        return report.ToString();
    }

    /// <summary>
    /// 内置自检基准：只证明这条通道能测、能算、能出表。
    /// 两项都刻意写成纯 CPU 的同步操作，不碰文件与网络，重复跑也安全。
    /// </summary>
    private static void RunSelfChecks()
    {
        // 1) 预热-空转：O(n) 累加。结果写进静态字段 _sink，
        //    避免被 JIT 当成无用计算整段优化掉（那样量到的接近 0，报告会撒谎）。
        Measure("预热-空转", () =>
        {
            long sum = 0;
            for (var i = 0; i < SelfCheckLoopCount; i++) sum += i;
            _sink = sum;
        });

        // 2) 排序-10000：固定种子生成一份输入，每次只排它的副本。
        //    输入在计时外生成，样本之间才可比；种子固定，换机器也能复现同一组数据。
        var random = new Random(SelfCheckSeed);
        var source = new int[SelfCheckSortCount];
        for (var i = 0; i < source.Length; i++) source[i] = random.Next();
        Measure("排序-10000", () =>
        {
            var copy = (int[])source.Clone();
            Array.Sort(copy);
            _sink = copy[0];
        });
    }

    /// <summary>
    /// 自检项只跑一次；重复调用 <see cref="RenderReport"/> 不会把表越堆越长。
    /// 整个采集过程放在同一把锁里（Monitor 可重入，<see cref="Measure"/> 再取一次锁是安全的），
    /// 这样并发出报告时第二个调用者拿到的是完整结果，中途抛异常也不会被误标成「已跑过」。
    /// </summary>
    private static void EnsureSelfChecks()
    {
        lock (Gate)
        {
            if (_selfChecksDone) return;
            RunSelfChecks();
            _selfChecksDone = true;
        }
    }

    private static double AvailableMemoryMb() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);

    /// <summary>
    /// 百分位：样本升序后取 index = ceil(p * n) - 1，再夹到 [0, n-1]。
    /// 样本量为 1 时 p50 / p95 都等于那唯一一次，不会越界；不引入第三方统计库。
    /// </summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>自检基准的落地字段，唯一作用是不让累加 / 排序结果被优化掉。</summary>
    private static long _sink;

    private sealed record Measurement(string Name, double[] Samples);
}
