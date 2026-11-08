using System.Xml.Linq;

namespace TeachCraft.Tests;

/// <summary>
/// `scripts/version.ps1` 只在 CI 里跑，本地测试碰不到——所以它坏了要等推送后
/// 14 个构建全挂才发现（2026-11-08 实际发生过一次）。
///
/// 根因：`Directory.Build.props` 里加了带 Condition 的 PropertyGroup 之后，
/// PowerShell 的 `$props.Project.PropertyGroup.Version` 返回 Object[] 而不是字符串，
/// `IsNullOrWhiteSpace` 收到数组直接抛异常。
///
/// 这里用同一份 XML 复现那段取值逻辑：脚本必须能从多个 PropertyGroup 中挑出
/// 唯一的非空 Version，且该值必须等于 Directory.Build.props 的基准版本。
/// </summary>
public sealed class VersionScriptTests
{
    /// <summary>与 scripts/version.ps1 的取值规则一致：所有 PropertyGroup 的 Version，挑第一个非空。</summary>
    private static string? ReadBaseVersion(string propsXml)
    {
        var doc = XDocument.Parse(propsXml);
        return doc.Root?
            .Elements("PropertyGroup")
            .Select(g => g.Element("Version")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    [Fact]
    public void VersionScript_PicksVersionWhenMultiplePropertyGroupsExist()
    {
        // 真实形态：一个无条件分组放 Version，后面还有带 Condition 的分组。
        // 取值必须仍然是 1.17.0，而不是数组或空。
        var xml = """
            <Project>
              <PropertyGroup>
                <Version>1.17.0</Version>
              </PropertyGroup>
              <PropertyGroup Condition="'$(MSBuildProjectName)' == 'TeachCraft.App'">
                <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
              </PropertyGroup>
            </Project>
            """;

        Assert.Equal("1.17.0", ReadBaseVersion(xml));
    }

    [Fact]
    public void VersionScript_RealPropsFileYieldsSingleVersion()
    {
        // 仓库真实的 Directory.Build.props：取值必须是单个字符串，且与基准版本一致。
        var xml = RepoRoot.Read("Directory.Build.props");
        var version = ReadBaseVersion(xml);

        Assert.False(string.IsNullOrWhiteSpace(version), "Directory.Build.props 必须有一个非空的 <Version>");

        var doc = XDocument.Parse(xml);
        var allVersions = doc.Root!
            .Elements("PropertyGroup")
            .Select(g => g.Element("Version")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        Assert.True(allVersions.Count >= 1);
        Assert.Equal(allVersions[0], version);
    }

    [Fact]
    public void VersionScript_ThrowsConceptuallyWhenVersionMissing()
    {
        // 缺 Version 时脚本会 throw；这里锁住「挑不到任何值」这个判定前提。
        var xml = """
            <Project>
              <PropertyGroup>
                <Product>x</Product>
              </PropertyGroup>
            </Project>
            """;

        Assert.Null(ReadBaseVersion(xml));
    }

    [Fact]
    public void VersionScript_StillGuardsEmptyVersion()
    {
        // 脚本里那句 throw 不能因为改成 FirstOrDefault 就失效：
        // 显式写了空的 <Version/> 也必须被当作缺失。
        var xml = """
            <Project>
              <PropertyGroup>
                <Version>   </Version>
              </PropertyGroup>
            </Project>
            """;

        Assert.Null(ReadBaseVersion(xml));
    }

    [Fact]
    public void VersionScript_WritesTagIntoCurrentProcessNotJustGitHubEnv()
    {
        // GITHUB_ENV 只对后续 step 生效。release-draft.yml 在**同一个 step**里
        // 紧接着读 $env:VERSION_TAG 拼 BUILD_IDENTITY，若脚本只写 GITHUB_ENV，
        // 拼出来就是 ".sc.win-x64"（没有版本号），更新功能的构建身份识别会全平台失效。
        // 因此脚本必须同时设置本进程的环境变量。
        var script = RepoRoot.Read("scripts/version.ps1");

        Assert.Contains("$env:VERSION_TAG = $tag", script);
        Assert.Contains("$env:IS_RELEASE = $isRelease", script);
    }

    [Fact]
    public void VersionScript_IsInvokedInTheSameStepThatBuildsIdentity()
    {
        // 这条锁住上面那条测试的前提：如果哪天有人把 version.ps1 拆到单独的
        // step，本进程环境变量就无所谓了，上面的断言会变成误导性的死约束。
        var workflow = RepoRoot.Read(".github/workflows/release-draft.yml");

        var prepare = workflow.IndexOf("./scripts/version.ps1", StringComparison.Ordinal);
        var identity = workflow.IndexOf("BUILD_IDENTITY=", StringComparison.Ordinal);

        Assert.True(prepare >= 0, "工作流里找不到 ./scripts/version.ps1");
        Assert.True(identity >= 0, "工作流里找不到 BUILD_IDENTITY");
        Assert.True(prepare < identity, "version.ps1 必须在拼 BUILD_IDENTITY 之前执行");
    }
}
