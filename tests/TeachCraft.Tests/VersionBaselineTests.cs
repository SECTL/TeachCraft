namespace TeachCraft.Tests;

/// <summary>
/// 版本基线契约（计划 todo 17 的验收门）。
/// </summary>
public sealed class VersionBaselineTests
{
    [Fact]
    public void VersionIs1170()
    {
        Assert.Contains("<Version>1.17.0</Version>", RepoRoot.Read("Directory.Build.props"));
    }

    [Fact]
    public void DocsBaselinesAgree()
    {
        Assert.Contains("1.17.0", RepoRoot.Read(Path.Combine(".memory", "README.md")));
        Assert.Contains("1.17.0", RepoRoot.Read(Path.Combine(".memory", "08-待办与后续.md")));
        Assert.Contains("1.17.0", RepoRoot.Read(Path.Combine(".memory", "05-构建发布与版本号.md")));
    }

    [Fact]
    public void VersionIsNotHardcodedInWorkflowOrScript()
    {
        // 唯一版本源是 Directory.Build.props，脚本与工作流不得重复硬编码。
        Assert.DoesNotContain("1.17.0", RepoRoot.Read(".github/workflows/release-draft.yml"));
        Assert.DoesNotContain("1.17.0", RepoRoot.Read("build-installer.ps1"));
    }
}
