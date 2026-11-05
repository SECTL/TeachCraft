namespace TeachCraft.Tests;

/// <summary>
/// 安装包身份记录契约（计划 todo 3 的验收门）。
/// </summary>
public sealed class InstallerIdentityTests
{
    [Fact]
    public void InstallerWritesIdentityIn64BitView()
    {
        var iss = RepoRoot.Read("installer/TeachCraft.iss");
        Assert.Contains("[Registry]", iss);
        // arm64 的 ArchitecturesInstallIn64BitMode 会把默认视图变 32 位，写 HKCU 读不到。
        Assert.Contains("HKCU64", iss);
    }

    [Fact]
    public void InstallerUsesPerSkuSubkeyWithCompileTimeConstants()
    {
        var iss = RepoRoot.Read("installer/TeachCraft.iss");
        Assert.Contains("Install-{#MyAppMode}-{#Arch}", iss);
        Assert.Contains("{#MyRid}", iss);

        // Inno 文档说明 {code:} 在 Subkey 里不受支持，必须用编译期常量。
        var registry = iss[iss.IndexOf("[Registry]", StringComparison.Ordinal)..];
        Assert.DoesNotContain("{code:", registry);
    }

    [Fact]
    public void ReaderReadsBothRegistryViews()
    {
        // 写的是 HKCU64，但 Inno 在 lowest 权限下的实际落点无法离线验证，
        // 因此读取端两个视图都要扫，不能赌一个假设。
        var reader = RepoRoot.Read(Path.Combine("src", "TeachCraft.Core", "Updating", "InstallRecordReader.cs"));
        Assert.Contains("Registry64", reader);
        Assert.Contains("Registry32", reader);
    }

    [Fact]
    public void ScriptPassesModeAndRid()
    {
        var script = RepoRoot.Read("build-installer.ps1");
        Assert.Contains("/DMyAppMode=", script);
        Assert.Contains("/DMyRid=", script);
        Assert.Contains("/DMyAppVersion=$Version", script);
    }

    [Fact]
    public void CiEnsuresInnoSetup()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("Ensure Inno Setup", wf);
    }
}
