using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TeachCraft.App.ViewModels;

namespace TeachCraft.App.Views;

/// <summary>
/// 工具卡：点卡片或点标题展开说明，右上「打开」进抽屉。
/// 打开动作以事件交给 MainWindow，卡片本身不碰窗口逻辑。
/// </summary>
public partial class ToolCardView : UserControl
{
    /// <summary>请求打开该工具的抽屉面板（用 RoutedEventArgs 与窗口侧的处理器签名对齐）。</summary>
    public event EventHandler<RoutedEventArgs>? OpenRequested;

    /// <summary>展开后请求把卡片滚进可见区；窗口侧决定滚多少，卡片自己不动滚动条。</summary>
    public event EventHandler? Expanded;

    public ToolCardView()
    {
        InitializeComponent();
    }

    private ToolItemViewModel? Tool => DataContext as ToolItemViewModel;

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        // 点在「打开」按钮或展开区里的按钮上时，交给按钮自己的 Click，
        // 不要把「打开面板」误判成「切换说明」。
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        Tool?.ToggleDescription();
        if (Tool is { IsDescriptionExpanded: true }) Expanded?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;

        Tool?.ToggleDescription();
        if (Tool is { IsDescriptionExpanded: true }) Expanded?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenRequested?.Invoke(this, new RoutedEventArgs());
    }

    private void OnCollapseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Tool is { IsDescriptionExpanded: true } tool) tool.ToggleDescription();
    }

    /// <summary>走查用：模拟点一次「打开」，验证事件接线与窗口侧处理。</summary>
    public void SimulateOpenForShot() => OnOpenClick(this, new RoutedEventArgs());

    /// <summary>走查用：模拟点卡片本体（非按钮区域）。</summary>
    public void SimulateCardClickForShot()
    {
        var point = new PointerPressedEventArgs(
            this, new Pointer(0, PointerType.Mouse, true), this, default,
            0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        OnCardPressed(this, point);
    }

    /// <summary>走查用：模拟点在卡内按钮上，应当被按钮守卫拦下、不切换展开。</summary>
    public void SimulateInnerButtonClickForShot()
    {
        var button = this.GetVisualDescendants().OfType<Button>().FirstOrDefault();
        if (button is null) return;

        var point = new PointerPressedEventArgs(
            button, new Pointer(0, PointerType.Mouse, true), button, default,
            0, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
        OnCardPressed(button, point);
    }

    /// <summary>走查用：卡片里那个展开容器的当前进度（0–1）。</summary>
    public double RevealProgress =>
        this.GetVisualDescendants().OfType<TeachCraft.App.Controls.ExpandReveal>().FirstOrDefault()?.Progress ?? -1;
}
