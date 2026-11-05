using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using TeachCraft.App.Controls;
using TeachCraft.App.ViewModels;
using TeachCraft.Shared;

namespace TeachCraft.App.Views;

/// <summary>
/// 工具箱工作台的**设计走查探针**（只读诊断，不参与产品逻辑）。
/// 从 MainWindow.axaml.cs 拆出来，让主文件只留窗口行为：这些方法只在
/// `--shot` / `--expand` / `--interact` 等命令行走查模式下被调用，
/// 平时不影响启动与交互路径。
///
/// 存在的理由：卡片展开涉及动画曲线、样式优先级、滚动时序与响应式重排，
/// 这些光靠截图肉眼比对容易漏，所以每条都留了可回读数值的取证入口。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 设计走查：展开一个工具卡或常备卡的说明区后截图。
    /// 走查参数里的 id 既认工具 ID（image-convert），也认常备应用 key（seatgrid / gradestrip）。
    /// </summary>
    public bool ExpandForShot(string id)
    {
        if (ViewModel.FindTool(id) is { } tool)
        {
            tool.IsDescriptionExpanded = true;
            MainScroll.Offset = new Vector(0, 0);
            BringCardIntoView(tool);
            return true;
        }

        AppCardViewModel? card = id switch
        {
            ToolDescriptionCatalog.GradeStripKey => ViewModel.GradeStripCard,
            ToolDescriptionCatalog.SeatGridKey => ViewModel.SeatGridCard,
            ToolDescriptionCatalog.ClassBlockKey => ViewModel.ClassBlockCard,
            ToolDescriptionCatalog.GradeWeaveKey => ViewModel.GradeWeaveCard,
            ToolDescriptionCatalog.MeetRosterKey => ViewModel.MeetRosterCard,
            _ => null,
        };

        if (card is null) return false;

        card.IsDescriptionExpanded = true;
        MainScroll.Offset = new Vector(0, 0);
        BringAppCardIntoView(card);
        return true;
    }


    /// <summary>
    /// 设计走查：把工具卡 / 常备卡的实测尺寸写进文本，替代「靠肉眼比截图」。
    /// 每行给出类型、标题、X/Y/宽/高与说明区是否可见，用来确认双列宽度、
    /// 展开后没有把隔壁卡拉出空白、以及说明区确实占到了自然高度。
    /// </summary>
    public void DumpCardLayout(string path)
    {
        UpdateLayout();

        var viewModel = ViewModel;

        // 状态栏三项：确认「检查更新」入口真的在可视树里、版本行能自证身份、
        // 以及提示条当前是否可见。没有这几行，走查就无法证明按钮存在。
        var checkUpdatePresent = this.GetVisualDescendants()
            .OfType<Button>()
            .Any(b => (AutomationProperties.GetName(b) ?? "") == "检查更新");
        var badgeVisible = this.GetVisualDescendants()
            .OfType<StackPanel>()
            .Any(p => p.IsVisible && p.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(t => (t.Text ?? "") == viewModel.UpdateBadgeText && viewModel.UpdateBadgeText.Length > 0));

        var lines = new List<string>
        {
            $"scroll-extent={MainScroll.Extent.Width:0.#}x{MainScroll.Extent.Height:0.#} " +
            $"viewport={MainScroll.Viewport.Width:0.#}x{MainScroll.Viewport.Height:0.#} " +
            $"offset={MainScroll.Offset.Y:0.#}",
            $"statusbar-check-update-present={checkUpdatePresent}",
            $"statusbar-versionline={viewModel.VersionLine}",
            $"statusbar-badge={viewModel.UpdateBadgeText}",
            $"statusbar-badge-visible={badgeVisible}",
        };

        foreach (var card in this.GetVisualDescendants().OfType<ToolCardView>())
        {
            var tool = card.DataContext as ToolItemViewModel;
            var reveal = card.GetVisualDescendants().OfType<ExpandReveal>().FirstOrDefault();
            var position = card.TranslatePoint(new Point(0, 0), this) ?? default;

            lines.Add(
                $"tool {tool?.Id,-16} x={position.X,7:0.#} y={position.Y,8:0.#} " +
                $"w={card.Bounds.Width,7:0.#} h={card.Bounds.Height,7:0.#} " +
                $"expanded={tool?.IsDescriptionExpanded} reveal={reveal?.Bounds.Height ?? -1:0.#} " +
                $"progress={reveal?.Progress ?? -1:0.00} content={reveal?.ContentHeight ?? -1:0.#} " +
                $"clipped={(reveal is null ? "?" : (reveal.ContentHeight > reveal.Bounds.Height + 0.5 ? "YES" : "no"))}");
        }

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            if (border.Tag is not AppCardViewModel card || !border.Classes.Contains("app-card")) continue;
            var reveal = border.GetVisualDescendants().OfType<ExpandReveal>().FirstOrDefault();
            var position = border.TranslatePoint(new Point(0, 0), this) ?? default;
            var tagline = border.GetVisualDescendants().OfType<ToolDescriptionView>().FirstOrDefault()
                ?.DataContext is ToolDescription bound ? bound.Tagline : "（未绑定）";

            lines.Add(
                $"app  {card.Key,-16} x={position.X,7:0.#} y={position.Y,8:0.#} " +
                $"w={border.Bounds.Width,7:0.#} h={border.Bounds.Height,7:0.#} " +
                $"expanded={card.IsDescriptionExpanded} reveal={reveal?.Bounds.Height ?? -1:0.#} " +
                $"progress={reveal?.Progress ?? -1:0.00} content={reveal?.ContentHeight ?? -1:0.#} " +
                $"clipped={(reveal is null ? "?" : (reveal.ContentHeight > reveal.Bounds.Height + 0.5 ? "YES" : "no"))} " +
                $"tagline=[{tagline[..Math.Min(18, tagline.Length)]}]");
        }

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：逐帧采样展开动画，证明「动画确实在跑」而不是瞬间跳开。
    /// 采样点写进文本，包含进度、卡片高度与说明区高度三条曲线。
    /// </summary>
    public async Task RecordExpandAnimationAsync(string path, string toolId)
    {
        var tool = ViewModel.FindTool(toolId);
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        if (tool is null || card is null)
        {
            File.WriteAllText(path, "找不到工具卡：" + toolId);
            return;
        }

        var lines = new List<string> { "t(ms)\tprogress\treveal-height\tcard-height" };
        await ExpandAndSampleAsync(path, lines, tool, card);
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：验证「点卡片展开、点卡内按钮不展开、点「打开」进抽屉」三条交互。
    /// 用真实的可视树控件调用，结果写进文本，避免只凭截图猜有没有接上。
    /// </summary>
    public async Task RecordInteractionAsync(string path, string toolId)
    {
        var tool = ViewModel.FindTool(toolId);
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        if (tool is null || card is null)
        {
            File.WriteAllText(path, "找不到工具卡：" + toolId);
            return;
        }

        var lines = new List<string>();

        // 1) 点卡片本体：应展开
        card.SimulateCardClickForShot();
        lines.Add($"点击卡片 → expanded={tool.IsDescriptionExpanded}（期望 True）");

        // 2) 点卡内按钮：不应切换展开状态
        card.SimulateInnerButtonClickForShot();
        lines.Add($"点击卡内按钮 → expanded={tool.IsDescriptionExpanded}（期望 True，且不被切换）");

        // 3) 点「打开」：应进抽屉
        card.SimulateOpenForShot();
        await Task.Delay(200);
        lines.Add($"点击「打开」→ IsToolPanelOpen={ViewModel.IsToolPanelOpen}（期望 True）");
        lines.Add($"当前选中工具={ViewModel.SelectedTool?.Id}（期望 {toolId}）");

        // 4) 收起说明
        ViewModel.ClosePanels();
        tool.IsDescriptionExpanded = false;
        lines.Add($"收起后 expanded={tool.IsDescriptionExpanded}（期望 False）");

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：验证常备卡的点击守卫——点卡片展开、点卡内按钮（打开应用 / 项目详情）不触发展开。
    /// 走查参数也顺带回读版本行，确认版本号跟着 Directory.Build.props 走。
    /// </summary>

    public void RecordAppInteraction(string path)
    {
        var lines = new List<string> { $"版本行={ViewModel.VersionLine}" };

        var card = ViewModel.SeatGridCard;
        var border = this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(view => ReferenceEquals(view.Tag, card) && view.Classes.Contains("app-card"));
        lines.Add($"找到常备卡 SeatGrid={border is not null}（期望 True）");
        if (border is null)
        {
            File.WriteAllLines(path, lines);
            return;
        }

        // 卡内第一个按钮就是「打开应用」：点它只应打开窗口，不该切换展开。
        var button = border.GetVisualDescendants().OfType<Button>().LastOrDefault();
        lines.Add($"卡内按钮数={border.GetVisualDescendants().OfType<Button>().Count()}");

        card.IsDescriptionExpanded = false;
        if (button is not null)
        {
            var point = new PointerPressedEventArgs(
                button, new Pointer(0, PointerType.Mouse, true), button, default,
                0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                    PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None);
            OnAppCardPressed(button, point);
        }
        lines.Add($"点卡内按钮 → expanded={card.IsDescriptionExpanded}（期望 False）");

        var cardPoint = new PointerPressedEventArgs(
            border, new Pointer(0, PointerType.Mouse, true), border, default,
            0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        OnAppCardPressed(border, cardPoint);
        lines.Add($"点卡片 → expanded={card.IsDescriptionExpanded}（期望 True）");

        card.IsDescriptionExpanded = false;
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：核对「展开态看得见」与「点标题也能展开」。
    /// 展开态靠 CSS 类换底色与描边，绑定写错会静默失效，所以这里回读实际的
    /// 类名、背景色、标题下划线，而不是只看截图。
    /// </summary>
    public async Task RecordExpandVisualsAsync(string path)
    {
        var lines = new List<string>();

        var tool = ViewModel.VisibleImageTools[0];
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        var border = card?.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(view => view.Classes.Contains("tool-card"));
        if (tool is null || card is null || border is null)
        {
            File.WriteAllText(path, "找不到工具卡");
            return;
        }

        lines.Add($"收起态 expanded-class={border.Classes.Contains("expanded")} " +
                  $"bg={Describe(border.Background)} border={Describe(border.BorderBrush)} " +
                  $"classes=[{string.Join(",", border.Classes)}] " +
                  $"styles={StyleCount(border)}");

        var title = card.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(view => view.Classes.Contains("tool-card-title"));
        lines.Add($"标题找到={title is not null} 收起态下划线={title?.TextDecorations is not null}");

        tool.IsDescriptionExpanded = true;

        // 底色与标题色都挂着 0.16s 的 BrushTransition，而 Transitions 在 Avalonia 里
        // 是通过动画改属性值实现的：刚设完立刻读只会拿到起始值，必须等过渡跑完。
        await Task.Delay(260);
        UpdateLayout();

        lines.Add($"展开态 expanded-class={border.Classes.Contains("expanded")} " +
                  $"bg={Describe(border.Background)} border={Describe(border.BorderBrush)} " +
                  $"classes=[{string.Join(",", border.Classes)}] " +
                  $"styles={StyleCount(border)}");
        lines.Add($"展开态下划线={title?.TextDecorations is not null} " +
                  $"标题色={Describe(title?.Foreground)}");
        lines.Add($"读屏名称={tool.DescriptionAccessibleName}");

        tool.IsDescriptionExpanded = false;
        await Task.Delay(260);
        UpdateLayout();
        lines.Add($"收回后 expanded-class={border.Classes.Contains("expanded")}");

        // 常备卡同一套检查：它有局部 Background/BorderBrush，展开态是否也压不过样式
        var appCard = ViewModel.SeatGridCard;
        var appBorder = this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(view => ReferenceEquals(view.Tag, appCard) && view.Classes.Contains("app-card"));
        if (appBorder is not null)
        {
            lines.Add($"常备卡收起 bg={Describe(appBorder.Background)} " +
                      $"border={Describe(appBorder.BorderBrush)}");
            appCard.IsDescriptionExpanded = true;
            await Task.Delay(260);
            UpdateLayout();
            lines.Add($"常备卡展开 expanded-class={appBorder.Classes.Contains("expanded")} " +
                      $"border={Describe(appBorder.BorderBrush)}（期望 #FFD9A58E）");
            appCard.IsDescriptionExpanded = false;
        }

        // 分晓大卡：暖色常备底（PinnedBrush #F7F4EB）必须还在，
        // 刚才把 Background 从局部值挪进样式，这一条就是防回归的护栏。
        var pinned = ViewModel.GradeStripCard;
        var pinnedBorder = this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(view => ReferenceEquals(view.Tag, pinned) && view.Classes.Contains("app-card"));
        if (pinnedBorder is not null)
        {
            lines.Add($"分晓大卡背景={Describe(pinnedBorder.Background)}（期望 #FFF7F4EB）");
            pinned.IsDescriptionExpanded = true;
            await Task.Delay(260);
            UpdateLayout();
            lines.Add($"分晓大卡展开 border={Describe(pinnedBorder.BorderBrush)}（期望 #FFD9A58E）" +
                      $" bg={Describe(pinnedBorder.Background)}（期望 #FFF7F4EB）");
            pinned.IsDescriptionExpanded = false;
        }

        File.WriteAllLines(path, lines);
    }

    private static string Describe(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString() : "(none)";

    /// <summary>
    /// 设计走查：验证「系统减弱动效」下的展开路径——不做逐帧动画，直接落到终态。
    /// 通过 MotionPreference 的诊断注入复现，不需要真去改系统设置。
    /// </summary>
    public async Task RecordReducedMotionAsync(string path)
    {
        var lines = new List<string>();
        var tool = ViewModel.VisibleImageTools[0];
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        var reveal = card?.GetVisualDescendants().OfType<ExpandReveal>().FirstOrDefault();
        if (tool is null || card is null || reveal is null)
        {
            File.WriteAllText(path, "找不到工具卡");
            return;
        }

        MotionPreference.OverrideForDiagnostics(true);
        lines.Add($"注入后 ReduceMotion={MotionPreference.ReduceMotion}（期望 True）");

        tool.IsDescriptionExpanded = true;
        // 减弱动效下应当立刻到位：只等一帧，不等动画时长。
        await Task.Delay(20);
        lines.Add($"展开 20ms 后 progress={card.RevealProgress:0.00}（期望 1.00，即立刻落位）");
        lines.Add($"展开 20ms 后 reveal={reveal.Bounds.Height:0.#} " +
                  $"content={reveal.ContentHeight:0.#}（两者应相等）");

        tool.IsDescriptionExpanded = false;
        await Task.Delay(20);
        lines.Add($"收起 20ms 后 progress={card.RevealProgress:0.00}（期望 0.00）");

        MotionPreference.OverrideForDiagnostics(null);
        lines.Add($"恢复探测后 ReduceMotion={MotionPreference.ReduceMotion}");

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：连续展开 / 收起很多次，确认滚动定时器只有一个、视图模型状态不乱。
    /// 这条专门防「每次展开 new 一个 DispatcherTimer」的泄漏回归。
    /// </summary>
    public async Task RecordRepeatedToggleAsync(string path, int rounds)
    {
        var lines = new List<string>();
        var tool = ViewModel.VisibleImageTools[0];
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        if (tool is null || card is null)
        {
            File.WriteAllText(path, "找不到工具卡");
            return;
        }

        for (var i = 0; i < rounds; i++)
        {
            tool.IsDescriptionExpanded = true;
            BringCardIntoView(tool);
            await Task.Delay(12);
            tool.IsDescriptionExpanded = false;
            await Task.Delay(12);
        }

        await Task.Delay(400); // 让最后一轮滚动定时器跑完
        lines.Add($"连续切换 {rounds} 轮后 滚动定时器实例数={RevealScrollTimerCount}（期望 1）");
        lines.Add($"最终 expanded={tool.IsDescriptionExpanded}（期望 False）");
        lines.Add($"最终 progress={card.RevealProgress:0.00}（期望 0.00）");
        var finalReveal = card.GetVisualDescendants().OfType<ExpandReveal>().FirstOrDefault();
        lines.Add($"最终 reveal 高度={finalReveal?.Bounds.Height ?? -1:0.#}（期望 0）");

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：展开着改窗口宽度。说明区高度取决于文本换行，宽度一变行数就变；
    /// 这条确认 ExpandReveal 会重新量自然高度、不会把内容裁掉，双列也不会溢出。
    /// </summary>
    public async Task RecordExpandResizeAsync(string path, double newWidth)
    {
        var lines = new List<string>();
        var tool = ViewModel.VisibleImageTools[0];
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        var reveal = card?.GetVisualDescendants().OfType<ExpandReveal>().FirstOrDefault();
        if (tool is null || card is null || reveal is null)
        {
            File.WriteAllText(path, "找不到工具卡");
            return;
        }

        tool.IsDescriptionExpanded = true;
        await Task.Delay(400);
        UpdateLayout();
        var before = reveal.ContentHeight;
        lines.Add($"展开后 窗口={Width:0} 卡宽={card.Bounds.Width:0.#} " +
                  $"content={before:0.#} reveal={reveal.Bounds.Height:0.#} " +
                  $"clipped={(reveal.ContentHeight > reveal.Bounds.Height + 0.5 ? "YES" : "no")} " +
                  $"量算次数={reveal.MeasurePassCount}");

        Width = newWidth;
        await Task.Delay(500);
        UpdateLayout();
        var after = reveal.ContentHeight;
        lines.Add($"改宽到 {newWidth:0} 后 卡宽={card.Bounds.Width:0.#} " +
                  $"content={after:0.#} reveal={reveal.Bounds.Height:0.#} " +
                  $"clipped={(reveal.ContentHeight > reveal.Bounds.Height + 0.5 ? "YES" : "no")} " +
                  $"量算次数={reveal.MeasurePassCount}");
        // 宽度从 436 到 397 这段文字行数没变，高度自然不变，不能拿它当「机制失效」。
        // 真正要证明的是「窄到一定程度会重排」——见下面的宽度约束扫描。
        lines.Add($"该区间内容高度变化={before.ToString("0.#") != after.ToString("0.#")}" +
                  "（同一行数区间内不变属正常，窄到换行点才变）");

        var maxRight = 0.0;
        foreach (var each in this.GetVisualDescendants().OfType<ToolCardView>())
        {
            var pos = each.TranslatePoint(new Point(0, 0), this);
            if (pos is null) continue;
            maxRight = Math.Max(maxRight, pos.Value.X + each.Bounds.Width);
        }
        lines.Add($"所有卡片最右缘={maxRight:0.#} 窗口宽={Width:0} " +
                  $"溢出={(maxRight > Width + 0.5 ? "YES" : "no")}");

        // 窗口有 MinWidth=1120、卡片宽度又被 WrapPanel 覆盖，靠改窗口只能覆盖窄幅区间。
        // 直接给 ExpandReveal 施加宽度约束，验证「量算确实按传入宽度换行」这条机制。
        lines.Add("-- 对说明区直接施加宽度约束 --");
        var heights = new List<double>();
        foreach (var scanWidth in new[] { 460.0, 400.0, 340.0, 300.0, 240.0, 180.0 })
        {
            reveal.Measure(new Size(scanWidth, double.PositiveInfinity));
            lines.Add($"  约束宽={scanWidth,5:0.#} → 内容高={reveal.ContentHeight,6:0.#} " +
                      $"子内容宽={reveal.LastChildWidth,6:0.#}");
            heights.Add(reveal.ContentHeight);
        }
        lines.Add($"越窄越高={heights.Zip(heights.Skip(1)).All(pair => pair.Second >= pair.First)}" +
                  "（期望 True：宽度递减时内容高度不减，说明确实按宽度重新换行）");


        tool.IsDescriptionExpanded = false;
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：搜索过滤与展开状态的交互。展开着搜索、命中项被筛掉再清空搜索，
    /// 卡片必须回到原来的展开形态，不能留下「看不见但已展开」的残留。
    /// </summary>
    public async Task RecordSearchExpandAsync(string path)
    {
        var lines = new List<string>();
        var tool = ViewModel.VisibleImageTools[0];
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        if (card is null)
        {
            File.WriteAllText(path, "找不到工具卡");
            return;
        }

        tool.IsDescriptionExpanded = true;
        await Task.Delay(350);
        UpdateLayout();
        lines.Add($"搜索前 expanded={tool.IsDescriptionExpanded} " +
                  $"reveal={card.RevealProgress:0.00}（期望 True / 1.00）");

        // 搜一个不命中它的词：卡片应被筛掉，但展开状态不该被悄悄改掉
        ViewModel.SearchText = "PDF";
        await Task.Delay(250);
        UpdateLayout();
        var stillPresent = ViewModel.VisibleImageTools.Contains(tool);
        lines.Add($"搜「PDF」后该卡可见={stillPresent}（期望 False，图片工具被筛掉）");
        lines.Add($"被筛掉期间 expanded={tool.IsDescriptionExpanded}（期望仍为 True，不该被清）");

        // 清空搜索：卡片回来，且仍是展开的
        ViewModel.SearchText = string.Empty;
        await Task.Delay(350);
        UpdateLayout();
        var back = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        lines.Add($"清空搜索后 可见={ViewModel.VisibleImageTools.Contains(tool)} 期望 True");
        lines.Add($"回来后 expanded={tool.IsDescriptionExpanded} " +
                  $"reveal={back?.RevealProgress ?? -1:0.00}（期望 True / 1.00，还原展开形态）");

        tool.IsDescriptionExpanded = false;
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// 设计走查：证明「点标题」这条路径真的通——从标题 TextBlock 发起一次真实的
    /// 路由 PointerPressed，让事件冒泡到卡片根 Border 上的处理器，而不是直接调处理器。
    /// </summary>
    public void RecordTitleClickBubbling(string path)
    {
        var lines = new List<string>();
        var tool = ViewModel.VisibleImageTools[0];
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, tool));
        var title = card?.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(view => view.Classes.Contains("tool-card-title"));

        if (tool is null || card is null || title is null)
        {
            File.WriteAllText(path, "找不到工具卡标题");
            return;
        }

        lines.Add($"初始 expanded={tool.IsDescriptionExpanded}（期望 False）");

        // 以标题为源头发路由事件：若卡片没接住，说明「点标题」在真实点击里是死路。
        var args = new PointerPressedEventArgs(
            title, new Pointer(0, PointerType.Mouse, true), title, default,
            0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        title.RaiseEvent(args);

        lines.Add($"从标题冒泡一次 → expanded={tool.IsDescriptionExpanded}（期望 True）" +
                  $" handoff={args.Handled}（期望 True，说明卡片接住了）");

        // 再点一次标题应收起
        var second = new PointerPressedEventArgs(
            title, new Pointer(0, PointerType.Mouse, true), title, default,
            0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        title.RaiseEvent(second);
        lines.Add($"再点一次标题 → expanded={tool.IsDescriptionExpanded}（期望 False）");

        // 键盘路径：先真聚焦再按键，模拟真实 Tab + Enter，而不是凭空投递一个按键事件
        var innerBorder = card.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(view => view.Classes.Contains("tool-card"));
        var focused = innerBorder?.Focus() ?? false;
        lines.Add($"卡片可聚焦={card.Focusable} 聚焦成功={focused} " +
                  $"焦点在卡片内={innerBorder?.IsKeyboardFocusWithin}");

        if (focused)
        {
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            (innerBorder as InputElement)!.RaiseEvent(enter);
            lines.Add($"聚焦后按 Enter → expanded={tool.IsDescriptionExpanded}（期望 True） " +
                      $"handled={enter.Handled}");

            var space = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space };
            (innerBorder as InputElement)!.RaiseEvent(space);
            lines.Add($"聚焦后按空格 → expanded={tool.IsDescriptionExpanded}（期望 False） " +
                      $"handled={space.Handled}");
        }

        // 常备卡：点标题（大字）也要能展开
        var appCard = ViewModel.SeatGridCard;
        var appBorder = this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(view => ReferenceEquals(view.Tag, appCard) && view.Classes.Contains("app-card"));
        var appTitle = appBorder?.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(view => view.Text == "归位");
        if (appTitle is not null)
        {
            var appArgs = new PointerPressedEventArgs(
                appTitle, new Pointer(0, PointerType.Mouse, true), appTitle, default,
                0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                    PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None);
            appTitle.RaiseEvent(appArgs);
            lines.Add($"点常备卡标题 → expanded={appCard.IsDescriptionExpanded}（期望 True）" +
                      $" handoff={appArgs.Handled}");
            appCard.IsDescriptionExpanded = false;
        }

        File.WriteAllLines(path, lines);
    }

    private static int StyleCount(Control control) =>
        (Application.Current?.Styles.Count ?? 0) + control.Styles.Count +
        (control.Parent is Control parent ? parent.Styles.Count : 0);
    private async Task ExpandAndSampleAsync(
        string path, List<string> lines, ToolItemViewModel tool, ToolCardView card)
    {
        var clock = Stopwatch.StartNew();
        tool.IsDescriptionExpanded = true;

        var reveal = card.GetVisualDescendants().OfType<ExpandReveal>().FirstOrDefault();
        while (clock.Elapsed.TotalMilliseconds < 600)
        {
            lines.Add($"{clock.Elapsed.TotalMilliseconds,6:0}\t{card.RevealProgress,8:0.000}\t" +
                      $"{reveal?.Bounds.Height ?? -1,13:0.#}\t{card.Bounds.Height,11:0.#}");
            await Task.Delay(30);
        }

        // 再采样一次收起：验证同一套缓动可以反向跑，且最终落回 0。
        lines.Add("-- 收起 --");
        var collapseClock = Stopwatch.StartNew();
        tool.IsDescriptionExpanded = false;
        while (collapseClock.Elapsed.TotalMilliseconds < 600)
        {
            lines.Add($"{collapseClock.Elapsed.TotalMilliseconds,6:0}\t{card.RevealProgress,8:0.000}\t" +
                      $"{reveal?.Bounds.Height ?? -1,13:0.#}\t{card.Bounds.Height,11:0.#}");
            await Task.Delay(30);
        }

        // 再验证一次「动画没落定就反向」：中途收起，进度应从当前值往回收而不是跳回 1。
        lines.Add("-- 中途反向 --");
        tool.IsDescriptionExpanded = true;
        await Task.Delay(80);
        var midway = card.RevealProgress;
        tool.IsDescriptionExpanded = false;
        await Task.Delay(60);
        lines.Add($"中途展开进度={midway:0.000} 反向 60ms 后={card.RevealProgress:0.000}" +
                  "（期望后者更小且仍 > 0）");
        await Task.Delay(400);
        lines.Add($"反向最终进度={card.RevealProgress:0.000}（期望 0）");
    }
    /// <summary>
    /// 设计走查：逐张核对**实际渲染出来的**工具卡与常备卡都绑到了自己的说明。
    /// 目录里有数据不等于界面上绑对了——模板写错 DataContext 时卡片会静默空白，
    /// 所以这里比对每张卡的 Tag/DataContext 与它内部 ToolDescriptionView 拿到的对象。
    /// </summary>
    public void RecordDescriptionBinding(string path)
    {
        var lines = new List<string>();
        var missing = 0;

        foreach (var card in this.GetVisualDescendants().OfType<ToolCardView>())
        {
            if (card.DataContext is not ToolItemViewModel tool) continue;
            var shown = card.GetVisualDescendants().OfType<ToolDescriptionView>()
                .FirstOrDefault()?.DataContext as ToolDescription;
            var ok = shown is not null && ReferenceEquals(shown, tool.Detail) &&
                     ReferenceEquals(shown, ToolDescriptionCatalog.ForTool(tool.Id));
            if (!ok) missing++;
            lines.Add($"tool {tool.Id,-16} 绑定正确={ok} " +
                      $"说明首句=[{(shown?.Tagline ?? "（空）")[..Math.Min(14, (shown?.Tagline ?? "（空）").Length)]}]");
        }

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            if (border.Tag is not AppCardViewModel card || !border.Classes.Contains("app-card")) continue;
            var shown = border.GetVisualDescendants().OfType<ToolDescriptionView>()
                .FirstOrDefault()?.DataContext as ToolDescription;
            var ok = shown is not null && ReferenceEquals(shown, card.Detail) &&
                     ReferenceEquals(shown, ToolDescriptionCatalog.ForApp(card.Key));
            if (!ok) missing++;
            lines.Add($"app  {card.Key,-16} 绑定正确={ok} " +
                      $"说明首句=[{(shown?.Tagline ?? "（空）")[..Math.Min(14, (shown?.Tagline ?? "（空）").Length)]}]");
        }

        lines.Add($"合计绑定错误={missing}（期望 0）");
        File.WriteAllLines(path, lines);
    }
}
