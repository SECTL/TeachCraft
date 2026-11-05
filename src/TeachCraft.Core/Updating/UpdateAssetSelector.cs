namespace TeachCraft.Core.Updating;

/// <summary>
/// 按（构建身份，安装形态）挑出唯一该下载的资产。
///
/// 优先级：安装包 &gt; 磁盘镜像 &gt; 压缩包。
/// 匹配的是资产名里的 (rid, mode) 两段，不靠后缀猜平台——
/// macOS 的 .app zip 与 Windows/Linux 的 .zip 曾经完全同名，靠后缀猜必然选错。
/// 认不出或找不到时返回 null，由调用方给出人话解释，而不是抛异常。
/// </summary>
public static class UpdateAssetSelector
{
    public static ReleaseAsset? Select(
        IEnumerable<ReleaseAsset> assets,
        BuildIdentity current,
        InstallKind kind)
    {
        var rid = current.Rid;
        if (string.IsNullOrEmpty(rid)) return null;

        // legacy 身份拿不到 mode：保守选 sc（自带运行时，不依赖目标机器装没装 .NET）。
        var mode = current.Mode;
        if (mode is null)
        {
            if (kind is not (InstallKind.InnoInstallLegacy or InstallKind.PortableZip or InstallKind.LinuxPortable))
                return null;
            mode = "sc";
        }

        var pool = assets
            .Where(a => AssetNaming.MatchesRidAndMode(a.Name, rid, mode))
            .Where(a => !IsNonPackage(a.Name))
            .ToList();

        return kind switch
        {
            InstallKind.InnoInstall or InstallKind.InnoInstallLegacy
                => pool.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.SetupSuffix, StringComparison.OrdinalIgnoreCase)),

            InstallKind.MacBundle
                => pool.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.DmgSuffix, StringComparison.OrdinalIgnoreCase))
                   ?? pool.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.MacZipSuffix, StringComparison.OrdinalIgnoreCase)),

            // 从 zip 启动的 Mac 用户：只有 -mac.zip 是他能直接替换的形态。
            InstallKind.MacBundlePortable
                => pool.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.MacZipSuffix, StringComparison.OrdinalIgnoreCase)),

            InstallKind.PortableZip
                => pool.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.ZipSuffix, StringComparison.OrdinalIgnoreCase)
                                            && !a.Name.EndsWith(AssetNaming.MacZipSuffix, StringComparison.OrdinalIgnoreCase)),

            InstallKind.LinuxPortable
                => pool.FirstOrDefault(a => a.Name.EndsWith(AssetNaming.ZipSuffix, StringComparison.OrdinalIgnoreCase)),

            _ => null,
        };
    }

    /// <summary>legacy 身份（老安装包）无法判断 sc/fdd，界面需要说明「已为你选独立版」。</summary>
    /// <summary>
    /// 老安装包（哪些是 legacy）无法自证部署模式，需要说明「已为你选独立版」。
    ///
    /// 注意不能用 <c>Mode is null</c> 判断：走查/兜底路径会先补上 Mode，
    /// 那正是最需要解释的场景，却会被判成「不需要解释」。
    /// </summary>
    public static bool NeedsModeExplanation(BuildIdentity current)
        => current.IsLegacy;

    private static bool IsNonPackage(string name)
        => name.EndsWith(AssetNaming.ChecksumsSuffix, StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);
}
