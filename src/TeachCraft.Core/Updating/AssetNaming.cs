namespace TeachCraft.Core.Updating;

/// <summary>
/// 安装形态。七种，全项目只在这里定义，别处不要再写一套。
/// </summary>
public enum InstallKind
{
    /// <summary>用 Inno 安装包装的，且写入了身份记录（能知道 sc/fdd）。</summary>
    InnoInstall,

    /// <summary>用 Inno 安装包装的旧版本——没有身份记录，只能从安装路径推断。</summary>
    InnoInstallLegacy,

    /// <summary>绿色包解压出来的。</summary>
    PortableZip,

    /// <summary>macOS 上放在应用程序文件夹里的 .app。</summary>
    MacBundle,

    /// <summary>macOS 上从 zip 解压、放在任意位置的 .app。</summary>
    MacBundlePortable,

    /// <summary>Linux 绿色包。</summary>
    LinuxPortable,

    /// <summary>认不出来，任何自动动作都要先让用户确认。</summary>
    Unknown,
}

/// <summary>
/// 发布资产的命名规则。所有拼接/解析都必须走这里，避免两处各写一套。
/// </summary>
public static class AssetNaming
{
    /// <summary>macOS 应用压缩包后缀。macOS 的 .app zip 与 Windows/Linux 的 zip 天然同名，
    /// 加这个后缀才能按名字区分，否则 Windows 用户可能被推一个 Mac 包。</summary>
    public const string MacZipSuffix = "-mac.zip";

    public const string SetupSuffix = "-Setup.exe";
    public const string DmgSuffix = ".dmg";
    public const string ZipSuffix = ".zip";
    public const string ChecksumsSuffix = "-SHA256SUMS.txt";

    public static string ChecksumsName(string tag) => $"TeachCraft-{tag}{ChecksumsSuffix}";

    /// <summary>判断一个资产名是否属于 rid+mode 这一支。</summary>
    public static bool MatchesRidAndMode(string assetName, string rid, string mode)
        => assetName.Contains($"-{rid}-{mode}", StringComparison.OrdinalIgnoreCase);
}
