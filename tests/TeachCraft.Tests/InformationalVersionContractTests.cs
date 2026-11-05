namespace TeachCraft.Tests;

/// <summary>
/// 构建身份注入契约（计划 todo 2 的验收门）。
/// 类名必须与计划里的 --filter 一致，否则过滤器零匹配会静默通过。
/// </summary>
public sealed class InformationalVersionContractTests
{
    [Fact]
    public void SourceRevisionIsNotAppendedToIdentity()
    {
        // SDK 默认会把 git sha 追加成 …+sha 或 ….sha，严格解析会全部退化成「无身份」。
        var props = RepoRoot.Read("Directory.Build.props");
        Assert.Contains("<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>", props);
    }

    [Fact]
    public void CiInjectsIdentityForWindowsLinuxAndMac()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("InformationalVersion", wf);
        Assert.Contains("BUILD_IDENTITY", wf);

        var mac = RepoRoot.Read("bundle/macos/build-dmg.sh");
        Assert.Contains("-p:InformationalVersion=", mac);
        Assert.Contains("__BUNDLE_VERSION__", mac);
        Assert.Contains("__SHORT_VERSION__", mac);
    }

    [Fact]
    public void CiPublishesChecksumManifest()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("SHA256SUMS", wf);
        Assert.Contains("release-assets/*SHA256SUMS.txt", wf);
    }
}
