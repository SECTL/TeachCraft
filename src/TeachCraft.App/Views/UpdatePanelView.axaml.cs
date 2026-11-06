using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TeachCraft.App.Views;

/// <summary>
/// 检查更新页。沿用工具／项目面板的抽屉形态：非模态、可随时关闭。
/// 本身不做任何业务，只把用户动作抛给 MainWindow（它持有 ViewModel 与进程控制权）。
/// </summary>
public partial class UpdatePanelView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? CheckRequested;
    public event EventHandler? InstallRequested;
    public event EventHandler? DismissRequested;

    public UpdatePanelView()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnCheck(object? sender, RoutedEventArgs e) => CheckRequested?.Invoke(this, EventArgs.Empty);
    private void OnInstall(object? sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);
    private void OnDismiss(object? sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);
}
