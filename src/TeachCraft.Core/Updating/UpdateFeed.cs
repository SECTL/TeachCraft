using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeachCraft.Core.Updating;

/// <summary>发布资产（安装包 / 压缩包 / 镜像）的最小信息。</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl);

/// <summary>一条 GitHub Release 的最小信息。</summary>
public sealed record ReleaseEntry(
    string TagName,
    SemanticVersion Version,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// 把 GitHub Releases API 的 JSON 归一化为可比较的条目。
///
/// 两条硬规则：
///   1. 排序键是语义版本，绝不使用 published_at —— 本仓库历史 tag 是非连续的
///      （v1.10.2-preview.8 与 v1.2.1-preview.5 并存），按时间排会给出错误顺序。
///   2. draft 一律排除；正式版用户只看正式版，预览版用户看「更高的预览 + 更高的正式版」。
/// </summary>
public static class UpdateFeed
{
    public static IEnumerable<ReleaseEntry> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            // draft 的版本对用户不可见，直接跳过。
            if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

            if (!element.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag)) continue;

            // 不认得的 tag（例如手工打过的不规范标签）跳过，不要抛异常整条链路崩掉。
            if (!SemanticVersion.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;

            var isPrerelease = element.TryGetProperty("prerelease", out var pre)
                               && pre.ValueKind == JsonValueKind.True;

            var assets = new List<ReleaseAsset>();
            if (element.TryGetProperty("assets", out var assetsElement)
                && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sizeValue)
                        ? sizeValue
                        : 0L;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

                    assets.Add(new ReleaseAsset(name, size, url));
                }
            }

            yield return new ReleaseEntry(tag, version, isPrerelease, assets);
        }
    }

    /// <summary>按版本从新到旧排序；同版本时正式版排在预览版之前。</summary>
    public static IEnumerable<ReleaseEntry> OrderNewestFirst(IEnumerable<ReleaseEntry> entries)
        => entries.OrderByDescending(e => e.Version);

    /// <summary>
    /// 给定当前安装的构建与用户选定的通道，返回可选版本，从新到旧。
    ///
    /// 稳定通道：只给正式版。
    ///   正式版用户 → 严格更高的正式版。
    ///   预览版用户 → **不要求更高**。他切到稳定通道就是要回到正式版，
    ///   而正式版（1.18.0）的版本号本来就低于同号预览（1.18.0-preview.15），
    ///   按「必须更高」筛会把唯一该给他的版本筛掉。
    ///
    /// 预览通道：预览版与正式版都算，取版本号最高的。
    ///   正式版用户 → 更高即可（同号预览低于正式版，自然被排除）。
    ///   预览版用户 → 更高的预览，或更高的正式版。
    /// </summary>
    public static IReadOnlyList<ReleaseEntry> SelectCandidates(
        IEnumerable<ReleaseEntry> entries,
        BuildIdentity current,
        UpdateChannel channel = UpdateChannel.Stable)
    {
        var currentVersion = current.Version;
        var all = entries.ToList();

        if (channel == UpdateChannel.Stable)
        {
            var stable = all.Where(e => !e.IsPrerelease).ToList();

            // 预览版用户切稳定通道：允许降级，但**只给最高的那个正式版**。
            // 把所有更低的正式版都列出来，用户会看到「1.18.0 和 1.17.0 都能选」——
            // 那不是选择，是噪音；他要的是「回到正式版」，不是版本考古。
            if (currentVersion.IsPreview)
            {
                var best = OrderNewestFirst(stable).FirstOrDefault();
                return best is null ? Array.Empty<ReleaseEntry>() : new[] { best };
            }

            return OrderNewestFirst(stable.Where(e => e.Version > currentVersion)).ToList();
        }

        // 预览通道：预览与正式都算，取严格更高的。
        return OrderNewestFirst(all.Where(e => e.Version > currentVersion)).ToList();
    }

    /// <summary>最高候选；没有可升级的版本时返回 null。</summary>
    public static ReleaseEntry? BestCandidate(
        IEnumerable<ReleaseEntry> entries,
        BuildIdentity current,
        UpdateChannel channel = UpdateChannel.Stable)
        => SelectCandidates(entries, current, channel).FirstOrDefault();
}
