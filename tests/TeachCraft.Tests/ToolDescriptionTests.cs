using TeachCraft.App.ViewModels;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

/// <summary>
/// 工具箱「点开卡片看说明」的数据层回归：
/// 说明文案必须覆盖全部 10 件工具与 5 件常备应用，且 ID 与 .memory/03 的工具契约一致。
/// </summary>
public sealed class ToolDescriptionTests
{
    /// <summary>工具 ID 与顺序是稳定契约，改动必须升 Minor 并同步 .memory/03。</summary>
    private static readonly string[] ExpectedToolIds =
    {
        "image-convert",
        "image-resize",
        "image-compress",
        "image-watermark",
        "pdf-word",
        "word-pdf",
        "images-pdf",
        "pdf-images",
        "pdf-merge",
        "pdf-split",
    };

    private static readonly string[] ExpectedAppKeys =
    {
        "gradestrip",
        "seatgrid",
        "classblock",
        "gradeweave",
        "meetroster",
    };

    [Fact]
    public void Catalog_CoversEveryToolInContractOrder()
    {
        Assert.Equal(ExpectedToolIds, ToolDescriptionCatalog.ToolIds);

        foreach (var id in ExpectedToolIds)
            Assert.NotNull(ToolDescriptionCatalog.ForTool(id));
    }

    [Fact]
    public void Catalog_CoversEveryPinnedApp()
    {
        Assert.Equal(ExpectedAppKeys, ToolDescriptionCatalog.AppKeys);

        foreach (var key in ExpectedAppKeys)
            Assert.NotNull(ToolDescriptionCatalog.ForApp(key));
    }

    [Fact]
    public void Catalog_UnknownIdReturnsNull()
    {
        // 未登记时返回 null 而不是抛异常：界面据此隐藏展开区，不至于崩。
        Assert.Null(ToolDescriptionCatalog.ForTool("no-such-tool"));
        Assert.Null(ToolDescriptionCatalog.ForApp("no-such-app"));
        Assert.Null(ToolDescriptionCatalog.ForTool(null));
    }

    [Fact]
    public void Catalog_EveryDescriptionIsCompleteAndDistinct()
    {
        var taglines = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ToolDescriptionCatalog.ToolIds)
        {
            var description = ToolDescriptionCatalog.ForTool(id)!;
            AssertComplete(description, id);
            Assert.True(taglines.Add(description.Tagline), $"{id} 的一句话说与其他条目重复了。");
        }

        foreach (var key in ToolDescriptionCatalog.AppKeys)
        {
            var description = ToolDescriptionCatalog.ForApp(key)!;
            AssertComplete(description, key);
            Assert.True(taglines.Add(description.Tagline), $"{key} 的一句话说与其他条目重复了。");
        }
    }

    [Fact]
    public void Catalog_HighlightsAndFeaturesAreShortEnoughForCards()
    {
        foreach (var id in ToolDescriptionCatalog.ToolIds)
        {
            var description = ToolDescriptionCatalog.ForTool(id)!;
            Assert.InRange(description.Highlights.Count, 3, 4);
            Assert.Equal(3, description.Features.Count);

            // chip 一行放得下：要点不超过 8 个汉字宽度，避免说明区被撑得很高。
            foreach (var highlight in description.Highlights)
                Assert.InRange(highlight.Length, 2, 16);

            foreach (var feature in description.Features)
                Assert.InRange(feature.Length, 8, 80);
        }
    }

    [Fact]
    public void ToolItemViewModel_ExposesItsOwnDescriptionAndToggles()
    {
        var tool = new ToolItemViewModel(
            ToolKind.ImageConvert, "01", "image-convert", "图片格式转换",
            "PNG、JPG、WebP 格式之间自由互转。", "image", "图",
            "PNG / JPG / WebP", "转换后的 PNG / JPG / WebP 文件", new[] { "PNG", "JPG", "WebP" });

        Assert.True(tool.HasDetail);
        Assert.Same(ToolDescriptionCatalog.ForTool("image-convert"), tool.Detail);
        Assert.False(tool.IsDescriptionExpanded);
        Assert.Equal("展开说明", tool.DescriptionToggleLabel);

        tool.ToggleDescription();
        Assert.True(tool.IsDescriptionExpanded);
        Assert.Equal("收起说明", tool.DescriptionToggleLabel);

        tool.ToggleDescription();
        Assert.False(tool.IsDescriptionExpanded);
        Assert.Equal("展开说明", tool.DescriptionToggleLabel);
    }

    [Fact]
    public void ToolItemViewModel_WithoutRegisteredDescriptionStaysCollapsed()
    {
        // 将来临时加一件工具但忘了写说明：卡片不展开空白区，也不报错。
        var tool = new ToolItemViewModel(
            ToolKind.PdfMerge, "99", "not-registered", "临时工具",
            "还没写说明。", "file", "档", "PDF", "PDF");

        Assert.False(tool.HasDetail);
        tool.ToggleDescription();
        Assert.False(tool.IsDescriptionExpanded);
    }

    [Fact]
    public void AppCards_EachBindTheirOwnDescription()
    {
        var viewModel = new MainViewModel();

        var cards = new[]
        {
            (ToolDescriptionCatalog.GradeStripKey, "分晓", viewModel.GradeStripCard),
            (ToolDescriptionCatalog.SeatGridKey, "归位", viewModel.SeatGridCard),
            (ToolDescriptionCatalog.ClassBlockKey, "时序", viewModel.ClassBlockCard),
            (ToolDescriptionCatalog.GradeWeaveKey, "织序", viewModel.GradeWeaveCard),
            (ToolDescriptionCatalog.MeetRosterKey, "竞名", viewModel.MeetRosterCard),
        };

        foreach (var (key, name, card) in cards)
        {
            Assert.Equal(key, card.Key);
            Assert.Equal(name, card.Name);
            Assert.Same(ToolDescriptionCatalog.ForApp(key), card.Detail);
            Assert.False(card.IsDescriptionExpanded);
        }

        // 五张卡不能共用同一个说明对象，否则展开任意一张都会显示同一段话。
        Assert.Equal(cards.Length, cards.Select(item => item.Item3.Detail).Distinct().Count());
    }

    [Fact]
    public void AppCard_TogglesIndependently()
    {
        var viewModel = new MainViewModel();

        viewModel.SeatGridCard.ToggleDescription();

        Assert.True(viewModel.SeatGridCard.IsDescriptionExpanded);
        Assert.False(viewModel.GradeStripCard.IsDescriptionExpanded);
        Assert.False(viewModel.MeetRosterCard.IsDescriptionExpanded);
        Assert.Equal("收起说明", viewModel.SeatGridCard.DescriptionToggleLabel);
    }

    [Fact]
    public void Search_MatchesWordsThatOnlyLiveInTheExpandedDescription()
    {
        var viewModel = new MainViewModel();

        // 「EXIF」只出现在说明里，卡片短描述没有，用来证明说明文案进了检索面。
        viewModel.SearchText = "EXIF";

        Assert.Contains(viewModel.VisibleImageTools, tool => tool.Id == "image-convert");
        Assert.Contains(viewModel.VisibleImageTools, tool => tool.Id == "image-resize");
        // 图片工具里只有这两件写得进「EXIF」，其余不该被顺带命中。
        Assert.Equal(2, viewModel.VisibleImageTools.Count);
        Assert.Empty(viewModel.VisibleFileTools);
    }

    [Fact]
    public void Search_MatchesAppDescriptionAndKeepsOthersHidden()
    {
        var viewModel = new MainViewModel();

        viewModel.SearchText = "走班";

        Assert.True(viewModel.IsGradeWeaveVisible);
        Assert.False(viewModel.IsGradeStripVisible);
        Assert.False(viewModel.IsSeatGridVisible);
    }

    [Fact]
    public void ExpandState_IsIndependentPerToolInstance()
    {
        // 工具卡的展开状态挂在视图模型实例上：两件工具各自展开、互不影响。
        // （常备卡曾经写成 static 字段，导致第二个视图模型一创建就继承上次的展开状态。）
        var first = new MainViewModel();
        var second = new MainViewModel();

        first.VisibleImageTools[0].ToggleDescription();

        Assert.Contains(first.VisibleImageTools, tool => tool.IsDescriptionExpanded);
        Assert.DoesNotContain(second.VisibleImageTools, tool => tool.IsDescriptionExpanded);
    }

    [Fact]
    public void TogglingManyTimes_AlwaysReturnsToTheOppositeState()
    {
        // 展开 / 收起是可重入的：奇数次切换一定与初始相反，偶数次一定回到初始。
        // 这条防的是「动画状态没落定就再点一次」导致状态与视觉错位。
        var viewModel = new MainViewModel();
        var tool = viewModel.VisibleImageTools[0];

        for (var i = 0; i < 41; i++) tool.ToggleDescription();
        Assert.True(tool.IsDescriptionExpanded);

        tool.ToggleDescription();
        Assert.False(tool.IsDescriptionExpanded);
    }

    [Fact]
    public void Search_FilteringOutAnExpandedCard_DoesNotResetItsState()
    {
        // 搜索把卡片筛掉再清空搜索，展开状态要原样回来。
        // 若在过滤时顺手重置状态，用户「展开着搜个词再清空」就会莫名丢展开。
        var viewModel = new MainViewModel();
        var tool = viewModel.VisibleImageTools[0];
        tool.ToggleDescription();
        Assert.True(tool.IsDescriptionExpanded);

        viewModel.SearchText = "PDF";
        Assert.DoesNotContain(tool, viewModel.VisibleImageTools);
        Assert.True(tool.IsDescriptionExpanded);

        viewModel.SearchText = string.Empty;
        Assert.Contains(tool, viewModel.VisibleImageTools);
        Assert.True(tool.IsDescriptionExpanded);
    }

    [Fact]
    public void AccessibleName_TellsWhatPressingWillDo()
    {
        // 读屏不该只念一个工具名：要说明按下去是展开还是收起。
        var viewModel = new MainViewModel();
        var tool = viewModel.VisibleImageTools[0];

        Assert.Equal($"{tool.Name}，展开说明", tool.DescriptionAccessibleName);

        tool.ToggleDescription();
        Assert.Equal($"{tool.Name}，收起说明", tool.DescriptionAccessibleName);

        var card = viewModel.SeatGridCard;
        Assert.Equal("归位，展开说明", card.DescriptionAccessibleName);
        card.ToggleDescription();
        Assert.Equal("归位，收起说明", card.DescriptionAccessibleName);
    }

    private static void AssertComplete(ToolDescription description, string key)
    {
        Assert.False(string.IsNullOrWhiteSpace(description.Tagline), $"{key} 缺少一句话说");
        Assert.False(string.IsNullOrWhiteSpace(description.InputDetail), $"{key} 缺少输入说明");
        Assert.False(string.IsNullOrWhiteSpace(description.OutputDetail), $"{key} 缺少输出说明");
        Assert.False(string.IsNullOrWhiteSpace(description.ProcessingDetail), $"{key} 缺少处理方式说明");
        Assert.NotEmpty(description.Highlights);
        Assert.NotEmpty(description.Features);

        // 面向老师的中文文案，不应把英文异常或堆栈塞进来。
        Assert.DoesNotContain("Exception", description.Tagline);
        Assert.DoesNotContain("null", description.Tagline);
    }
}
