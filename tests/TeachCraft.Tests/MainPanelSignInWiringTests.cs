using System.Reflection;
using TeachCraft.App.Cloud;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

public sealed class MainPanelSignInWiringTests
{
    private const string SignInFieldName = "_signIn";

    private sealed class FakeAccountService : ICloudAccountService
    {
        public CloudAccount? Current => null;
        public bool IsSignedIn => false;
        public string? LastSignOutNotice => null;
        public event EventHandler? StateChanged;
        public int CompleteCalls { get; private set; }

        public CloudSignInRequest CreateSignInRequest() => new(
            new Uri("https://example.invalid/oauth/authorize?state=abc"), "abc", "verifier",
            "http://localhost:53140/callback", DateTimeOffset.UtcNow);

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.FromResult(new CloudAccount("u1", "示例教师", Array.Empty<string>(),
                SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow));
        }

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("fake");

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public int OpenCalls { get; private set; }

        public Task OpenAsync(Uri url, CancellationToken ct)
        {
            OpenCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiver : IOAuthCallbackReceiver
    {
        public OAuthCallback Callback { get; set; } = new("code-1", "abc", null, null);
        public int WaitCalls { get; private set; }

        public Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
        {
            WaitCalls++;
            return Task.FromResult(Callback);
        }
    }

    private static CloudRuntime ConfiguredRuntime(ICloudAccountService account) =>
        new(true, account, null, null, new NoopCloudSyncSettingsStore(), CloudSyncSettings.Default);

    private static DesktopAccountSignInFlow? ReadSignIn(AccountViewModel vm)
    {
        var field = typeof(AccountViewModel).GetField(SignInFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null,
            $"AccountViewModel 的私有字段 {SignInFieldName} 不存在（可能已改名），无法验证主面板登录接线。");
        return field!.GetValue(vm) as DesktopAccountSignInFlow;
    }

    [Fact]
    public void ConfiguredRuntime_InjectsSignInFlowIntoAccount()
    {
        var vm = new MainViewModel(cloudRuntime: ConfiguredRuntime(new FakeAccountService()));

        Assert.NotNull(ReadSignIn(vm.Account));
    }

    [Fact]
    public void DisabledRuntime_HasNoSignInFlow()
    {
        var vm = new MainViewModel();

        Assert.Null(ReadSignIn(vm.Account));
    }

    [Fact]
    public async Task MainPanelLogin_DoesNotReportNotConfiguredWhenConsentAccepted()
    {
        var account = new FakeAccountService();
        var vm = new MainViewModel(cloudRuntime: ConfiguredRuntime(account));
        vm.Account.AcceptConsent();

        Assert.NotNull(ReadSignIn(vm.Account));
        Assert.True(vm.Account.IsConfigured);
        Assert.True(vm.Account.ConsentAccepted);
        Assert.Same(account, vm.Account.Runtime.Account);

        var browser = new FakeBrowser();
        var receiver = new FakeReceiver();
        var direct = new AccountViewModel(vm.Account.Runtime,
            new DesktopAccountSignInFlow(account, browser, receiver));
        direct.AcceptConsent();
        await direct.BeginSignInAsync(null!);

        Assert.NotEqual(AccountViewModel.NotConfiguredStatus, direct.StatusText);
        Assert.Equal(1, browser.OpenCalls);
        Assert.Equal(1, receiver.WaitCalls);
    }

    [Fact]
    public void MainWindow_AccountPanelBindsToAccountViewModel()
    {
        // 面板若继承 MainWindow 的 MainViewModel，AccountText/CloudEnabled 等属性不存在，
        // 账号卡会整块空白（截图里"状态/账号"两行没有值）。显式绑定是硬约束。
        var xaml = RepoRoot.Read(Path.Combine("src", "TeachCraft.App", "Views", "MainWindow.axaml"));
        var marker = "AccountPanelView x:Name=\"AccountPanel\"";
        var index = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "找不到 AccountPanelView 声明");
        var declaration = xaml.Substring(index, Math.Min(400, xaml.Length - index));
        Assert.Contains("DataContext=\"{Binding Account}\"", declaration);
    }

    [Fact]
    public async Task DescribeCallbackError_MapsStateMismatchChineseMessage()
    {
        var account = new FakeAccountService();
        var receiver = new FakeReceiver
        {
            Callback = new OAuthCallback(null, "abc", CloudErrorText.StateMismatch, null),
        };

        var outcome = await new DesktopAccountSignInFlow(account, new FakeBrowser(), receiver)
            .SignInAsync(null!, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(CloudErrorText.StateMismatch, outcome.Message);
        Assert.Equal(0, account.CompleteCalls);
    }
}
