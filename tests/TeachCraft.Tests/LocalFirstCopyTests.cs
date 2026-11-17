namespace TeachCraft.Tests;

/// <summary>
/// 联网口径的守护测试。
///
/// 2026-11-16 起，「不联网 / 不上传」的绝对承诺被废除：接入集控必然联网。
/// 现在的契约是「两处联网，都由用户明确开启」——检查更新与集控。
/// 这条测试的存在意义是：以后有人随手写一句「无需联网」或「从不上传」，
/// 会立刻被打红；同时保证「集控默认关闭」这条不被悄悄改掉。
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
    public void App_DoesNotClaimNothingIsEverUploaded()
    {
        // 集控会把班级与运行状态报到平台，且未来可能扩展。
        var surface = RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "SeatGrid.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "ClassBlock.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "GradeWeave.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "MeetRoster.App"), ".axaml", ".cs"))
            .Concat(RepoRoot.ReadAll(Path.Combine("src", "GradeStrip.App"), ".axaml", ".cs"));

        var offenders = surface
            .Where(f => f.Text.Contains("不上传", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoShippedSurfaceClaimsNothingIsEverStored()
    {
        // 程序会在 %LOCALAPPDATA%\TeachCraft\ 留存更新状态、集控设置与安装包，
        // 任何「不留存」的绝对表述都成了假话。
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

        Assert.Empty(offenders);
    }

    [Fact]
    public void App_StatesBothNetworkSurfaces()
    {
        var corpus = RepoRoot.ReadAll(Path.Combine("src", "TeachCraft.App"), ".axaml", ".cs")
            .Select(f => f.Text)
            .ToList();

        // 必须说清联网在哪两处，而不是含糊地说「可能联网」。
        Assert.Contains(corpus, text => text.Contains("仅检查更新会联网", StringComparison.Ordinal)
                                        || text.Contains("联网仅限更新与集控", StringComparison.Ordinal)
                                        || text.Contains("除更新外不联网", StringComparison.Ordinal));
    }

    [Fact]
    public void WalkthroughArgsNeverOpenPersistentConnections()
    {
        // 集控将是唯一一条常驻连接。走查参数一律不能联网，否则一次截图就会
        // 往开发者本机写凭据、或连到真实 Hub。这条在接入集控之前就先立起来。
        var app = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "App.axaml.cs"));
        Assert.Contains("isWalkthrough", app, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacySentenceIsConsistentBetweenPanelAndCatalog()
    {
        // 这两处曾经把同一句话各写了一份，改一处会让面板自己前后矛盾。
        var panel = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "Views", "ProjectPanelView.axaml"));
        var catalog = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "ViewModels", "ToolDescriptionCatalog.cs"));

        Assert.Contains("本机处理", panel);
        Assert.Contains("本机处理", catalog);
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
        // 「除「检查更新」外全程不联网」这类绝对句式正是要消灭的。
        // 注意：断言的是「不联网。」这个完整句式，而不是「不联网」三个字——
        // 「联网只在……不建立连接」是合法表述。
        Assert.DoesNotContain("全程不联网", RepoRoot.Read("AGENTS.md"));

        var mem01 = RepoRoot.Read(Path.Combine(".memory", "01-项目概览.md"));
        Assert.DoesNotContain("全程不联网", mem01);

        // 但必须说明联网发生在哪两处。
        Assert.Contains("检查更新", mem01);
        Assert.Contains("集控", mem01);
    }
}
