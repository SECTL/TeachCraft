namespace TeachCraft.Tests;

/// <summary>
/// 本地优先承诺的守护测试。
///
/// 加入「检查更新」之后，界面上任何「绝不联网」的绝对表述都会变成假话。
/// 这条测试的存在意义是：以后有人随手写一句「无需联网」，会被立刻打红。
/// </summary>
public sealed class LocalFirstCopyTests
{
    [Fact]
    public void App_ContainsNoAbsoluteNoNetworkClaim()
    {
        var offenders = RepoRoot
            .ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Where(f => f.Text.Contains("无需联网", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void App_DoesNotClaimUploadsAreNeverPossible()
    {
        var offenders = RepoRoot
            .ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Where(f => f.Text.Contains("不上传也不留存", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoShippedSurfaceClaimsNothingIsEverStored()
    {
        // 加入更新功能后，程序会在 %LOCALAPPDATA%\TeachCraft\Update\ 留存 state.json 与安装包，
        // 任何「不留存」的绝对表述都成了假话。这条要覆盖 UI、文档与发布正文三处。
        var surfaces = new List<(string Path, string Text)>
        {
            ("AGENTS.md", RepoRoot.Read("AGENTS.md")),
            (".memory/README.md", RepoRoot.Read(Path.Combine(".memory", "README.md"))),
            (".github/workflows/release-draft.yml", RepoRoot.Read(Path.Combine(".github", "workflows", "release-draft.yml"))),
        };
        surfaces.AddRange(RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs"));

        var offenders = surfaces
            .Where(f => f.Text.Contains("不留存", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        // 「不留存」这个词在加入更新功能后本身就是不准确的表述：
        // 程序确实会在本机留存状态与安装包。允许的写法是「只留在本机」「从不上传」，
        // 而不是给「不留存」打补丁。
        Assert.Empty(offenders);
    }

    [Fact]
    public void App_StatesThatOnlyUpdateCheckGoesOnline()
    {
        var corpus = RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Select(f => f.Text)
            .ToList();

        Assert.Contains(corpus, text =>
            text.Contains("仅检查更新会联网", StringComparison.Ordinal)
            || text.Contains("除更新外不联网", StringComparison.Ordinal));
    }

    [Fact]
    public void PrivacySentenceIsConsistentBetweenPanelAndCatalog()
    {
        // 这两处曾经把同一句话各写了一份，改一处会让面板自己前后矛盾。
        var panel = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "Views", "ProjectPanelView.axaml"));
        var catalog = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "ViewModels", "ToolDescriptionCatalog.cs"));

        Assert.Contains("文件不上传", panel);
        Assert.Contains("文件不上传", catalog);
        Assert.DoesNotContain("全程本机，文件不上传", panel);
    }

    [Fact]
    public void StatusBarDefaultAndResetUseTheSameConstant()
    {
        var vm = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "ViewModels", "MainViewModel.cs"));

        // 复位若直接写字面量，用完面板后旧口径会复活。
        Assert.DoesNotContain("SetStatus(\"本机运行", vm);
        Assert.Contains("SetStatus(DefaultStatusText)", vm);
    }

    [Fact]
    public void Docs_DoNotPromiseNoNetworkAtAll()
    {
        // 之前这里只断言「含『除』」，而「除「检查更新」外全程不联网」恰好满足——
        // 那正是要消灭的绝对表述，守护却永远绿。改成对绝对句式本身断言。
        Assert.DoesNotContain("不联网。", RepoRoot.Read("AGENTS.md"));

        var mem01 = RepoRoot.Read(Path.Combine(".memory", "01-项目概览.md"));
        Assert.DoesNotContain("全程不联网", mem01);
        Assert.DoesNotContain("不联网。", mem01);

        // 但必须说明联网发生在哪：「检查更新」四个字要在。
        Assert.Contains("检查更新", mem01);
    }
}
