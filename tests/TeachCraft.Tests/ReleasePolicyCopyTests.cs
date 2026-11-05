namespace TeachCraft.Tests;

/// <summary>
/// 发布策略与文案契约（计划 todo 4 的验收门）。
/// </summary>
public sealed class ReleasePolicyCopyTests
{
    [Fact]
    public void PreviewDemotionStepIsGone()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.DoesNotContain("Demote Previous Preview", wf);
        Assert.DoesNotContain("自动转为草稿", wf);
    }

    [Fact]
    public void PrereleaseFlagSurvivesExactlyOnce()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        var lines = wf.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("prerelease: ${{ env.IS_RELEASE != 'true' }}", lines);
    }

    [Fact]
    public void DocsMatchNewPolicy()
    {
        Assert.DoesNotContain("自动转为 Draft", RepoRoot.Read("AGENTS.md"));

        var mem05 = RepoRoot.Read(Path.Combine(".memory", "05-构建发布与版本号.md"));
        Assert.DoesNotContain("自动转 Draft", mem05);
        Assert.DoesNotContain("降级旧预览", mem05);
        Assert.DoesNotContain("只使用数字段", mem05);
    }

    [Fact]
    public void ReleaseBodyStatesOnlyUpdateUsesNetwork()
    {
        var wf = RepoRoot.Read(".github/workflows/release-draft.yml");
        Assert.Contains("仅「检查更新」会联网", wf);
    }
}
