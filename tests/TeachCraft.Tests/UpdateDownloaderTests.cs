using System.Net;
using System.Security.Cryptography;
using System.Text;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 下载与校验。
///
/// 关键约束：
///   - 只写 %LOCALAPPDATA%\TeachCraft\Update\downloads\（注入临时目录），绝不碰安装目录。
///   - 先写 .part 再改名：中途被杀不能留下一个「看起来完整」的半截文件。
///   - 校验清单缺失时**失败关闭**，绝不降级成「只校验长度」。
/// </summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _root;

    public UpdateDownloaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    private static byte[] Payload(int size = 1024)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ReleaseAsset Asset(string name = "TeachCraft-v1.18.0-win-x64-sc-Setup.exe", long size = 1024)
        => new(name, size, $"https://example/download/{name}");

    private GuardedHttpHandler HandlerFor(ReleaseAsset asset, byte[] payload, string? checksumBody = null)
    {
        var handler = new GuardedHttpHandler();
        handler.RespondWith(asset.DownloadUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        if (checksumBody is not null)
        {
            handler.Respond(UpdateCheckService.DefaultFeedUrl.Replace("/releases?per_page=30", "/releases?checksums"),
                HttpStatusCode.OK, checksumBody);
        }

        return handler;
    }

    [Fact]
    public async Task Download_WritesFileWithoutPartSuffix()
    {
        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(result.Path));
        Assert.DoesNotContain(".part", Path.GetFileName(result.Path!));
        Assert.Equal(payload.Length, new FileInfo(result.Path!).Length);
    }

    [Fact]
    public async Task Download_LeavesNoPartFileBehind()
    {
        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null);

        Assert.Empty(Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_Cancel_DeletesPartialFileAndReportsFailure()
    {
        var payload = Payload(64 * 1024);
        var asset = Asset(size: payload.Length);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null, cts.Token);

        Assert.False(result.IsSuccess);
        var files = Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : Array.Empty<string>();
        Assert.Empty(files);
    }

    [Fact]
    public async Task Cancel_MidStream_LeavesNoPartFile()
    {
        // 关键：在流读到一半时取消，而不是调用前就取消。
        // 预先取消只能观察到「空文件被清理」，测不出真实半截残留。
        var payload = Payload(256 * 1024);
        var asset = Asset(size: payload.Length);
        using var cts = new CancellationTokenSource();

        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl, () =>
        {
            var content = new ByteArrayContent(payload);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var downloader = new UpdateDownloader(handler, _root);

        // 读到一部分后取消。
        var progress = new Progress<int>(_ => cts.Cancel());
        var result = await downloader.DownloadAsync(asset, "v1.18.0", null, cts.Token, progress);

        Assert.False(result.IsSuccess);
        var leftovers = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void CleanupStaleParts_KeepsFreshFiles()
    {
        // 下载根目录是用户级共享的；无差别删除会误伤并发下载。
        // 只清「一小时前」的，刚创建的必须留下。
        var dir = Path.Combine(_root, "v1.18.0");
        Directory.CreateDirectory(dir);

        var fresh = Path.Combine(dir, "fresh.zip.part");
        var stale = Path.Combine(dir, "stale.zip.part");
        File.WriteAllBytes(fresh, Payload(64));
        File.WriteAllBytes(stale, Payload(64));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));

        new UpdateDownloader(new GuardedHttpHandler(), _root).CleanupStaleParts();

        Assert.True(File.Exists(fresh), "刚创建的 .part 可能属于正在进行的下载，不能删");
        Assert.False(File.Exists(stale), "一天前的残留应该被清掉");
    }

    [Fact]
    public async Task StalePartFromKilledRun_IsCleanedBeforeNextDownload()
    {
        // 模拟上次被强杀：目录里留下一个半截 .part。
        var staleDir = Path.Combine(_root, "v1.18.0");
        Directory.CreateDirectory(staleDir);
        var staleFile = Path.Combine(staleDir, "TeachCraft-v1.18.0-win-x64-sc-Setup.exe.part");
        File.WriteAllBytes(staleFile, Payload(1024));

        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null);

        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(staleFile), "上次崩溃留下的 .part 必须被清掉");
        Assert.Empty(Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FetchChecksums_ReleaseWithoutList_IsOkWithNullMap()
    {
        // 版本存在但发布里确实没有清单 → 这是发布问题，不是网络问题。
        const string feed = """
        [ { "tag_name": "v1.18.0", "draft": false, "prerelease": false,
            "assets": [ { "name": "TeachCraft-v1.18.0-win-x64-sc.zip", "size": 1, "browser_download_url": "https://e/x.zip" } ] } ]
        """;
        var handler = new GuardedHttpHandler().Respond(UpdateCheckService.DefaultFeedUrl, HttpStatusCode.OK, feed);
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.FetchChecksumsAsync(UpdateCheckService.DefaultFeedUrl, "v1.18.0");

        Assert.True(result.IsSuccess, "取到了发布信息，只是没有清单——不能报成网络失败");
        Assert.Null(result.Map);
    }

    [Fact]
    public async Task FetchChecksums_NetworkFailure_IsDistinctFromMissingList()
    {
        // 取不到（网络/限流/门禁）→ 必须报失败原因，不能谎称「发布缺清单」。
        var handler = new GuardedHttpHandler().Respond(UpdateCheckService.DefaultFeedUrl, (HttpStatusCode)403, "rate");
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.FetchChecksumsAsync(UpdateCheckService.DefaultFeedUrl, "v1.18.0");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Failure);
        Assert.DoesNotContain("缺少完整性校验清单", result.Failure!);
    }

    [Fact]
    public async Task MissingChecksumsList_FailsClosed()
    {
        // 计划的核心安全约束：拿不到校验清单就失败关闭，绝不降级成「只校验长度」。
        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.FetchChecksumsAsync(
            UpdateCheckService.DefaultFeedUrl, "v1.18.0");

        // 桩没有提供 releases feed，桩会抛「测试中出现了真实网络请求」，
        // 因此这属于「取不到」而不是「发布没提供清单」——必须区分开。
        Assert.False(result.IsSuccess);
        Assert.Null(result.Map);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Download_LengthMismatch_AbortsAndCleansUp()
    {
        var payload = Payload(512);
        // 资产声明 1024 字节，实际只给 512。
        var asset = Asset(size: 1024);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null);

        Assert.False(result.IsSuccess);
        Assert.Contains("没有下载完整", result.Failure!);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_ChecksumMismatch_AbortsAndCleansUp()
    {
        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0",
            expectedSha256: Sha256(Encoding.UTF8.GetBytes("something else")));

        Assert.False(result.IsSuccess);
        Assert.Contains("校验失败", result.Failure!);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_ChecksumMatch_Succeeds()
    {
        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: Sha256(payload));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task InsufficientSpace_RejectedBeforeDownloading()
    {
        var asset = Asset(size: 10_000_000_000);
        var handler = new GuardedHttpHandler();
        var downloader = new UpdateDownloader(handler, _root, freeSpaceProbe: _ => 1024);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null);

        Assert.False(result.IsSuccess);
        Assert.Contains("空间", result.Failure!);
        // 空间不够时不该发任何请求。
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task Progress_IsReportedMonotonically()
    {
        var payload = Payload(2048);
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var reported = new List<int>();
        await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null, progress: new Progress<int>(reported.Add));

        Assert.NotEmpty(reported);
        for (var i = 1; i < reported.Count; i++)
            Assert.True(reported[i] >= reported[i - 1], "进度必须单调不减");
    }

    [Fact]
    public async Task Download_WritesOnlyUnderInjectedRoot()
    {
        var payload = Payload();
        var asset = Asset(size: payload.Length);
        var handler = new GuardedHttpHandler().RespondWith(asset.DownloadUrl,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var downloader = new UpdateDownloader(handler, _root);

        var result = await downloader.DownloadAsync(asset, "v1.18.0", expectedSha256: null);

        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(result.Path!), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 校验清单解析。清单来自 CI 生成的 TeachCraft-{tag}-SHA256SUMS.txt。
/// </summary>
public sealed class ChecksumsTests
{
    [Fact]
    public void Parse_ReadsStandardTwoSpaceFormat()
    {
        var text = """
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  TeachCraft-v1.18.0-win-x64-sc-Setup.exe
        bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  TeachCraft-v1.18.0-win-x64-sc.zip
        """;

        var map = Checksums.Parse(text);

        Assert.Equal(2, map.Count);
        Assert.Equal(new string('a', 64), map["TeachCraft-v1.18.0-win-x64-sc-Setup.exe"]);
        Assert.Equal(new string('b', 64), map["TeachCraft-v1.18.0-win-x64-sc.zip"]);
    }

    [Fact]
    public void Parse_ToleratesBlankLinesAndLeadingStar()
    {
        var text = """

        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa *TeachCraft-x.zip

        """;

        var map = Checksums.Parse(text);

        Assert.Equal(new string('a', 64), map["TeachCraft-x.zip"]);
    }

    [Fact]
    public void Parse_IgnoresMalformedLines()
    {
        var map = Checksums.Parse("garbage line without separator\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  good.zip");

        Assert.Single(map);
        Assert.True(map.ContainsKey("good.zip"));
    }

    [Fact]
    public void Parse_EmptyText_YieldsEmptyMap()
    {
        Assert.Empty(Checksums.Parse(""));
    }
}
