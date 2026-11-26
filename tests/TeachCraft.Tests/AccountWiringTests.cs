using TeachCraft.App.Cloud;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 账号页接线：打开面板只是摆出本机状态，绝不能顺手登录、开浏览器或列云端名单。
/// 注入一个「任何方法被调用就抛」的账号服务，一旦接线错了测试立刻红。
/// </summary>
public sealed class AccountWiringTests
{
    private sealed class ThrowingAccountService : ICloudAccountService
    {
        public int Calls { get; private set; }
        public bool IsSignedIn => false;
        public string? LastSignOutNotice => null;
        public CloudAccount? Current => null;
        public event EventHandler? StateChanged { add { } remove { } }

        private void Called() => Calls++;

        public CloudSignInRequest CreateSignInRequest()
        {
            Called();
            throw new InvalidOperationException("打开账号页不该发起登录");
        }

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
        {
            Called();
            throw new InvalidOperationException("打开账号页不该换 token");
        }

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
        {
            Called();
            throw new InvalidOperationException("打开账号页不该取令牌");
        }

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            Called();
            throw new InvalidOperationException("打开账号页不该退出登录");
        }
    }

    [Fact]
    public void OpenAccountPanel_DoesNotGoOnline()
    {
        var account = new ThrowingAccountService();
        var runtime = new CloudRuntime(true, account, null, null,
            new NoopCloudSyncSettingsStore(), CloudSyncSettings.Default);
        var vm = new MainViewModel(cloudRuntime: runtime);

        vm.OpenAccountPanel();

        Assert.True(vm.IsAccountPanelOpen);
        Assert.Equal(0, account.Calls);
        Assert.False(vm.Account.IsBusy);
    }

    [Fact]
    public void OpenAccountPanel_WithDisabledRuntime_DoesNotGoOnline()
    {
        var vm = new MainViewModel(cloudRuntime: CloudRuntime.Disabled);

        vm.OpenAccountPanel();

        Assert.True(vm.IsAccountPanelOpen);
        Assert.False(vm.Account.IsConfigured);
        Assert.Equal(AccountViewModel.NotConfiguredStatus, vm.Account.StatusText);
    }

    [Fact]
    public void OpenAccountPanel_IsMutuallyExclusiveWithOtherPanels()
    {
        var vm = new MainViewModel();

        vm.OpenUpdatePanel();
        Assert.True(vm.IsUpdatePanelOpen);

        vm.OpenAccountPanel();

        Assert.True(vm.IsAccountPanelOpen);
        Assert.False(vm.IsUpdatePanelOpen);
        Assert.False(vm.IsProjectPanelOpen);
        Assert.False(vm.IsToolPanelOpen);
        Assert.True(vm.IsPanelOpen);

        vm.OpenProject();
        Assert.False(vm.IsAccountPanelOpen);
        Assert.True(vm.IsProjectPanelOpen);

        vm.OpenAccountPanel();
        vm.ClosePanels();
        Assert.False(vm.IsAccountPanelOpen);
        Assert.False(vm.IsPanelOpen);
    }

    [Fact]
    public void DefaultViewModel_HasUnconfiguredAccount()
    {
        var vm = new MainViewModel();

        Assert.False(vm.Account.IsConfigured);
        Assert.False(vm.Account.IsSignedIn);
        Assert.False(vm.Account.CloudEnabled);
        Assert.Empty(vm.Account.Rosters);
    }

    [Fact]
    public void BothHosts_WireJumpToConsentToRevealConsent()
    {
        // 账号页有两个宿主：主窗口抽屉与独立 AccountWindow（归位 / 辑册桥接、--account-shot）。
        // 「查看并同意字段说明」按钮抛出的事件必须在两处都接到 RevealConsent，否则独立窗口里点了没反应。
        var mainWindow = RepoRoot.Read("src/TeachCraft.App/Views/MainWindow.axaml.cs");
        var accountWindow = RepoRoot.Read("src/TeachCraft.App/Views/AccountWindow.axaml.cs");

        Assert.Contains("AccountPanel.JumpToConsentRequested", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AccountPanel.RevealConsent()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Panel.JumpToConsentRequested", accountWindow, StringComparison.Ordinal);
        Assert.Contains("Panel.RevealConsent()", accountWindow, StringComparison.Ordinal);
    }
}
