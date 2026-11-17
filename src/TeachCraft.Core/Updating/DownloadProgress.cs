using System;
using System.Collections.Generic;

namespace TeachCraft.Core.Updating;

/// <summary>
/// 一次下载的进度快照。
///
/// 为什么不再只用 <c>int</c> 百分比：老师看到的「正在下载 45%」卡住不动时，
/// 分不清是在慢、还是根本不动。有速度与剩余时间才答得了「还要等多久」。
/// </summary>
/// <param name="Percent">0–100。拿不到总大小时恒为 0，此时界面应只显示已下载量与速度。</param>
/// <param name="BytesReceived">已下载字节数。</param>
/// <param name="TotalBytes">总字节数；未知时为 0。</param>
/// <param name="BytesPerSecond">当前速度；样本不足或已停止时为 0。</param>
/// <param name="IsSizeKnown">是否知道总大小。</param>
public readonly record struct DownloadProgress(
    int Percent,
    long BytesReceived,
    long TotalBytes,
    double BytesPerSecond,
    bool IsSizeKnown)
{
    /// <summary>根据速度估算剩余时间；速度未知时返回 null。</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (!IsSizeKnown || BytesPerSecond <= 0) return null;
            var left = TotalBytes - BytesReceived;
            if (left <= 0) return TimeSpan.Zero;

            // 离谱值（限速抖动会算出几天）不显示，交给界面显示「即将完成」。
            var seconds = left / BytesPerSecond;
            return seconds > 24 * 3600 ? null : TimeSpan.FromSeconds(seconds);
        }
    }
}

/// <summary>
/// 下载速度的滑动窗口采样。
///
/// **不能用「总字节 / 总耗时」**：那是全程平均，一开始的慢启动会一直拖着，
/// 后面网速明明恢复了数字却还在爬；反过来，若中途卡住，平均值又是缓慢下滑，
/// 迟迟不显示「快停了」。滑动窗口只看最近几秒，才对得上「现在有多快」。
///
/// 纯函数式：喂时间戳与字节数，拿走速度。时间由调用方给，便于测试注入假时钟。
/// </summary>
public sealed class DownloadSpeedMeter
{
    /// <summary>窗口长度。3 秒足以平滑抖动，又不至于对「网断了」反应太慢。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _window;
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private DateTimeOffset _startedAt;
    private long _startedBytes;
    private bool _started;

    public DownloadSpeedMeter(TimeSpan? window = null)
    {
        _window = window ?? DefaultWindow;
    }

    /// <summary>开始计速。第一个采样点既是窗口起点，也是「还没动」的基准。</summary>
    public void Start(DateTimeOffset now, long bytes)
    {
        _samples.Clear();
        _samples.Enqueue((now, bytes));
        _startedAt = now;
        _startedBytes = bytes;
        _started = true;
    }

    /// <summary>
    /// 记录一个采样点并返回当前速度（字节/秒）。
    ///
    /// 返回 0 表示「不足以判断」：样本跨度太小（刚开始那几十毫秒）时除以接近零的
    /// 时间会得到荒谬的巨大值，界面上会跳出「12.3 GB/s」这种一眼假的东西。
    /// </summary>
    public double Sample(DateTimeOffset now, long bytes)
    {
        if (!_started) Start(now, bytes);

        _samples.Enqueue((now, bytes));

        // 丢掉窗口外的样本，但**永远保留至少两个点**，否则窗口刚滑过起点时会瞬间没有基准。
        while (_samples.Count > 2)
        {
            var oldest = _samples.Peek();
            if (now - oldest.At <= _window) break;
            _samples.Dequeue();
        }

        var first = _samples.Peek();
        var elapsed = (now - first.At).TotalSeconds;

        // 太短的跨度算不准：至少 250ms 才出数。
        if (elapsed < 0.25) return 0;

        var delta = bytes - first.Bytes;
        if (delta <= 0) return 0;

        return delta / elapsed;
    }

    /// <summary>全程平均速度，用于「下载完成」时的总结。</summary>
    public double Overall(DateTimeOffset now, long bytes)
    {
        if (!_started) return 0;
        var seconds = (now - _startedAt).TotalSeconds;
        if (seconds <= 0) return 0;
        var delta = bytes - _startedBytes;
        return delta <= 0 ? 0 : delta / seconds;
    }
}

/// <summary>把字节数与人读的时间变成界面文案。纯函数，好测也好复用。</summary>
public static class DownloadText
{
    /// <summary>「1.4 MB/s」「820 KB/s」。速度未知或为 0 时返回空串，界面据此隐藏。</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1) return string.Empty;

        // 低于 1 KB/s 时按 B/s 显示，免得出现「0.0 KB/s」这种看着像断流的数字。
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";

        var kb = bytesPerSecond / 1024;
        if (kb < 1024) return $"{kb:0.#} KB/s";

        var mb = kb / 1024;
        // 到 GB 量级说明是内网 / 校园盘，虽然少见，但硬写成「1280 MB/s」不如「1.25 GB/s」好读。
        return mb < 1024 ? $"{mb:0.##} MB/s" : $"{mb / 1024:0.##} GB/s";
    }

    /// <summary>「已下载 12.4 MB / 58.2 MB」；总大小未知时只报已下载量。</summary>
    public static string Amount(long bytesReceived, long totalBytes, bool sizeKnown)
    {
        var done = Size(bytesReceived);
        return sizeKnown && totalBytes > 0 ? $"已下载 {done} / {Size(totalBytes)}" : $"已下载 {done}";
    }

    /// <summary>「剩余约 1 分 20 秒」；剩余不足 1 秒返回「即将完成」，未知返回空串。</summary>
    public static string Remaining(TimeSpan? remaining)
    {
        if (remaining is not { } left) return string.Empty;
        if (left <= TimeSpan.Zero) return "即将完成";

        // 四舍五入到秒，避免「剩余约 1 分 19.983 秒」。
        var total = (int)Math.Round(left.TotalSeconds);
        if (total < 1) return "即将完成";
        if (total < 60) return $"剩余约 {total} 秒";

        var minutes = total / 60;
        var seconds = total % 60;
        if (minutes < 60)
            return seconds == 0 ? $"剩余约 {minutes} 分" : $"剩余约 {minutes} 分 {seconds} 秒";

        var hours = minutes / 60;
        minutes %= 60;
        return minutes == 0 ? $"剩余约 {hours} 小时" : $"剩余约 {hours} 小时 {minutes} 分";
    }

    /// <summary>「12.4 MB」「812 KB」「640 字节」。</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} 字节";

        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";

        var mb = kb / 1024;
        return mb < 1024 ? $"{mb:0.#} MB" : $"{mb / 1024:0.##} GB";
    }

    /// <summary>
    /// 量 / 速度 / 剩余，**不含百分比**：「已下载 26.2 MB / 58.2 MB · 1.4 MB/s · 剩余约 23 秒」。
    ///
    /// 更新页上面那行已经写了「正在下载… 45%」，这里再报一遍就是同一个数字出现两次。
    /// 状态栏那种没有单独进度显示的地方用 <see cref="ProgressLine"/>。
    /// </summary>
    public static string AmountLine(DownloadProgress progress)
    {
        var parts = new List<string>(3);
        parts.Add(Amount(progress.BytesReceived, progress.TotalBytes, progress.IsSizeKnown));

        var speed = Speed(progress.BytesPerSecond);
        if (speed.Length > 0) parts.Add(speed);

        var remaining = Remaining(progress.Remaining);
        if (remaining.Length > 0) parts.Add(remaining);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 拼出完整一行（含百分比）：「45% · 12.4 MB / 58.2 MB · 1.4 MB/s · 剩余约 32 秒」。
    /// 速度未知时自动省略对应片段，不留多余的分隔点。
    /// </summary>
    public static string ProgressLine(DownloadProgress progress)
    {
        var parts = new List<string>(4);

        if (progress.IsSizeKnown) parts.Add($"{progress.Percent}%");
        parts.Add(DownloadText.Amount(progress.BytesReceived, progress.TotalBytes, progress.IsSizeKnown));

        var speed = Speed(progress.BytesPerSecond);
        if (speed.Length > 0) parts.Add(speed);

        var remaining = Remaining(progress.Remaining);
        if (remaining.Length > 0) parts.Add(remaining);

        return string.Join(" · ", parts);
    }
}
