using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 资产挑选：用户永远不该自己挑包。
/// 最危险的错误是「同一平台选错部署模式」——
/// 把 fdd 用户升成 sc 会多下上百 MB，把 sc 用户升成 fdd 会让没有 .NET 的机器更新完打不开。
/// </summary>
public sealed class UpdateAssetSelectorTests
{
    private static readonly string[] Rids =
    {
        "win-x86", "win-x64", "win-arm64",
        "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64",
    };

    private static readonly string[] Modes = { "sc", "fdd" };

    /// <summary>
    /// 按真实命名规则拼出一次发布里应该出现的全部资产。
    /// 真实总数是 24：10 个 Windows/Linux zip + 4 个 macOS -mac.zip + 6 个 Setup + 4 个 dmg。
    /// （计划早先写的 28 是把「14 个 zip」与「额外 4 个 mac zip」重复计数了。）
    /// </summary>
    private static List<ReleaseAsset> FullAssetSet(string tag)
    {
        var assets = new List<ReleaseAsset>();

        // 10 个 Windows/Linux 绿色包 zip
        foreach (var rid in Rids.Where(r => !r.StartsWith("osx-")))
            foreach (var mode in Modes)
                assets.Add(new ReleaseAsset($"TeachCraft-{tag}-{rid}-{mode}.zip", 100_000_000, $"https://e/{rid}-{mode}.zip"));
        // 6 个 Windows 安装包
        foreach (var rid in Rids.Where(r => r.StartsWith("win-")))
            foreach (var mode in Modes)
                assets.Add(new ReleaseAsset($"TeachCraft-{tag}-{rid}-{mode}-Setup.exe", 120_000_000, $"https://e/{rid}-{mode}-Setup.exe"));

        // 4 个 macOS 镜像
        foreach (var rid in Rids.Where(r => r.StartsWith("osx-")))
            foreach (var mode in Modes)
                assets.Add(new ReleaseAsset($"TeachCraft-{tag}-{rid}-{mode}.dmg", 130_000_000, $"https://e/{rid}-{mode}.dmg"));

        // 4 个 macOS 应用压缩包（带 -mac 后缀，避免与 Windows/Linux zip 同名）
        foreach (var rid in Rids.Where(r => r.StartsWith("osx-")))
            foreach (var mode in Modes)
                assets.Add(new ReleaseAsset($"TeachCraft-{tag}-{rid}-{mode}{AssetNaming.MacZipSuffix}", 128_000_000, $"https://e/{rid}-{mode}-mac.zip"));

        return assets;
    }

    private static BuildIdentity Id(string text) => BuildIdentity.Parse(text);

    [Fact]
    public void FullAssetSet_Has24Entries()
    {
        var assets = FullAssetSet("v1.17.0");

        // 数字对不上就红：漏造 mac zip 会让「恰好一个」的断言假性通过。
        Assert.Equal(24, assets.Count);
    }

    [Fact]
    public void InnoInstall_PicksExactlyOneSetupExe()
    {
        var assets = FullAssetSet("v1.17.0");
        var current = Id("1.17.0.sc.win-x64");

        var picked = UpdateAssetSelector.Select(assets, current, InstallKind.InnoInstall);

        Assert.NotNull(picked);
        Assert.Equal("TeachCraft-v1.17.0-win-x64-sc-Setup.exe", picked!.Name);
        // 同一支只应有一个 Setup，避免名字前缀相近时误中别的架构。
        Assert.Single(assets, a => a.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)
                                   && a.Name.Contains("-win-x64-sc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InnoInstall_RespectsFddMode()
    {
        var assets = FullAssetSet("v1.17.0");
        var current = Id("1.17.0.fdd.win-x64");

        var picked = UpdateAssetSelector.Select(assets, current, InstallKind.InnoInstall);

        Assert.Equal("TeachCraft-v1.17.0-win-x64-fdd-Setup.exe", picked!.Name);
    }

    [Fact]
    public void MacBundle_PrefersDmgOverZip()
    {
        var assets = FullAssetSet("v1.17.0");
        var current = Id("1.17.0.fdd.osx-arm64");

        var picked = UpdateAssetSelector.Select(assets, current, InstallKind.MacBundle);

        Assert.Equal("TeachCraft-v1.17.0-osx-arm64-fdd.dmg", picked!.Name);
        Assert.DoesNotContain(".zip", picked.Name);
    }

    [Fact]
    public void MacBundlePortable_SelectsMacZipNotPlainZip()
    {
        var assets = FullAssetSet("v1.17.0");
        var current = Id("1.17.0.fdd.osx-arm64");

        var picked = UpdateAssetSelector.Select(assets, current, InstallKind.MacBundlePortable);

        Assert.Equal("TeachCraft-v1.17.0-osx-arm64-fdd-mac.zip", picked!.Name);
        Assert.EndsWith(AssetNaming.MacZipSuffix, picked.Name);
    }

    [Fact]
    public void PortableZip_RespectsMode_DoesNotSwapFddForSc()
    {
        var assets = FullAssetSet("v1.17.0");

        var fdd = UpdateAssetSelector.Select(assets, Id("1.17.0.fdd.win-x64"), InstallKind.PortableZip);
        var sc = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.win-x64"), InstallKind.PortableZip);

        Assert.Equal("TeachCraft-v1.17.0-win-x64-fdd.zip", fdd!.Name);
        Assert.Equal("TeachCraft-v1.17.0-win-x64-sc.zip", sc!.Name);
    }

    [Fact]
    public void LinuxPortable_SelectsLinuxZip()
    {
        var assets = FullAssetSet("v1.17.0");
        var picked = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.linux-x64"), InstallKind.LinuxPortable);

        Assert.Equal("TeachCraft-v1.17.0-linux-x64-sc.zip", picked!.Name);
    }

    [Fact]
    public void WindowsRid_IsNeverGivenAMacAsset()
    {
        var assets = FullAssetSet("v1.17.0");
        var picked = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.win-x64"), InstallKind.PortableZip);

        Assert.DoesNotContain("osx", picked!.Name);
        Assert.DoesNotContain(".dmg", picked.Name);
        Assert.EndsWith(".zip", picked.Name);
    }

    [Fact]
    public void MacRid_IsNeverGivenAWindowsSetup()
    {
        var assets = FullAssetSet("v1.17.0");
        var picked = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.osx-x64"), InstallKind.MacBundle);

        Assert.DoesNotContain("Setup.exe", picked!.Name);
        Assert.DoesNotContain("win-", picked.Name);
    }

    [Fact]
    public void WinX86_AlsoFindsASetup()
    {
        var assets = FullAssetSet("v1.17.0");
        var picked = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.win-x86"), InstallKind.InnoInstall);

        Assert.Equal("TeachCraft-v1.17.0-win-x86-sc-Setup.exe", picked!.Name);
    }

    [Fact]
    public void MissingTarget_ReturnsNullInsteadOfThrowing()
    {
        var assets = FullAssetSet("v1.17.0").Where(a => !a.Name.Contains("win-x64-sc-Setup")).ToList();
        var picked = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.win-x64"), InstallKind.InnoInstall);

        Assert.Null(picked);
    }

    [Theory]
    [InlineData("SHA256SUMS.txt")]
    [InlineData("TeachCraft-v1.17.0-win-x64-sc-Setup.exe.sha256")]
    [InlineData("TeachCraft.1.17.0.nupkg")]
    [InlineData("readme.txt")]
    public void NonPackageAssets_AreNeverSelected(string name)
    {
        var assets = new List<ReleaseAsset> { new(name, 10, "https://e/" + name) };

        var picked = UpdateAssetSelector.Select(assets, Id("1.17.0.sc.win-x64"), InstallKind.InnoInstall);

        Assert.Null(picked);
    }

    [Fact]
    public void LegacyIdentity_FallsBackToScAndSaysSo()
    {
        var assets = FullAssetSet("v1.17.0");
        // legacy 身份拿不到 mode，只能保守选 sc（自带运行时，最稳）。
        var legacy = BuildIdentity.Parse(null, "1.17.0.0").WithRid("win-x64");

        var picked = UpdateAssetSelector.Select(assets, legacy, InstallKind.InnoInstallLegacy);

        Assert.Equal("TeachCraft-v1.17.0-win-x64-sc-Setup.exe", picked!.Name);
        Assert.True(UpdateAssetSelector.NeedsModeExplanation(legacy));
    }
}
