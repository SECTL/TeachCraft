using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 资产命名契约（计划 todo 16 的验收门）。
/// 单一所有者是 AssetNaming，shell 与测试都必须引用同一常量。
/// </summary>
public sealed class AssetNamingTests
{
    [Fact]
    public void MacZipSuffixIsSingleOwner()
    {
        Assert.Equal("-mac.zip", AssetNaming.MacZipSuffix);
    }

    [Fact]
    public void ShellScriptUsesTheSameSuffix()
    {
        var sh = RepoRoot.Read("bundle/macos/build-dmg.sh");
        Assert.Contains("ZIP_SUFFIX=\"" + AssetNaming.MacZipSuffix + "\"", sh);
        Assert.Contains("${ZIP_SUFFIX}", sh);
    }

    [Fact]
    public void DmgNameDoesNotUseZipSuffix()
    {
        // 防止改错行：把 ZIP_SUFFIX 拼进 DMG 名字会让镜像变成 -mac.zip.dmg。
        var sh = RepoRoot.Read("bundle/macos/build-dmg.sh");
        var dmgLine = sh.Split('\n').First(l => l.StartsWith("DMG_NAME=", StringComparison.Ordinal));
        Assert.DoesNotContain("ZIP_SUFFIX", dmgLine);
    }

    [Fact]
    public void ChecksumsNameMatchesCiOutput()
    {
        Assert.Equal("TeachCraft-v1.17.0-SHA256SUMS.txt", AssetNaming.ChecksumsName("v1.17.0"));
    }
}
