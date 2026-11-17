using System.Net;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 下载速度显示。
///
/// 为什么不能简单用「总字节 / 总耗时」：那是全程平均，开头慢启动会一直拖着，
/// 后面网速恢复了数字还在爬；中途卡住时平均值又是缓慢下滑，迟迟不显示「快停了」。
/// 这里钉死的是**滑动窗口**的行为，以及文案在各种边界下的样子。
/// </summary>
public sealed class UpdateSpeedTests : IDisposable
{
    private readonly string _root;

    public UpdateSpeedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", "Speed", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    // ----------------------------- 速度计 -----------------------------

    [Fact]
    public void Meter_NoSpeedBeforeEnoughTimeHasPassed()
    {
        // 刚开始那几十毫秒里除以接近零的时间会得到荒谬的巨大值，
        // 界面上会跳出「12.3 GB/s」这种一眼假的东西。
        var meter = new DownloadSpeedMeter();
        meter.Start(T0, 0);

        Assert.Equal(0, meter.Sample(T0.AddMilliseconds(50), 10 * 1024));
        Assert.Equal(0, meter.Sample(T0.AddMilliseconds(200), 40 * 1024));
    }

    [Fact]
    public void Meter_ReportsSteadySpeed()
    {
        var meter = new DownloadSpeedMeter();
        meter.Start(T0, 0);

        // 每 500ms 收到 512 KB → 1 MB/s。
        meter.Sample(T0.AddSeconds(1), 1024 * 1024);
        var speed = meter.Sample(T0.AddSeconds(2), 2 * 1024 * 1024);

        Assert.InRange(speed, 900 * 1024, 1100 * 1024);
    }

    [Fact]
    public void Meter_TracksRecoveryRatherThanLifetimeAverage()
    {
        // 这是滑动窗口存在的理由：前 10 秒很慢、之后很快。
        // 全程平均会被慢启动拖住，窗口值必须跟得上「现在」。
        var meter = new DownloadSpeedMeter();
        meter.Start(T0, 0);

        // 前 10 秒：每秒 100 KB
        for (var s = 1; s <= 10; s++)
            meter.Sample(T0.AddSeconds(s), s * 100 * 1024);

        // 之后 3 秒：每秒 5 MB
        var bytes = 10 * 100 * 1024;
        for (var s = 11; s <= 13; s++)
        {
            bytes += 5 * 1024 * 1024;
            meter.Sample(T0.AddSeconds(s), bytes);
        }

        var speed = meter.Sample(T0.AddSeconds(14), bytes + 5 * 1024 * 1024);
        var lifetimeAverage = (bytes + 5 * 1024 * 1024) / 14.0;

        Assert.True(speed > 4 * 1024 * 1024, $"窗口速度应当反映当前的 5 MB/s，实际 {speed:0}");
        Assert.True(speed > lifetimeAverage * 3, "窗口值必须明显高于全程平均");
    }

    [Fact]
    public void Meter_ReportsZeroWhenStalled()
    {
        // 卡住时不能还显示上一个速度——那会让老师以为在下，其实没动。
        var meter = new DownloadSpeedMeter();
        meter.Start(T0, 0);
        meter.Sample(T0.AddSeconds(1), 1024 * 1024);
        meter.Sample(T0.AddSeconds(2), 2 * 1024 * 1024);

        // 又过 5 秒，字节数没变（窗口已经滑过全部有增量的样本）。
        var stalled = meter.Sample(T0.AddSeconds(7), 2 * 1024 * 1024);
        Assert.Equal(0, stalled);
    }

    [Fact]
    public void Meter_OverallIsLifetimeAverage()
    {
        var meter = new DownloadSpeedMeter();
        meter.Start(T0, 0);
        meter.Sample(T0.AddSeconds(1), 1024 * 1024);
        meter.Sample(T0.AddSeconds(2), 2 * 1024 * 1024);

        var overall = meter.Overall(T0.AddSeconds(4), 4 * 1024 * 1024);
        Assert.InRange(overall, 900 * 1024, 1100 * 1024);
    }

    // ----------------------------- 文案 -----------------------------

    [Theory]
    [InlineData(0, "")]
    [InlineData(0.4, "")]
    [InlineData(800, "800 B/s")]
    [InlineData(1024, "1 KB/s")]
    [InlineData(1536, "1.5 KB/s")]
    [InlineData(1024 * 1024, "1 MB/s")]
    [InlineData((long)(2.5 * 1024 * 1024), "2.5 MB/s")]
    [InlineData((long)(1.25 * 1024 * 1024 * 1024), "1.25 GB/s")]
    public void SpeedText_FormatsHumanReadable(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, DownloadText.Speed(bytesPerSecond));
    }

    [Theory]
    [InlineData(512, "512 字节")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData((long)(58.2 * 1024 * 1024), "58.2 MB")]
    public void SizeText_FormatsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadText.Size(bytes));
    }

    [Fact]
    public void AmountText_ShowsTotalOnlyWhenKnown()
    {
        Assert.Equal("已下载 12.4 MB / 58.2 MB",
            DownloadText.Amount((long)(12.4 * 1024 * 1024), (long)(58.2 * 1024 * 1024), sizeKnown: true));

        // 不知道总大小时不能编一个出来。
        Assert.Equal("已下载 12.4 MB",
            DownloadText.Amount((long)(12.4 * 1024 * 1024), 0, sizeKnown: false));
    }

    [Theory]
    [InlineData(0, "即将完成")]
    [InlineData(-5, "即将完成")]
    [InlineData(1, "剩余约 1 秒")]
    [InlineData(32, "剩余约 32 秒")]
    [InlineData(59, "剩余约 59 秒")]
    [InlineData(60, "剩余约 1 分")]
    [InlineData(80, "剩余约 1 分 20 秒")]
    [InlineData(3600, "剩余约 1 小时")]
    [InlineData(3900, "剩余约 1 小时 5 分")]
    public void RemainingText_RoundsToSomethingReadable(int seconds, string expected)
    {
        Assert.Equal(expected, DownloadText.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void RemainingText_EmptyWhenUnknown()
    {
        Assert.Equal(string.Empty, DownloadText.Remaining(null));
    }

    [Fact]
    public void ProgressLine_OmitsSpeedBeforeItIsKnown()
    {
        // 刚开始还没有速度样本：不能显示「0 B/s」，那看着像断流。
        var line = DownloadText.ProgressLine(new DownloadProgress(
            Percent: 3, BytesReceived: 1024 * 1024, TotalBytes: 32 * 1024 * 1024,
            BytesPerSecond: 0, IsSizeKnown: true));

        Assert.Equal("3% · 已下载 1 MB / 32 MB", line);
        Assert.DoesNotContain("B/s", line);
    }

    [Fact]
    public void ProgressLine_IncludesSpeedAndRemaining()
    {
        // 32 MB 总量、已下 1 MB、1 MB/s → 还剩约 31 秒。
        var progress = new DownloadProgress(
            Percent: 3, BytesReceived: 1024 * 1024, TotalBytes: 32 * 1024 * 1024,
            BytesPerSecond: 1024 * 1024, IsSizeKnown: true);

        var line = DownloadText.ProgressLine(progress);

        Assert.Contains("3%", line);
        Assert.Contains("已下载 1 MB / 32 MB", line);
        Assert.Contains("1 MB/s", line);
        Assert.Contains("剩余约 31 秒", line);
    }

    [Fact]
    public void AmountLine_OmitsPercentBecauseItIsShownElsewhere()
    {
        // 更新页上面已经写「正在下载… 45%」，速度行再报一遍就是同一个数字出现两次。
        var progress = new DownloadProgress(
            Percent: 45, BytesReceived: (long)(26.2 * 1024 * 1024),
            TotalBytes: (long)(58.2 * 1024 * 1024),
            BytesPerSecond: 1.42 * 1024 * 1024, IsSizeKnown: true);

        var line = DownloadText.AmountLine(progress);

        Assert.DoesNotContain("%", line);
        Assert.Contains("已下载 26.2 MB / 58.2 MB", line);
        Assert.Contains("1.42 MB/s", line);
        Assert.Contains("剩余约", line);
    }

    [Fact]
    public void ProgressLine_HidesPercentWhenSizeUnknown()
    {
        var line = DownloadText.ProgressLine(new DownloadProgress(
            Percent: 0, BytesReceived: 5 * 1024 * 1024, TotalBytes: 0,
            BytesPerSecond: 2 * 1024 * 1024, IsSizeKnown: false));

        Assert.DoesNotContain("%", line);
        Assert.StartsWith("已下载 5 MB", line);
    }

    [Fact]
    public void Remaining_IsNullForAbsurdEstimates()
    {
        // 限速抖动会算出一整天；那种数字没有意义，交给界面不显示。
        var progress = new DownloadProgress(
            Percent: 1, BytesReceived: 1024, TotalBytes: 10L * 1024 * 1024 * 1024,
            BytesPerSecond: 1024, IsSizeKnown: true);   // 约 115 天

        Assert.Null(progress.Remaining);
    }

    // ----------------------------- 端到端 -----------------------------

    [Fact]
    public async Task Download_ReportsDetailedProgressWithGrowingByteCount()
    {
        // 走真实下载循环：确认详细重载真的被调用、字节数递增、终态是 100。
        var payload = new byte[256 * 1024];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var asset = new ReleaseAsset("pkg.zip", payload.Length, "https://example/download/pkg.zip");
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

        var seen = new List<DownloadProgress>();
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(
            asset, "v1.33.0", expectedSha256: null, CancellationToken.None,
            new Progress<DownloadProgress>(p => seen.Add(p)));

        Assert.True(result.IsSuccess, $"失败原因：{result.Failure}；请求过的 URL：{string.Join(",", handler.RequestedUrls)}");

        // Progress<T> 会把回调投递到同步上下文，可能要等一会儿才排空。
        for (var i = 0; i < 50 && seen.Count == 0; i++) await Task.Delay(10);

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.True(p.IsSizeKnown));
        Assert.All(seen, p => Assert.Equal(payload.Length, p.TotalBytes));

        // 字节数单调不减，最后一次报满。
        for (var i = 1; i < seen.Count; i++)
            Assert.True(seen[i].BytesReceived >= seen[i - 1].BytesReceived);

        Assert.Equal(payload.Length, seen[^1].BytesReceived);
        Assert.Equal(100, seen[^1].Percent);
    }

    [Fact]
    public async Task Download_RejectsBodyShorterThanDeclaredSizeEvenIfHeaderAgrees()
    {
        // 资产声明 1024、响应头也写 1024、实际只给 512：只比响应头会通过，
        // 半截文件被当成完好的。两个基准都必须校验。
        var payload = new byte[512];
        var asset = new ReleaseAsset("pkg.zip", 1024, "https://example/download/pkg.zip");

        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl, () =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = 1024;   // 响应头撒谎
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var downloader = new UpdateDownloader(handler, _root);
        var result = await downloader.DownloadAsync(asset, "v1.33.0", expectedSha256: null);

        Assert.False(result.IsSuccess);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_UsesHeaderLengthForProgressWhenItDiffersFromAsset()
    {
        // 镜像改写了大小：进度应跟随实际传输量（响应头），而不是发布页声明的旧值，
        // 否则进度条会一直停在 50% 之类的位置直到结束。
        var payload = new byte[8192];
        var asset = new ReleaseAsset("pkg.zip", 4096, "https://example/download/pkg.zip");   // 声明偏小

        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

        var seen = new List<DownloadProgress>();
        var downloader = new UpdateDownloader(handler, _root);
        var result = await downloader.DownloadAsync(
            asset, "v1.33.0", null, CancellationToken.None,
            new Progress<DownloadProgress>(p => seen.Add(p)));

        for (var i = 0; i < 50 && seen.Count == 0; i++) await Task.Delay(10);
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(8192, p.TotalBytes));
    }

    [Fact]
    public async Task Download_LegacyPercentOverloadStillWorks()
    {
        // 旧重载必须继续可用（状态栏那条路径还在用它）。
        var payload = new byte[64 * 1024];
        var asset = new ReleaseAsset("pkg.zip", payload.Length, "https://example/download/pkg.zip");
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

        var percents = new List<int>();
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(
            asset, "v1.33.0", null, CancellationToken.None, new Progress<int>(p => percents.Add(p)));

        Assert.True(result.IsSuccess, result.Failure);
        for (var i = 0; i < 50 && percents.Count == 0; i++) await Task.Delay(10);

        Assert.NotEmpty(percents);
        Assert.Equal(100, percents[^1]);
    }
}
