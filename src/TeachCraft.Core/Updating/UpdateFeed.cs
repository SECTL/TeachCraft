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
    /// 给定当前安装的构建，返回「比它更高」且用户本该看到的候选版本，从新到旧。
    /// 正式版安装：只看正式版。
    /// 预览版安装：看更高的预览 + 更高的正式版。
    /// </summary>
    public static IReadOnlyList<ReleaseEntry> SelectCandidates(IEnumerable<ReleaseEntry> entries, BuildIdentity current)
    {
        var currentVersion = current.Version;

        var candidates = entries.Where(e =>
        {
            if (e.Version <= currentVersion) return false;
            if (!current.Version.IsPreview) return !e.IsPrerelease;
            return true;
        });

        return OrderNewestFirst(candidates).ToList();
    }

    /// <summary>最高候选；没有可升级的版本时返回 null。</summary>
    public static ReleaseEntry? BestCandidate(IEnumerable<ReleaseEntry> entries, BuildIdentity current)
        => SelectCandidates(entries, current).FirstOrDefault();
}
