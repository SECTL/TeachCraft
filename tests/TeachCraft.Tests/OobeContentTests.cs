using TeachCraft.App.ViewModels;

namespace TeachCraft.Tests;

/// <summary>
/// 引导文案回归。三条硬要求：
/// 1. 四页顺序与数量固定；
/// 2. 提到的每件工具 / 常备应用都从 ToolDescriptionCatalog 取，不在这里另写副本；
/// 3. 隐私口径服从 LocalFirstCopyTests 的禁用词表。
/// </summary>
public sealed class OobeContentTests
{
    private static string AllText() =>
        string.Join("\n", OobeContent.Steps.Select(s =>
            string.Join("\n", s.Eyebrow, s.Title, s.Lead, s.Body, string.Join("\n", s.Bullets))));

    [Fact]
    public void Steps_HaveFixedOrderAndCount()
    {
        Assert.Equal(4, OobeContent.Steps.Count);
        Assert.Equal(
            new[] { OobeStepKind.Welcome, OobeStepKind.LocalFirst, OobeStepKind.Capabilities, OobeStepKind.TryFirst },
            OobeContent.Steps.Select(s => s.Kind));

        for (var i = 0; i < OobeContent.Steps.Count; i++)
            Assert.Equal(i + 1, OobeContent.Steps[i].Index);
    }

    [Fact]
    public void Steps_HaveDistinctTitlesAndNonEmptyLeads()
    {
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in OobeContent.Steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Lead));
            Assert.True(titles.Add(step.Title), $"标题重复了：{step.Title}");
        }
    }

    /// <summary>
    /// 每页至少要有「两条要点」或「一条要点 + 可点击入口」。
    /// 第 4 页把三件事做成了按钮而不是文字清单——那正是它可行动的形式，
    /// 不该为了凑数把同一句话再说一遍。
    /// </summary>
    [Fact]
    public void Steps_EveryStepIsSubstantive()
    {
        foreach (var step in OobeContent.Steps)
        {
            var actions = step.Actions?.Count ?? 0;
            var substantive = step.Bullets.Count >= 2 || (step.Bullets.Count >= 1 && actions >= 2);
            Assert.True(substantive,
                $"{step.Kind} 只有 {step.Bullets.Count} 条要点与 {actions} 个入口，撑不起一页。");
        }
    }

    [Fact]
    public void StepFour_HasFourClickableActions()
    {
        var step = OobeContent.Steps.Single(s => s.Kind == OobeStepKind.TryFirst);
        Assert.NotNull(step.Actions);
        Assert.Equal(4, step.Actions!.Count);
        Assert.Equal(
            new[]
            {
                OobeActionKind.OpenGradeStrip, OobeActionKind.OpenSeatGrid,
                OobeActionKind.OpenUpdatePanel, OobeActionKind.OpenAccountPanel,
            },
            step.Actions.Select(a => a.Kind));
        Assert.All(step.Actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Label)));
    }

    [Fact]
    public void OnlyStepFourHasActions()
    {
        foreach (var step in OobeContent.Steps.Where(s => s.Kind != OobeStepKind.TryFirst))
            Assert.True(step.Actions is null || step.Actions.Count == 0,
                $"{step.Kind} 不该有入口按钮。");
    }

    [Fact]
    public void StepThree_NamesEveryPinnedAppWithItsTagline()
    {
        var text = string.Join("\n", OobeContent.Steps
            .Single(s => s.Kind == OobeStepKind.Capabilities).Bullets);

        foreach (var key in ToolDescriptionCatalog.AppKeys)
        {
            var description = ToolDescriptionCatalog.ForApp(key)!;
            Assert.Contains(description.Name, text, StringComparison.Ordinal);
            Assert.Contains(description.Tagline, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StepThree_NamesEveryTool()
    {
        var text = string.Join("\n", OobeContent.Steps
            .Single(s => s.Kind == OobeStepKind.Capabilities).Bullets);

        foreach (var id in ToolDescriptionCatalog.ToolIds)
            Assert.Contains(ToolDescriptionCatalog.ForTool(id)!.Name, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_ContainsNoBannedPrivacyPhrases()
    {
        var text = AllText();

        // 与 LocalFirstCopyTests 的禁用词表保持一致：这些绝对表述在本项目里都是假话。
        Assert.DoesNotContain("不留存", text, StringComparison.Ordinal);
        Assert.DoesNotContain("无需联网", text, StringComparison.Ordinal);
        Assert.DoesNotContain("不上传也不留存", text, StringComparison.Ordinal);
        Assert.DoesNotContain("全程本机，文件不上传", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_NamesAllOptInNetworkSurfaces()
    {
        // 1.45.0 起联网有三处：更新、账号与共享名单、集控。
        // 引导页必须把「账号与云同步、集控都默认关闭」讲清楚，
        // 否则老师会以为软件背着他在连网。
        var text = AllText();
        Assert.Contains("检查更新", text, StringComparison.Ordinal);
        Assert.Contains("账号", text, StringComparison.Ordinal);
        Assert.Contains("共享名单", text, StringComparison.Ordinal);
        Assert.Contains("集控", text, StringComparison.Ordinal);
        Assert.Contains("默认关闭", text, StringComparison.Ordinal);
        Assert.DoesNotContain("联网只有两处", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 目录里的 Name 必须与主界面卡片上显示的逐字相同，否则它只是第三份副本。
    /// 这条测试是「文案同源」的真正保证：改了任意一边都会变红。
    /// </summary>
    [Fact]
    public void CatalogNamesMatchMainViewModel()
    {
        var vm = new MainViewModel();

        foreach (var id in ToolDescriptionCatalog.ToolIds)
        {
            var tool = vm.FindTool(id);
            Assert.NotNull(tool);
            Assert.Equal(ToolDescriptionCatalog.ForTool(id)!.Name, tool!.Name);
        }

        var cards = new (string Key, AppCardViewModel Card)[]
        {
            (ToolDescriptionCatalog.GradeStripKey, vm.GradeStripCard),
            (ToolDescriptionCatalog.SeatGridKey, vm.SeatGridCard),
            (ToolDescriptionCatalog.ClassBlockKey, vm.ClassBlockCard),
            (ToolDescriptionCatalog.GradeWeaveKey, vm.GradeWeaveCard),
            (ToolDescriptionCatalog.MeetRosterKey, vm.MeetRosterCard),
            (ToolDescriptionCatalog.HomeworkKey, vm.HomeworkCard),
        };

        foreach (var (key, card) in cards)
            Assert.Equal(ToolDescriptionCatalog.ForApp(key)!.Name, card.Name);
    }

    [Fact]
    public void Catalog_EveryEntryHasName()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ToolDescriptionCatalog.ToolIds)
        {
            var name = ToolDescriptionCatalog.ForTool(id)!.Name;
            Assert.False(string.IsNullOrWhiteSpace(name), $"{id} 没有名称。");
            Assert.True(names.Add(name), $"名称重复了：{name}");
        }

        foreach (var key in ToolDescriptionCatalog.AppKeys)
        {
            var name = ToolDescriptionCatalog.ForApp(key)!.Name;
            Assert.False(string.IsNullOrWhiteSpace(name), $"{key} 没有名称。");
            Assert.True(names.Add(name), $"名称重复了：{name}");
        }
    }
}
