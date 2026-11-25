using Avalonia.Controls;
using TeachCraft.App.Cloud;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 账号页状态机。所有服务都是内存假件：登录、列名单、删除都不碰网络与本机账号目录。
/// </summary>
public sealed class AccountViewModelTests
{
    private sealed class FakeSettingsStore : ICloudSyncSettingsStore
    {
        public CloudSyncSettings Settings { get; private set; } = CloudSyncSettings.Default;
        public int SaveCalls { get; private set; }
        public bool SaveSucceeds { get; set; } = true;
        public string? LastError { get; set; }
        public CloudSyncSettings Load() { LastError = null; return Settings; }
        public void Save(CloudSyncSettings settings) { SaveCalls++; Settings = settings; }
        public bool TrySave(CloudSyncSettings settings)
        {
            if (!SaveSucceeds)
            {
                LastError = "无法写入本机设置文件（IOException）";
                return false;
            }
            SaveCalls++;
            Settings = settings;
            LastError = null;
            return true;
        }
        public string Path => "memory";
    }

    private sealed class FakeAccountService : ICloudAccountService
    {
        public CloudAccount? Current { get; private set; }
        public bool IsSignedIn => Current is not null;
        public event EventHandler? StateChanged;

        public int CreateCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int SignOutCalls { get; private set; }
        public int TokenCalls { get; private set; }
        public string? LastSignOutNotice { get; set; }

        public CloudSignInRequest CreateSignInRequest()
        {
            CreateCalls++;
            return new CloudSignInRequest(
                new Uri("https://example.invalid/oauth/authorize"), "state-1", "verifier-1",
                "http://localhost:53140/callback", DateTimeOffset.UtcNow);
        }

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
        {
            CompleteCalls++;
            SignIn("示例教师");
            return Task.FromResult(Current!);
        }

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
        {
            TokenCalls++;
            return Task.FromResult("fake-token");
        }

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            SignOutCalls++;
            Current = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void SignIn(string displayName)
        {
            Current = new CloudAccount("user-1", displayName, Array.Empty<string>(),
                SectlPlatformDefaults.ClientId, DateTimeOffset.UtcNow);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>模拟 invalid_grant / 401：服务已登出并广播，但不走 SignOutAsync。</summary>
        public void Invalidate()
        {
            Current = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeRosterSource : ISharedRosterSource
    {
        public IReadOnlyList<SharedRosterInfo> Items { get; set; } = Array.Empty<SharedRosterInfo>();
        public int ListCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public string? LastDeletedId { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Gate { get; set; }

        public async Task<IReadOnlyList<SharedRosterInfo>> ListAsync(CancellationToken ct)
        {
            ListCalls++;
            LastToken = ct;
            if (Gate)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return Items;
        }

        public Task<SharedRosterDocument?> GetAsync(string rosterId, CancellationToken ct)
            => Task.FromResult<SharedRosterDocument?>(null);

        public Task<SharedRosterInfo> SaveAsync(
            SharedRosterDocument roster, RosterFieldSelection selection,
            string? existingId, int expectedVersion, CancellationToken ct)
            => throw new NotSupportedException();

        public Task DeleteAsync(string rosterId, CancellationToken ct)
        {
            DeleteCalls++;
            LastDeletedId = rosterId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public int OpenCalls { get; private set; }
        public Uri? LastUrl { get; private set; }
        public Task OpenAsync(Uri url, CancellationToken ct)
        {
            OpenCalls++;
            LastUrl = url;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCallbackReceiver : IOAuthCallbackReceiver
    {
        public OAuthCallback Callback { get; set; } =
            new("code-1", "state-1", null, null);

        public Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
            => Task.FromResult(Callback);
    }

    private sealed class BlockingCallbackReceiver : IOAuthCallbackReceiver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new OAuthCallback("code-1", "state-1", null, null);
        }
    }

    private static CloudRuntime Configured(
        FakeAccountService account,
        FakeSettingsStore settings,
        FakeRosterSource? rosters = null)
        => new(true, account, null, rosters, settings, settings.Load());

    private static AccountViewModel CreateVm(
        FakeAccountService account,
        FakeSettingsStore settings,
        FakeRosterSource? rosters = null,
        FakeBrowserLauncher? browser = null,
        IOAuthCallbackReceiver? receiver = null)
    {
        var runtime = Configured(account, settings, rosters);
        var flow = new DesktopAccountSignInFlow(account, browser ?? new FakeBrowserLauncher(),
            receiver ?? new FakeCallbackReceiver());
        return new AccountViewModel(runtime, flow);
    }

    [Fact]
    public async Task Unconfigured_ShowsNotConfiguredStatus_AndNeverTouchesServices()
    {
        var vm = new AccountViewModel(CloudRuntime.Disabled);

        Assert.False(vm.IsConfigured);
        Assert.False(vm.IsSignedIn);
        Assert.Equal(AccountViewModel.NotConfiguredStatus, vm.StatusText);
        Assert.Empty(vm.Rosters);

        vm.Refresh();
        await vm.RefreshRostersAsync();
        vm.SetCloudEnabled(true);
        vm.AcceptConsent();

        Assert.False(vm.CloudEnabled);
        Assert.False(vm.ConsentAccepted);
        Assert.Equal(AccountViewModel.NotConfiguredStatus, vm.StatusText);
    }

    [Fact]
    public void Refresh_ReadsSettingsAndAccountState()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        account.SignIn("示例教师");

        var vm = CreateVm(account, settings);

        Assert.True(vm.IsSignedIn);
        Assert.True(vm.CloudEnabled);
        Assert.True(vm.ConsentAccepted);
        Assert.Equal("示例教师", vm.AccountText);
        Assert.Equal("已登录：示例教师 · 云同步已开启（仅手动上传 / 拉取）", vm.StatusText);
    }

    [Fact]
    public void SetCloudEnabled_PersistsAndUpdatesStatus()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);

        vm.SetCloudEnabled(true);
        Assert.True(settings.Settings.Enabled);
        Assert.Equal("已登录：示例教师 · 云同步已开启（仅手动上传 / 拉取）", vm.StatusText);

        vm.SetCloudEnabled(false);
        Assert.False(settings.Settings.Enabled);
        Assert.Equal("已登录：示例教师 · 云同步已关闭", vm.StatusText);
    }

    [Fact]
    public void ToggleWritesThrough_ToSettingsAndNotifier()
    {
        var dir = Path.Combine(Path.GetTempPath(), "teachcraft-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new FileCloudSyncSettingsStore(dir);
            var account = new FakeAccountService();
            account.SignIn("示例教师");
            var runtime = new CloudRuntime(true, account, null, null, settings, settings.Load());
            var vm = new AccountViewModel(runtime);

            var notifierCalls = 0;
            runtime.Notifier.StateChanged += (_, _) => notifierCalls++;

            vm.CloudEnabled = true;

            Assert.True(settings.Load().Enabled);
            Assert.Contains("已开启", vm.StatusText, StringComparison.Ordinal);
            Assert.Equal(1, notifierCalls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Refresh_DoesNotRewriteSettings()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);

        var before = settings.SaveCalls;
        vm.Refresh();

        Assert.Equal(before, settings.SaveCalls);
        Assert.True(vm.CloudEnabled);
    }

    [Fact]
    public async Task SetCloudEnabledFalse_CancelsInFlightRosterRead()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        var rosters = new FakeRosterSource { Gate = true };
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings, rosters);
        vm.SetCloudEnabled(true);

        var pending = vm.RefreshRostersAsync();
        await rosters.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.SetCloudEnabled(false);

        Assert.True(rosters.LastToken.IsCancellationRequested);
        rosters.Release.TrySetResult();
        await pending;
    }

    [Fact]
    public async Task BeginSignIn_RequiresConsent_BeforeOpeningBrowser()
    {
        var account = new FakeAccountService();
        var browser = new FakeBrowserLauncher();
        var vm = CreateVm(account, new FakeSettingsStore(), browser: browser);

        await vm.BeginSignInAsync(null!);

        Assert.Equal(0, browser.OpenCalls);
        Assert.Equal(0, account.CreateCalls);
        Assert.Equal(CloudErrorText.ConsentRequiredDetailed, vm.StatusText);

        vm.AcceptConsent();
        await vm.BeginSignInAsync(null!);

        Assert.Equal(1, browser.OpenCalls);
        Assert.Equal(1, account.CreateCalls);
    }

    [Fact]
    public async Task BeginSignIn_CompletesAndRefreshesAccountState()
    {
        var account = new FakeAccountService();
        var browser = new FakeBrowserLauncher();
        var vm = CreateVm(account, new FakeSettingsStore(), browser: browser);
        vm.AcceptConsent();

        await vm.BeginSignInAsync(null!);

        Assert.Equal(1, browser.OpenCalls);
        Assert.Equal(1, account.CompleteCalls);
        Assert.True(vm.IsSignedIn);
        Assert.Equal("示例教师", vm.AccountText);
        Assert.Contains("登录成功", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeginSignIn_ShowsWaitingBrowserStatusWhileFlowPending()
    {
        var account = new FakeAccountService();
        var receiver = new BlockingCallbackReceiver();
        var vm = CreateVm(account, new FakeSettingsStore(), receiver: receiver);
        vm.AcceptConsent();

        var statusSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountViewModel.StatusText) &&
                vm.StatusText == CloudErrorText.SignInWaitingBrowser)
                statusSeen.TrySetResult();
        };

        var pending = vm.BeginSignInAsync(null!);
        await statusSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CloudErrorText.SignInWaitingBrowser, vm.StatusText);
        Assert.True(vm.IsBusy);

        receiver.Release.TrySetResult();
        await pending;

        Assert.Contains("登录成功", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UnacceptedConsent_ShowsDetailedPromptAndShowConsentPromptTrue()
    {
        var vm = CreateVm(new FakeAccountService(), new FakeSettingsStore());

        Assert.False(vm.ConsentAccepted);
        Assert.True(vm.ShowConsentPrompt);

        await vm.BeginSignInAsync(null!);

        Assert.Equal(CloudErrorText.ConsentRequiredDetailed, vm.StatusText);
    }

    [Fact]
    public void AcceptConsent_ClearsShowConsentPrompt()
    {
        var vm = CreateVm(new FakeAccountService(), new FakeSettingsStore());
        Assert.True(vm.ShowConsentPrompt);

        vm.AcceptConsent();

        Assert.True(vm.ConsentAccepted);
        Assert.False(vm.ShowConsentPrompt);
    }

    [Fact]
    public async Task SignOut_ClearsAccountRostersAndCloudEnabled()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        account.SignIn("示例教师");
        var rosters = new FakeRosterSource
        {
            Items = new[] { new SharedRosterInfo("r1", "八年级1班名单", 1, DateTimeOffset.UtcNow, 42, 100, RosterFieldPresence.None) },
        };
        var vm = CreateVm(account, settings, rosters);
        await vm.RefreshRostersAsync();
        Assert.True(vm.HasRosters);

        await vm.SignOutAsync();

        Assert.Equal(1, account.SignOutCalls);
        Assert.False(vm.IsSignedIn);
        Assert.False(vm.CloudEnabled);
        Assert.False(settings.Settings.Enabled);
        Assert.Empty(vm.Rosters);
        Assert.False(vm.HasRosters);
    }

    [Fact]
    public async Task RefreshRosters_WithNoSource_IsEmptyAndMakesNoCalls()
    {
        var vm = CreateVm(new FakeAccountService(), new FakeSettingsStore());

        await vm.RefreshRostersAsync();

        Assert.Empty(vm.Rosters);
        Assert.False(vm.HasRosters);
    }

    [Fact]
    public async Task RefreshRosters_MapsRowsWithRequiredFields()
    {
        var rosters = new FakeRosterSource
        {
            Items = new[]
            {
                new SharedRosterInfo("r1", "八年级1班名单", 3, DateTimeOffset.UtcNow, 42, 100,
                    new RosterFieldPresence(true, false, false, false, false)),
            },
        };
        var account = new FakeAccountService();
        account.SignIn("示例教师");
        var vm = CreateVm(account, new FakeSettingsStore(), rosters);
        vm.SetCloudEnabled(true);

        await vm.RefreshRostersAsync();

        var row = Assert.Single(vm.Rosters);
        Assert.Equal("r1", row.Id);
        Assert.Equal("八年级1班名单", row.Name);
        Assert.Equal(3, row.Version);
        Assert.Equal(42, row.StudentCount);
        Assert.Contains("姓名、性别、学号", row.FieldsText, StringComparison.Ordinal);
        Assert.Contains("身高", row.FieldsText, StringComparison.Ordinal);
        Assert.DoesNotContain("视力", row.FieldsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRoster_CallsSourceAndReloadsList()
    {
        var rosters = new FakeRosterSource
        {
            Items = new[] { new SharedRosterInfo("r1", "八年级1班名单", 1, DateTimeOffset.UtcNow, 42, 100, RosterFieldPresence.None) },
        };
        var account = new FakeAccountService();
        account.SignIn("示例教师");
        var vm = CreateVm(account, new FakeSettingsStore(), rosters);
        vm.SetCloudEnabled(true);
        await vm.RefreshRostersAsync();

        await vm.DeleteRosterAsync("r1");

        Assert.Equal(1, rosters.DeleteCalls);
        Assert.Equal("r1", rosters.LastDeletedId);
        Assert.Equal(2, rosters.ListCalls);
        Assert.Contains("已删除", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoViewModels_AcceptConsentAndToggle_SyncWithoutOverwritingSettings()
    {
        var account = new FakeAccountService();
        account.SignIn("示例教师");
        var settings = new FakeSettingsStore();
        var runtime = Configured(account, settings);
        var primary = new AccountViewModel(runtime);
        var modal = new AccountViewModel(runtime);

        modal.AcceptConsent();

        Assert.True(primary.ConsentAccepted);
        Assert.False(primary.ShowConsentPrompt);

        primary.SetCloudEnabled(true);

        Assert.True(modal.CloudEnabled);
        Assert.True(settings.Settings.Enabled);
        Assert.Equal(AccountViewModel.CurrentConsentVersion, settings.Settings.ConsentVersion);
    }

    [Fact]
    public async Task RefreshRosters_FormatsUpdatedTimeInLocalTimezone()
    {
        var utcTime = new DateTimeOffset(2026, 11, 24, 1, 15, 0, TimeSpan.Zero);
        var rosters = new FakeRosterSource
        {
            Items = new[]
            {
                new SharedRosterInfo("r1", "八年级1班名单", 1, utcTime, 42, 100, RosterFieldPresence.None),
            },
        };
        var account = new FakeAccountService();
        account.SignIn("示例教师");
        var vm = CreateVm(account, new FakeSettingsStore(), rosters);
        vm.SetCloudEnabled(true);

        await vm.RefreshRostersAsync();

        var row = Assert.Single(vm.Rosters);
        Assert.Equal(utcTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), row.UpdatedText);
    }

    [Fact]
    public async Task SignOut_AppendsRemoteRevokeNoticeToStatus()
    {
        const string notice = "已退出本机登录；云端授权可能仍然有效，请到 SECTL 控制台撤销。";
        var account = new FakeAccountService { LastSignOutNotice = notice };
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);

        await vm.SignOutAsync();

        Assert.Equal("已退出登录。 " + notice, vm.StatusText);
    }

    [Fact]
    public async Task SignOut_ClearsCloudDocumentIdentity_ButKeepsConsent()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(
            Enabled: true, ConsentVersion: 1,
            LastDocumentId: "synthetic-doc-id", LastDocumentRevision: 7,
            LastRosterHeight: true));
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);

        await vm.SignOutAsync();

        var saved = settings.Settings;
        Assert.False(saved.Enabled);
        Assert.Null(saved.LastDocumentId);
        Assert.Equal(0, saved.LastDocumentRevision);
        Assert.Equal(1, saved.ConsentVersion);
        Assert.True(saved.LastRosterHeight);
        Assert.True(vm.ConsentAccepted);
    }

    [Fact]
    public void AccountStateChanged_WhenServiceSignedOut_ResetsEnabledAndCanAccessCloud()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);
        Assert.True(vm.CloudEnabled);
        Assert.True(vm.CanAccessCloud);

        // 模拟 invalid_grant：服务已登出并广播，但设置里 Enabled 还是 true。
        account.Invalidate();

        Assert.False(settings.Settings.Enabled);
        Assert.False(vm.CloudEnabled);
        Assert.False(vm.IsSignedIn);
        Assert.False(vm.CanAccessCloud);
        Assert.Equal(AccountViewModel.SignedOutStatus, vm.StatusText);
    }

    [Fact]
    public void CanAccessCloud_RequiresConfiguredSignedInAndEnabled()
    {
        var account = new FakeAccountService();
        var vm = CreateVm(account, new FakeSettingsStore());
        Assert.False(vm.CanAccessCloud);

        account.SignIn("示例教师");
        Assert.False(vm.CanAccessCloud);

        vm.SetCloudEnabled(true);
        Assert.True(vm.CanAccessCloud);

        vm.SetCloudEnabled(false);
        Assert.False(vm.CanAccessCloud);
    }

    [Fact]
    public async Task RefreshRosters_WhenSignedOut_MakesNoCalls()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore();
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        var rosters = new FakeRosterSource();
        var vm = CreateVm(account, settings, rosters);

        await vm.RefreshRostersAsync();

        Assert.Equal(0, rosters.ListCalls);
        Assert.Equal(AccountViewModel.SignedOutStatus, vm.StatusText);
    }

    [Fact]
    public void SetCloudEnabled_WhenSaveFails_ShowsWarning_AndClearsOnNextSuccess()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore { SaveSucceeds = false };
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);

        vm.SetCloudEnabled(true);

        Assert.Equal(AccountViewModel.SettingsSaveFailedWarning, vm.SettingsSaveWarning);
        Assert.True(vm.HasSettingsSaveWarning);

        settings.SaveSucceeds = true;
        vm.SetCloudEnabled(false);

        Assert.Equal("", vm.SettingsSaveWarning);
        Assert.False(vm.HasSettingsSaveWarning);
    }

    [Fact]
    public void AcceptConsent_WhenSaveFails_ShowsWarning()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore { SaveSucceeds = false };
        var vm = CreateVm(account, settings);

        vm.AcceptConsent();

        Assert.Equal(AccountViewModel.SettingsSaveFailedWarning, vm.SettingsSaveWarning);
        Assert.True(vm.ConsentAccepted);
    }

    [Fact]
    public void Refresh_WhenSettingsFileCorrupt_ShowsLoadWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "teachcraft-account-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ broken");
            var settings = new FileCloudSyncSettingsStore(dir);
            var account = new FakeAccountService();
            var runtime = new CloudRuntime(true, account, null, null, settings, settings.Load());

            var vm = new AccountViewModel(runtime);

            Assert.Equal(FileCloudSyncSettingsStore.SettingsLoadFailedReason, vm.SettingsSaveWarning);
            Assert.True(vm.HasSettingsSaveWarning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SignOut_WhenSaveFails_ShowsWarning()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsStore { SaveSucceeds = false };
        settings.Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));
        account.SignIn("示例教师");
        var vm = CreateVm(account, settings);

        await vm.SignOutAsync();

        Assert.Equal(AccountViewModel.SettingsSaveFailedWarning, vm.SettingsSaveWarning);
        Assert.Contains("已退出登录", vm.StatusText, StringComparison.Ordinal);
    }
}
