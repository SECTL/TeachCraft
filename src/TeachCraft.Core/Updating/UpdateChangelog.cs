using System.Reflection;
using System.Text.RegularExpressions;

namespace TeachCraft.Core.Updating;

public sealed record ChangelogSection(string? Heading, IReadOnlyList<string> Items);

public sealed record ChangelogRelease(
    SemanticVersion Version,
    string TagName,
    string? DateText,
    IReadOnlyList<ChangelogSection> Sections);

public sealed record UpdateChangelogDocument(
    SemanticVersion FromVersion,
    SemanticVersion TargetVersion,
    string Headline,
    string Subtitle,
    IReadOnlyList<ChangelogRelease> Releases,
    bool IsFallbackSummary);

public static class EmbeddedChangelogProvider
{
    public const string ResourceName = "TeachCraft.Core.CHANGELOG.md";

    public static string? ReadOrNull(Assembly? assembly = null)
    {
        var source = assembly ?? typeof(EmbeddedChangelogProvider).Assembly;
        using var stream = source.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public static partial class ChangelogParser
{
    private const string CiBoilerplatePrefix = "## 知微 · TeachCraft 全矩阵跨平台构建";

    /// <summary>
    /// 解析仓库内置的 CHANGELOG.md。识别 `## &lt;version&gt;[ · &lt;date&gt;]`、`### &lt;小节&gt;`
    /// 与 `- / *` 列表项，返回按语义版本从新到旧排列的条目。
    /// </summary>
    public static IReadOnlyList<ChangelogRelease> ParseDocument(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Array.Empty<ChangelogRelease>();

        var releases = new List<ChangelogRelease>();

        SemanticVersion? currentVersion = null;
        string? currentTag = null;
        string? currentDate = null;
        var currentSections = new List<SectionBuilder>();
        SectionBuilder? activeSection = null;

        void FlushRelease()
        {
            if (currentVersion is null) return;
            var builtSections = currentSections
                .Where(s => s.Items.Count > 0)
                .Select(s => new ChangelogSection(s.Heading, s.Items.AsReadOnly()))
                .ToList();
            releases.Add(new ChangelogRelease(
                currentVersion.Value,
                currentTag ?? $"v{currentVersion.Value}",
                currentDate,
                builtSections));
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                if (TryParseReleaseHeader(trimmed[3..], out var parsedVersion, out var dateText))
                {
                    FlushRelease();
                    currentVersion = parsedVersion;
                    currentTag = $"v{parsedVersion}";
                    currentDate = dateText;
                    currentSections = new List<SectionBuilder>();
                    activeSection = null;
                    continue;
                }
            }

            if (currentVersion is null) continue;

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = CleanInlineMarkdown(trimmed[4..].Trim());
                activeSection = new SectionBuilder(string.IsNullOrWhiteSpace(heading) ? null : heading);
                currentSections.Add(activeSection);
                continue;
            }

            if (IsBulletLine(trimmed, out var bulletText))
            {
                if (activeSection is null)
                {
                    activeSection = new SectionBuilder(null);
                    currentSections.Add(activeSection);
                }
                var cleaned = CleanInlineMarkdown(bulletText);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    activeSection.Items.Add(cleaned);
                continue;
            }

            // 缩进续行并入上一条要点，避免折行被拆成两条或丢掉。
            if ((rawLine.StartsWith("  ", StringComparison.Ordinal) || rawLine.StartsWith("\t", StringComparison.Ordinal))
                && activeSection is { Items.Count: > 0 })
            {
                var continuation = CleanInlineMarkdown(trimmed);
                if (!string.IsNullOrWhiteSpace(continuation))
                    activeSection.Items[^1] = $"{activeSection.Items[^1]} {continuation}";
            }
        }

        FlushRelease();
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>
    /// 把上游 Release 说明（GitHub body / 镜像 notes）解析为单个版本的展示条目。
    /// 若内容为空或只是 CI 自动生成的通用资产模板，则返回 null。
    /// </summary>
    public static ChangelogRelease? ParseReleaseNotes(
        SemanticVersion version,
        string? tagName,
        string? rawNotes)
    {
        if (IsBoilerplateReleaseBody(rawNotes)) return null;

        var sections = new List<SectionBuilder>();
        SectionBuilder? active = null;

        foreach (var rawLine in rawNotes!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("### ", StringComparison.Ordinal) ||
                trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var headingText = CleanInlineMarkdown(trimmed.TrimStart('#').Trim());
                if (!string.IsNullOrWhiteSpace(headingText))
                {
                    active = new SectionBuilder(headingText);
                    sections.Add(active);
                }
                continue;
            }

            if (IsBulletLine(trimmed, out var bulletText))
            {
                if (active is null)
                {
                    active = new SectionBuilder(null);
                    sections.Add(active);
                }
                var cleaned = CleanInlineMarkdown(bulletText);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    active.Items.Add(cleaned);
                continue;
            }

            if (active is null)
            {
                active = new SectionBuilder(null);
                sections.Add(active);
            }
            var plain = CleanInlineMarkdown(trimmed);
            if (!string.IsNullOrWhiteSpace(plain))
                active.Items.Add(plain);
        }

        var built = sections
            .Where(s => s.Items.Count > 0)
            .Select(s => new ChangelogSection(s.Heading, s.Items.AsReadOnly()))
            .ToList();

        if (built.Count == 0) return null;

        return new ChangelogRelease(
            version,
            string.IsNullOrWhiteSpace(tagName) ? $"v{version}" : tagName.Trim(),
            null,
            built);
    }

    public static bool IsBoilerplateReleaseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return true;
        return body.TrimStart().StartsWith(CiBoilerplatePrefix, StringComparison.Ordinal);
    }

    internal static bool TryParseReleaseHeader(string header, out SemanticVersion version, out string? dateText)
    {
        version = default;
        dateText = null;

        var cleaned = header.Trim();
        // 支持 `1.45.0 · 2026-11-25`、`v1.45.0 - 2026-11-25`、`[1.45.0] (2026-11-25)`。
        var match = ReleaseHeaderRegex().Match(cleaned);
        if (!match.Success) return false;

        var rawVersion = match.Groups["ver"].Value.TrimStart('v', 'V');
        if (!SemanticVersion.TryParse(rawVersion, out version)) return false;

        var rest = match.Groups["rest"].Value.Trim(' ', '·', '-', '—', '(', ')', '[', ']');
        dateText = string.IsNullOrWhiteSpace(rest) ? null : rest;
        return true;
    }

    private static bool IsBulletLine(string trimmed, out string content)
    {
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal) ||
            trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            content = trimmed[2..].Trim();
            return true;
        }
        content = string.Empty;
        return false;
    }

    private static string CleanInlineMarkdown(string text)
    {
        // Plain TextBlock 不渲染 Markdown 语法：去掉成对 ** / ` 标记，保留文字本身。
        var result = BoldRegex().Replace(text, "$1");
        result = InlineCodeRegex().Replace(result, "$1");
        return result.Trim();
    }

    private sealed class SectionBuilder
    {
        public SectionBuilder(string? heading) => Heading = heading;
        public string? Heading { get; }
        public List<string> Items { get; } = new();
    }

    [GeneratedRegex(@"^\[?(?<ver>v?\d+\.\d+\.\d+(?:-preview\.\d+)?)\]?(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseHeaderRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();
}

/// <summary>
/// 启动时判定是否需要弹出一次更新日志，并拼装要展示的版本段落。
/// 规则：
///   1. 只有存在 pending-changelog 标记且本次启动真正切换到了目标版本才弹窗；
///   2. 消费即清除标记，确保最多只弹一次；
///   3. 绿色版自替换明确失败、安装取消（版本未变）或跨出目标版本的外部安装一律不弹。
/// </summary>
public static class UpdateChangelogPlanner
{
    public static UpdateChangelogDocument? ResolveOnStartup(
        IPendingChangelogStore store,
        SemanticVersion currentVersion,
        bool portableReplaceFailed,
        string? embeddedChangelogMarkdown)
    {
        ArgumentNullException.ThrowIfNull(store);

        var pending = store.Load();
        if (pending is null) return null;

        // 只要读到标记就立即清除：不论后续成功判定还是失败拦截，同一条记录绝不二次触发。
        store.Clear();

        if (portableReplaceFailed) return null;

        if (!SemanticVersion.TryParse(pending.FromVersion, out var fromVersion) ||
            !SemanticVersion.TryParse(pending.TargetVersion, out var targetVersion))
        {
            return null;
        }

        // 版本没变（典型：用户取消安装或安装包失败重开了旧版）或不是这次目标版本，都不算升级成功。
        if (currentVersion.Equals(fromVersion) || !currentVersion.Equals(targetVersion))
            return null;

        var embedded = ChangelogParser.ParseDocument(embeddedChangelogMarkdown);
        var matchedReleases = SelectReleasesForUpgrade(embedded, fromVersion, targetVersion);

        var isFallback = false;
        if (matchedReleases.Count == 0)
        {
            if (ChangelogParser.ParseReleaseNotes(targetVersion, pending.TagName, pending.FeedNotes) is { } feedRelease)
            {
                matchedReleases = new[] { feedRelease };
            }
            else
            {
                isFallback = true;
                matchedReleases = new[]
                {
                    new ChangelogRelease(
                        targetVersion,
                        string.IsNullOrWhiteSpace(pending.TagName) ? $"v{targetVersion}" : pending.TagName,
                        null,
                        new[]
                        {
                            new ChangelogSection(null, new[]
                            {
                                $"已从 v{fromVersion} 升级至 v{targetVersion}，当前版本未附带详细更新说明。",
                            }),
                        }),
                };
            }
        }

        var headline = $"已更新至 v{targetVersion}";
        var subtitle = matchedReleases.Count > 1
            ? $"从 v{fromVersion} 升级完成 · 包含 {matchedReleases.Count} 个版本的更新"
            : $"从 v{fromVersion} 升级完成";

        return new UpdateChangelogDocument(
            fromVersion,
            targetVersion,
            headline,
            subtitle,
            matchedReleases,
            isFallback);
    }

    private static IReadOnlyList<ChangelogRelease> SelectReleasesForUpgrade(
        IReadOnlyList<ChangelogRelease> allReleases,
        SemanticVersion fromVersion,
        SemanticVersion targetVersion)
    {
        if (allReleases.Count == 0) return Array.Empty<ChangelogRelease>();

        if (targetVersion > fromVersion)
        {
            return allReleases
                .Where(r => r.Version > fromVersion && r.Version <= targetVersion)
                .OrderByDescending(r => r.Version)
                .ToList();
        }

        // 例如预览通道切回正式版时版本号可能低于原预览：若内置日志恰好有目标版本，只展示目标版本本身。
        return allReleases
            .Where(r => r.Version.Equals(targetVersion))
            .ToList();
    }
}
