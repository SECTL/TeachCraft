using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using TeachCraft.App.Cloud;
using TeachCraft.App.ViewModels;

namespace TeachCraft.App.Views;

/// <summary>
/// 账号页的独立窗口形态。与抽屉里的 AccountPanelView 共用同一个 ViewModel 实例，
/// 所以两处看到的登录态、同意状态与名单列表永远一致。
/// 同时只允许一个实例：再开先关旧的，避免两个窗口抢同一份状态。
/// </summary>
public partial class AccountWindow : Window
{
    private static AccountWindow? _current;

    public AccountWindow() : this(new AccountViewModel(CloudRuntime.Disabled)) { }

    public AccountWindow(AccountViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Panel.CloseRequested += (_, _) => Close();
        Panel.SignInRequested += (_, _) => _ = viewModel.BeginSignInAsync(this);
        Panel.SignOutRequested += (_, _) => _ = viewModel.SignOutAsync();
        Panel.RefreshRostersRequested += (_, _) => _ = viewModel.RefreshRostersAsync();
        Panel.DeleteRosterRequested += (_, id) => _ = viewModel.DeleteRosterAsync(id);
        Panel.ConsentAcceptedRequested += (_, _) => viewModel.AcceptConsent();
        // 与 MainWindow 的抽屉形态一致：「查看并同意字段说明」必须展开同意区，
        // 否则独立窗口（归位 / 辑册桥接、--account-shot）里这个按钮点了没反应。
        Panel.JumpToConsentRequested += (_, _) => Panel.RevealConsent();

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    /// <summary>模态打开。调用方 await 这个 Task 直到窗口关闭。</summary>
    public static Task ShowModalAsync(AccountViewModel viewModel, Window owner)
    {
        _current?.Close();
        var window = new AccountWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _current = window;
        return window.ShowDialog(owner);
    }

    /// <summary>走查用：非模态打开，便于截图后由调用方主动关闭。</summary>
    public static AccountWindow OpenForCapture(AccountViewModel viewModel, Window owner)
    {
        _current?.Close();
        var window = new AccountWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _current = window;
        window.Show(owner);
        return window;
    }
}
