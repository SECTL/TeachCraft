using System.Text.Json;

namespace TeachCraft.Core.Updating;

/// <summary>
/// 更新源。两处可选：官方 GitHub Releases，与 SECTL 软件分发镜像。
///
/// 为什么需要镜像：GitHub 在部分校园网 / 教育网里连不上或极慢，
/// 而「检查更新」是本应用唯一的出站动作——它不通，用户就永远卡在旧版。
/// 镜像由 SECTL 托管同一批发布资产（哈希一致，已验证），把这条路补上。
/// </summary>
public enum UpdateSource
{
    /// <summary>GitHub Releases（api.github.com + github.com 下载）。</summary>
    GitHub,

    /// <summary>SECTL 软件分发镜像（appwrite.sectl.cn/api/software + stk.sectl.cn 下载）。</summary>
    SectlMirror,
}

public static class UpdateSourceText
{
    public static string DisplayName(UpdateSource source) => source switch
    {
        UpdateSource.SectlMirror => "SECTL 镜像",
        _ => "GitHub",
    };

    /// <summary>把持久化的字符串解析回枚举；认不出时回退到 GitHub。</summary>
    public static UpdateSource Parse(string? value) => value switch
    {
        "sectl" => UpdateSource.SectlMirror,
        _ => UpdateSource.GitHub,
    };

    /// <summary>写进状态文件的稳定标识，不随界面文案变化。</summary>
    public static string ToStorageValue(UpdateSource source) => source switch
    {
        UpdateSource.SectlMirror => "sectl",
        _ => "github",
    };
}

/// <summary>
/// 解析 SECTL 软件分发 API 的 `/distribution` 响应，归一化成与 GitHub 相同的 <see cref="ReleaseEntry"/>。
///
/// 为什么要归一化：下游（UpdateFeed.SelectCandidates / UpdateAssetSelector / UpdatePlanner）
/// 全都按 ReleaseEntry 工作。镜像若另写一套选择逻辑，两边的「哪个版本更新」「哪支资产对」
/// 迟早会分叉——这正是本项目反复踩过的坑。所以镜像只负责「取数据 + 换地址」，判断一律复用。
///
/// 与 GitHub 的字段对应：
///   versions[].tag        → ReleaseEntry.TagName
///   versions[].prerelease → ReleaseEntry.IsPrerelease
///   packages[].file_name  → ReleaseAsset.Name
///   packages[].size       → ReleaseAsset.Size
///   packages[].server_download_url → ReleaseAsset.DownloadUrl（镜像地址）
///
/// 镜像不提供 sha256 字段（实测全为空），所以校验清单要另取——见 <see cref="ParseChecksums"/>。
/// </summary>
public static class SectlMirrorFeed
{
    /// <summary>镜像 API 基址。末尾不带斜杠，拼接时统一补。</summary>
    public const string DefaultApiBase = "https://appwrite.sectl.cn/api/software";

    /// <summary>镜像项目名。带中文与空格，请求时必须 URL 编码。</summary>
    public const string DefaultProjectSlug = "知微 · TeachCraft";

    /// <summary>
    /// 从 `/distribution` 响应里挑出本项目、本版本可用的条目。
    ///
    /// 只保留 `available_sources` 含 "server" 的包——那是镜像真正托管了文件的；
    /// 只有 github/direct 的包在镜像上下载不到，留着会让用户点了下载却失败。
    /// </summary>
    public static IReadOnlyList<ReleaseEntry> Parse(string json, string projectSlug)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<ReleaseEntry>();

        if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
            return Array.Empty<ReleaseEntry>();

        // 按 slug 找项目；找不到就不返回任何版本（调用方据此报「镜像上还没有这个软件」）。
        string? projectId = null;
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object) continue;
            var slug = project.TryGetProperty("slug", out var s) ? s.GetString() : null;
            if (!string.Equals(slug, projectSlug, StringComparison.Ordinal)) continue;

            projectId = project.TryGetProperty("$id", out var id) ? id.GetString() : null;
            break;
        }

        if (string.IsNullOrEmpty(projectId)) return Array.Empty<ReleaseEntry>();

        // 版本表：只保留启用的，且必须能解析成语义版本。
        var versions = new Dictionary<string, (SemanticVersion Version, bool Prerelease)>(StringComparer.Ordinal);
        if (root.TryGetProperty("versions", out var versionArray) && versionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in versionArray.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                if (v.TryGetProperty("project_id", out var pid) && pid.GetString() != projectId) continue;

                var tag = v.TryGetProperty("tag", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag)) continue;

                // 镜像上的「停用版本」不该出现在更新候选里。
                if (v.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                    continue;

                if (!SemanticVersion.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;

                var prerelease = v.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
                versions[tag] = (version, prerelease);
            }
        }

        if (versions.Count == 0) return Array.Empty<ReleaseEntry>();

        // 资产按版本分组。
        var assets = new Dictionary<string, List<ReleaseAsset>>(StringComparer.Ordinal);
        if (root.TryGetProperty("packages", out var packageArray) && packageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in packageArray.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (p.TryGetProperty("project_id", out var pid) && pid.GetString() != projectId) continue;

                var tag = p.TryGetProperty("version_tag", out var vt) ? vt.GetString() : null;
                if (tag is null || !versions.ContainsKey(tag)) continue;

                // 镜像没托管文件的包跳过——下载地址会指向 GitHub，在镜像语境下不可靠。
                if (!HasServerSource(p)) continue;

                var name = p.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var url = p.TryGetProperty("server_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var size = p.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sizeValue) ? sizeValue : 0L;

                if (!assets.TryGetValue(tag, out var list))
                {
                    list = new List<ReleaseAsset>();
                    assets[tag] = list;
                }

                list.Add(new ReleaseAsset(name, size, url));
            }
        }

        // 保留「有版本记录但包还没同步」的条目——镜像同步是分步的：
        // 版本先登记，包后到位（实测 v1.18.0 发布后 versions 表已有记录，
        // packages 表仍是空的）。
        //
        // 不能在这里静默丢掉它们：上游 SelectCandidates 会因此得出「没有新版」，
        // 用户选了镜像就会被告知「已是最新」，而实际上有新版只是还没同步完。
        // 让它们带着空资产列表上去，由 UpdatePlanner 如实报告「镜像还没同步好」。
        return versions
            .Select(kv => new ReleaseEntry(
                kv.Key,
                kv.Value.Version,
                kv.Value.Prerelease,
                assets.TryGetValue(kv.Key, out var list) ? list : new List<ReleaseAsset>()))
            .ToList();
    }

    private static bool HasServerSource(JsonElement package)
    {
        if (!package.TryGetProperty("available_sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            // 老记录可能没有这个字段；有 server_download_url 就当作可用。
            return package.TryGetProperty("server_download_url", out var u)
                   && !string.IsNullOrWhiteSpace(u.GetString());
        }

        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind == JsonValueKind.String
                && string.Equals(source.GetString(), "server", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>拼 `/distribution` 请求地址。</summary>
    public static string BuildDistributionUrl(string apiBase = DefaultApiBase)
        => $"{apiBase.TrimEnd('/')}/distribution";

    /// <summary>
    /// 从镜像的 SHA256SUMS 资产里找清单地址。镜像 packages 的 sha256 字段实测为空，
    /// 校验只能靠 CI 生成的那份清单文件——它同样被镜像了。
    /// </summary>
    public static string? FindChecksumsUrl(IReadOnlyList<ReleaseAsset> assets)
        => assets.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.ChecksumsSuffix, StringComparison.OrdinalIgnoreCase))?.DownloadUrl;
}
