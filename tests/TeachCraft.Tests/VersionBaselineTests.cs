using System.Xml.Linq;

namespace TeachCraft.Tests;

/// <summary>
/// 版本基线契约（计划 todo 17 的验收门）。
///
/// 这里的断言**不写死版本号**：唯一版本源是 Directory.Build.props，
/// 测试若也硬编码一个版本，每次晋级都要手改测试，等于把「唯一版本源」
/// 变成了「两个地方要同步」。改为读出版本再检查各处是否一致。
/// </summary>
public sealed class VersionBaselineTests
{
    /// <summary>从 Directory.Build.props 读基准版本（与 scripts/version.ps1 同一规则）。</summary>
    private static string BaseVersion()
    {
        var doc = XDocument.Parse(RepoRoot.Read("Directory.Build.props"));
        var version = doc.Root?
            .Elements("PropertyGroup")
            .Select(g => g.Element("Version")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        Assert.False(string.IsNullOrWhiteSpace(version), "Directory.Build.props 必须有非空的 <Version>");
        return version!;
    }

    [Fact]
    public void VersionIsSemanticThreePart()
    {
        // AssemblyVersion 仍是三段；身份串的预览序号由 CI 注入，不写进 props。
        Assert.Matches(@"^\d+\.\d+\.\d+$", BaseVersion());
    }

    [Fact]
    public void DocsBaselinesAgree()
    {
        // .memory 里的版本记录必须跟上当前基线，否则读文档的人会以为还在旧版。
        var version = BaseVersion();
        Assert.Contains(version, RepoRoot.Read(Path.Combine(".memory", "README.md")));
        Assert.Contains(version, RepoRoot.Read(Path.Combine(".memory", "08-待办与后续.md")));
        Assert.Contains(version, RepoRoot.Read(Path.Combine(".memory", "05-构建发布与版本号.md")));
    }

    [Fact]
    public void VersionIsNotHardcodedInWorkflowOrScript()
    {
        // 唯一版本源是 Directory.Build.props，脚本与工作流不得重复硬编码。
        var version = BaseVersion();
        Assert.DoesNotContain(version, RepoRoot.Read(".github/workflows/release-draft.yml"));
        Assert.DoesNotContain(version, RepoRoot.Read("build-installer.ps1"));
    }
}
