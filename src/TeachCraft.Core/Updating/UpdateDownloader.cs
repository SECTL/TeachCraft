using System.Security.Cryptography;

namespace TeachCraft.Core.Updating;

/// <summary>下载结果的终态。失败时一定给出中文原因，且不留下半截文件。</summary>
public sealed record DownloadResult(bool IsSuccess, string? Path, string? Failure)
{
    public static DownloadResult Ok(string path) => new(true, path, null);
    public static DownloadResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// 解析 CI 生成的 SHA256SUMS 清单。格式与 sha256sum 输出一致：<c>&lt;hex&gt;  &lt;filename&gt;</c>。
/// 兼容前导 '*'（二进制模式标记）与空行，无法解析的行直接跳过而不是抛异常。
/// </summary>
public static class Checksums
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var separator = line.IndexOf(' ');
            if (separator <= 0) continue;

            var hash = line[..separator].Trim();
            var name = line[(separator + 1)..].Trim().TrimStart('*');

            if (hash.Length == 0 || name.Length == 0) continue;
            // 哈希必须是十六进制；否则像 "garbage line without separator" 这种行
            // 会被误解析成「文件 line without separator」，让清单凭空多出条目。
            if (!IsHex(hash)) continue;
            map[name] = hash.ToLowerInvariant();
        }

        return map;
    }

    private static bool IsHex(string text)
    {
        if (text.Length < 32) return false;
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}

/// <summary>
/// 下载更新包。
///
/// 三个必须守住的点：
///   1. 只往 %LOCALAPPDATA%\TeachCraft\Update\downloads\ 写，绝不碰安装目录（IS-11）。
///   2. 先写 .part 再改名：进程被杀不能留下一个看起来完整的半截文件。
///   3. 校验不通过一律删除并失败，宁可让用户重来也不装一个可能损坏的包。
/// </summary>
public sealed class UpdateDownloader
{
    private readonly HttpClient _client;
    private readonly string _root;
    private readonly Func<string, long> _freeSpaceProbe;

    public UpdateDownloader(HttpMessageHandler handler, string? root = null, Func<string, long>? freeSpaceProbe = null)
        : this(new HttpClient(handler, disposeHandler: false), root, freeSpaceProbe)
    {
    }

    private UpdateDownloader(HttpClient client, string? root, Func<string, long>? freeSpaceProbe)
    {
        _client = client;
        _root = root ?? DefaultRoot();
        _freeSpaceProbe = freeSpaceProbe ?? ProbeFreeSpace;
    }

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeachCraft", "Update", "downloads");

    public async Task<DownloadResult> DownloadAsync(
        ReleaseAsset asset,
        string tag,
        string? expectedSha256,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        var targetDir = Path.Combine(_root, Sanitize(tag));

        // 上次若被强杀，会留下 .part；先扫干净再开始，避免与旧文件同名冲突。
        CleanupStaleParts();

        try
        {
            // 空间预检：留 1.5 倍余量，避免下载到一半磁盘满。
            var required = (long)(asset.Size * 1.5);
            if (asset.Size > 0 && _freeSpaceProbe(_root) < required)
                return DownloadResult.Fail($"磁盘剩余空间不足，本次下载约需要 {asset.Size / (1024 * 1024)} MB。");

            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(targetDir);

            var finalPath = Path.Combine(targetDir, Sanitize(asset.Name));
            var partPath = finalPath + ".part";

            using (var response = await _client.GetAsync(
                       asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    return DownloadResult.Fail(UpdateErrorText.ForStatus(response.StatusCode, "下载更新"));

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = File.Create(partPath);

                var buffer = new byte[81920];
                long total = 0;
                var lastReported = -1;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;

                    if (asset.Size > 0 && progress is not null)
                    {
                        var percent = (int)Math.Min(100, total * 100 / asset.Size);
                        if (percent != lastReported)
                        {
                            lastReported = percent;
                            progress.Report(percent);
                        }
                    }
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Close();

                // 长度不符：删掉半截文件，别让用户以为下好了。
                if (asset.Size > 0 && total != asset.Size)
                {
                    TryDelete(partPath);
                    return DownloadResult.Fail(UpdateErrorText.DownloadFailed);
                }

                if (expectedSha256 is not null)
                {
                    var actual = await ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(partPath);
                        return DownloadResult.Fail(UpdateErrorText.ChecksumMismatch);
                    }
                }
            }

            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(100);
            return DownloadResult.Ok(finalPath);
        }
        catch (OperationCanceledException)
        {
            Cleanup(targetDir);
            return DownloadResult.Fail("已取消下载。");
        }
        catch (Exception ex)
        {
            Cleanup(targetDir);
            return DownloadResult.Fail(UpdateErrorText.DescribeForUi(ex, "下载更新"));
        }
    }

    /// <summary>拉取发布页的校验清单。拿不到就返回 null —— 调用方据此失败关闭。</summary>
    /// <summary>
    /// 拉取发布页的校验清单。
    ///
    /// 返回值必须区分两种「没有清单」：
    ///   - <c>Ok(null)</c>：发布里确实没有清单文件 → 该版本本身有问题；
    ///   - <c>Fail(...)</c>：网络/认证/限流出错 → 与「发布缺清单」无关。
    /// 以前一律返回 null，会把「学校网络拦了」误报成「这个版本没提供校验清单」。
    /// </summary>
    public async Task<FetchResult> FetchChecksumsAsync(
        string releasesApiUrl,
        string tag,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _client.GetStringAsync(releasesApiUrl, cancellationToken).ConfigureAwait(false);
            var entry = UpdateFeed.Parse(json).FirstOrDefault(e => e.TagName == tag);
            if (entry is null)
                return FetchResult.Fail("在发布页上找不到这个版本，可能刚刚被清理，请稍后再试。");

            var sumsAsset = entry.Assets
                .FirstOrDefault(a => a.Name.EndsWith(AssetNaming.ChecksumsSuffix, StringComparison.OrdinalIgnoreCase));

            // 版本存在但没有清单：这是发布本身的问题。
            if (sumsAsset is null) return FetchResult.Ok(null);

            var body = await _client.GetStringAsync(sumsAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            var map = Checksums.Parse(body);
            return map.Count == 0 ? FetchResult.Ok(null) : FetchResult.Ok(map);
        }
        catch (Exception ex)
        {
            // 网络、TLS、限流、门禁页……都属于「取不到」，不是「没有」。
            return FetchResult.Fail(UpdateErrorText.DescribeForUi(ex, "下载更新"));
        }
    }

    /// <summary>取清单的结果：成功时 Map 可能为 null（发布确实没提供）。</summary>
    public sealed record FetchResult(bool IsSuccess, IReadOnlyDictionary<string, string>? Map, string? Failure)
    {
        public static FetchResult Ok(IReadOnlyDictionary<string, string>? map) => new(true, map, null);
        public static FetchResult Fail(string reason) => new(false, null, reason);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Cleanup(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            // 先清掉本次留下的半截文件，否则用户下次会看到一个「下载过」的假象。
            foreach (var part in Directory.GetFiles(directory, "*.part", SearchOption.AllDirectories))
                TryDelete(part);

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception)
        {
            // 清理失败不影响结论。
        }
    }

    /// <summary>
    /// 清理上次异常退出留下的半截文件。进程被杀时 <c>finally</c> 不会执行，
    /// 所以启动时（或每次下载前）必须主动扫一遍。
    /// </summary>
    /// <summary>
    /// 清理上次异常退出留下的半截文件。
    ///
    /// 只删**足够旧**的（默认 1 小时前）：下载根目录是用户级共享的，
    /// 无差别删除会误伤同一进程的并发下载、另一个实例正在下的文件，
    /// 或多用户/多副本共享该目录时的他人任务。
    /// </summary>
    public void CleanupStaleParts(TimeSpan? olderThan = null)
    {
        var threshold = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(1));

        try
        {
            if (!Directory.Exists(_root)) return;

            foreach (var part in Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(part) < threshold)
                        TryDelete(part);
                }
                catch (Exception)
                {
                    // 单个文件读不到时间就跳过，不影响其它文件。
                }
            }
        }
        catch (Exception)
        {
            // 扫不到就算了，不该因此拦住本次下载。
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    private static long ProbeFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // 探测不到就不拦，交给写文件时的异常兜底。
            return long.MaxValue;
        }
    }

    private static string Sanitize(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
