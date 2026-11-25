using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TeachCraft.App.Views;

/// <summary>
/// 账号页。本身不做业务，只把用户动作抛给宿主（主窗口或独立窗口），
/// 由宿主调用同一个 AccountViewModel——抽屉与弹窗因此不会有两套行为。
/// </summary>
public partial class AccountPanelView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? SignInRequested;
    public event EventHandler? SignOutRequested;
    public event EventHandler? RefreshRostersRequested;
    public event EventHandler<string>? DeleteRosterRequested;
    public event EventHandler? ConsentAcceptedRequested;
    public event EventHandler? JumpToConsentRequested;

    public AccountPanelView()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnSignIn(object? sender, RoutedEventArgs e) => SignInRequested?.Invoke(this, EventArgs.Empty);
    private void OnSignOut(object? sender, RoutedEventArgs e) => SignOutRequested?.Invoke(this, EventArgs.Empty);
    private void OnRefreshRosters(object? sender, RoutedEventArgs e) => RefreshRostersRequested?.Invoke(this, EventArgs.Empty);
    private void OnAcceptConsent(object? sender, RoutedEventArgs e) => ConsentAcceptedRequested?.Invoke(this, EventArgs.Empty);
    private void OnJumpToConsent(object? sender, RoutedEventArgs e) => JumpToConsentRequested?.Invoke(this, EventArgs.Empty);

    public void RevealConsent()
    {
        ConsentExpander.IsExpanded = true;
        ConsentExpander.BringIntoView();
    }

    private void OnDeleteRoster(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string id)
            DeleteRosterRequested?.Invoke(this, id);
    }
}
