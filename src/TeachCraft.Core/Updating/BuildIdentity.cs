namespace TeachCraft.Core.Updating;

/// <summary>
/// 可自证的构建身份。
///
/// 身份串由 CI 通过 <c>-p:InformationalVersion</c> 注入，形如：
/// <c>{主}.{次}.{修}[-preview.{N}].{sc|fdd}.{rid}</c>，例如 <c>1.17.0-preview.142.sc.win-x64</c>。
///
/// 解析规则：先剥掉 SDK 可能追加的 git sha 尾巴（<c>+sha</c>），
/// 再从尾部依次剥 <c>rid</c> 与 <c>sc|fdd</c>，剩下的必须是合法语义版本。
/// 任何一步失败都退回 legacy 形态，绝不猜一个看起来合理的值——
/// 认错身份的后果是给用户推错安装包。
/// </summary>
public sealed class BuildIdentity
{
    private static readonly string[] KnownRids =
    {
        "win-x64", "win-x86", "win-arm64",
        "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64",
    };

    private static readonly string[] KnownModes = { "sc", "fdd" };

    private BuildIdentity(SemanticVersion version, string? mode, string? rid, bool isLegacy)
    {
        Version = version;
        Mode = mode;
        Rid = rid;
        IsLegacy = isLegacy;
    }

    public SemanticVersion Version { get; }

    /// <summary>部署模式：sc（自包含）或 fdd（依赖框架）；legacy 身份为 null。</summary>
    public string? Mode { get; }

    /// <summary>运行标识，如 win-x64；legacy 身份为 null。</summary>
    public string? Rid { get; }

    /// <summary>true 表示这是本次改造之前构建的产物，无法自证身份。</summary>
    public bool IsLegacy { get; }

    /// <summary>由 Rid 推出的平台前缀：win / osx / linux；legacy 身份为 null。</summary>
    public string? Platform => Rid is null ? null : Rid.Split('-')[0];

    public static BuildIdentity Parse(string? informationalVersion, string? fallbackAssemblyVersion = null)
    {
        var text = StripBuildMetadata(informationalVersion);

        if (text is not null)
        {
            var segments = text.Split('.');
            // 至少要有 主.次.修 + mode + rid 五段才可能是完整身份。
            if (segments.Length >= 5)
            {
                var rid = string.Join('.', segments[^1..]);
                var mode = segments[^2];
                var versionPart = string.Join('.', segments[..^2]);

                if (KnownRids.Contains(rid) && KnownModes.Contains(mode)
                    && SemanticVersion.TryParse(versionPart, out var version))
                {
                    return new BuildIdentity(version, mode, rid, isLegacy: false);
                }
            }

            // 没有 mode/rid 尾巴但至少是个合法版本：当成「无身份的正式/预览构建」。
            if (SemanticVersion.TryParse(text, out var bare))
            {
                return new BuildIdentity(DeriveLegacyVersion(bare, fallbackAssemblyVersion), null, null, isLegacy: true);
            }
        }

        // AssemblyVersion 是四段（1.16.0.0），而语义版本只有三段，先裁到三段再解析。
        var fallback = SemanticVersion.TryParse(TrimToThreeParts(fallbackAssemblyVersion), out var fromAssembly)
            ? fromAssembly
            : SemanticVersion.Create(0, 0, 0, null);

        return new BuildIdentity(DeriveLegacyVersion(fallback, fallbackAssemblyVersion), null, null, isLegacy: true);
    }

    /// <summary>当前进程的身份。null 表示读不到（例如在测试宿主里运行）。</summary>
    public static BuildIdentity Current(Type assemblyAnchor)
    {
        var assembly = assemblyAnchor.Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        var assemblyVersion = assembly.GetName().Version?.ToString(3);
        return Parse(informational, assemblyVersion);
    }

    /// <summary>给用户看的身份描述，必须包含预览序号，否则两个预览看起来完全一样。</summary>
    public string Describe() => Version.ToString();

    /// <summary>
    /// 补上运行标识。用于「老安装包」这类认不出身份的产物：
    /// 版本与部署模式无法自证，但当前进程对应的 rid 是确定的。
    /// </summary>
    public BuildIdentity WithRid(string rid) => new(Version, Mode, rid, IsLegacy);

    /// <summary>补上部署模式。用于开发机构建（没有 CI 注入的身份串）与走查参数。</summary>
    public BuildIdentity WithMode(string mode) => new(Version, mode, Rid, IsLegacy);

    public override string ToString() => Describe();

    /// <summary>剥掉 SDK 追加的构建元数据（<c>+&lt;sha&gt;</c> 或 <c>.&lt;sha&gt;</c> 形式）。</summary>
    private static string? StripBuildMetadata(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var span = text.Trim();

        var plus = span.IndexOf('+');
        if (plus >= 0) span = span[..plus];

        // SDK 在身份串已含 '+' 时改用 '.' 追加 sha，且 sha 是长十六进制串；
        // 只在这一段明显是 7 位以上的十六进制时裁掉，避免误伤 win-x64 之类的真实段。
        var segments = span.Split('.');
        if (segments.Length > 0)
        {
            var last = segments[^1];
            if (last.Length >= 7 && last.All(char.IsAsciiHexDigit)
                && !KnownRids.Contains(last) && !KnownModes.Contains(last)
                && last.Any(char.IsAsciiLetter))
            {
                span = string.Join('.', segments[..^1]);
            }
        }

        return span.Length == 0 ? null : span;
    }

    /// <summary>把四段式的程序集版本（1.16.0.0）裁成语义版本的三段式（1.16.0）。</summary>
    private static string? TrimToThreeParts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('.');
        return parts.Length > 3 ? string.Join('.', parts[..3]) : text.Trim();
    }

    /// <summary>
    /// 无身份信息时的版本回退。
    ///
    /// 只有「版本本身完全解析不出来」（0.0.0）才退回 <c>-preview.0</c>——那是一个诚实的
    /// 「不知道是哪一支」标记。若解析出了合法的裸版本（如 <c>1.17.0</c>），
    /// 它就是**正式版版本号**，不能伪造成 <c>1.17.0-preview.0</c>：
    /// 那个号码不代表任何真实预览，而且无数本地构建会共享同一个号。
    /// </summary>
    private static SemanticVersion DeriveLegacyVersion(SemanticVersion parsed, string? fallbackAssemblyVersion)
    {
        if (parsed.Major == 0 && parsed.Minor == 0 && parsed.Patch == 0 && fallbackAssemblyVersion is not null)
        {
            if (SemanticVersion.TryParse(fallbackAssemblyVersion, out var fromAssembly))
                parsed = fromAssembly;
        }

        // 真的什么都没解析出来：保留 -preview.0 作为「未知」标记，免得排序时冒充正式版。
        if (parsed.Major == 0 && parsed.Minor == 0 && parsed.Patch == 0)
            return SemanticVersion.Create(0, 0, 0, 0);

        // 有合法版本号：按它本来的样子呈现（正式版就是正式版）。
        return SemanticVersion.Create(parsed.Major, parsed.Minor, parsed.Patch, parsed.Preview);
    }
}
