using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using TeachCraft.App.Cloud;
using TeachCraft.App.ControlCenter;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Control;

namespace TeachCraft.Tests;

/// <summary>
/// 集控错误脱敏与网关回归（W1 todo 1-2）：
/// 1. ErrorText / CloudErrorText 识别带 Detail + Code 的 ControlException 原样透传中文；
/// 2. ControlViewModel 全部反馈经过 ErrorText.Describe，绝不泄露 CLR/HTTP 英文原文；
/// 3. ControlCenterService.EnrollAsync / EnrollWithAccountAsync 非业务异常返回固定中文。
/// </summary>
public sealed class ControlErrorSanitizationTests
{
    private sealed class ThrowingTokenAccountService : ICloudAccountService
    {
        private readonly Exception _ex;

        public ThrowingTokenAccountService(Exception ex) => _ex = ex;

        public bool IsSignedIn => true;
        public CloudAccount? Current => new("user_1", "示例教师", new[] { "openid" }, "client_1", DateTimeOffset.UtcNow);
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();
        public Task<CloudAccount> CompleteSignInAsync(CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken) => Task.FromException<string>(_ex);
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;

        public ThrowingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request));
    }

    private static ControlRuntime CreateConfiguredRuntime(HttpMessageHandler? handler = null)
    {
        var service = new ControlCenterService(new ControlCenterOptions
        {
            Enabled = false,
            Host = "https://sectl.cn",
            CredentialStore = new NoopControlCredentialStore(),
            StateStore = new NoopControlStateStore(),
            HttpHandler = handler,
        });
        return new ControlRuntime(
            isConfigured: true,
            service: service,
            settings: new NoopControlSettingsStore(),
            credentialStore: new NoopControlCredentialStore(),
            stateStore: new NoopControlStateStore(),
            initialSettings: ControlSettings.Default);
    }

    [Fact]
    public async Task ControlViewModel_LoadAccountSites_WhenTokenThrowsEnglishHttpError_ShowsChineseFeedback()
    {
        var cloud = new CloudRuntime(
            IsConfigured: true,
            Account: new ThrowingTokenAccountService(new HttpRequestException("The remote name could not be resolved")),
            SeatDocuments: null,
            Rosters: null,
            Settings: new NoopCloudSyncSettingsStore(),
            Snapshot: CloudSyncSettings.Default);

        await using var runtimeService = new ControlCenterService(new ControlCenterOptions
        {
            CredentialStore = new NoopControlCredentialStore(),
            StateStore = new NoopControlStateStore(),
        });
        var control = new ControlRuntime(
            isConfigured: true,
            service: runtimeService,
            settings: new NoopControlSettingsStore(),
            credentialStore: new NoopControlCredentialStore(),
            stateStore: new NoopControlStateStore(),
            initialSettings: ControlSettings.Default);
        using var vm = new ControlViewModel(control, cloud);

        await vm.LoadAccountSitesAsync();

        Assert.False(string.IsNullOrWhiteSpace(vm.FeedbackMessage));
        Assert.DoesNotContain("HttpRequestException", vm.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote name", vm.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("失败", vm.FeedbackMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlViewModel_EnrollAndTreeOperations_WhenUnexpectedExceptionThrown_NeverLeakEnglishText()
    {
        var handler = new ThrowingHttpHandler(_ => throw new InvalidOperationException("The remote host closed socket 10.0.0.1"));
        var runtime = CreateConfiguredRuntime(handler);
        var cloud = new CloudRuntime(
            IsConfigured: true,
            Account: new ThrowingTokenAccountService(new IOException(@"C:\Users\alice\token.json failed")),
            SeatDocuments: null,
            Rosters: null,
            Settings: new NoopCloudSyncSettingsStore(),
            Snapshot: CloudSyncSettings.Default);

        using var vm = new ControlViewModel(runtime, cloud);

        // 触发 EnrollWithAccountAsync（GetValidAccessTokenAsync 抛含本机路径的 IOException）
        vm.SelectedAccountSite = new ControlSiteSummary("site_1", "SC-6M34RQ", "示例中学", "admin", "管理员", 1);
        await vm.EnrollWithAccountAsync();
        Assert.DoesNotContain("alice", vm.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token.json", vm.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("失败", vm.FeedbackMessage, StringComparison.Ordinal);

        // 触发 RefreshDeviceTreeAsync（需 HasCredential=true，先给假凭证）
        var credStore = new NoopControlCredentialStore();
        credStore.Save(new ControlCredential
        {
            DeviceId = "dev_1",
            DeviceSecret = "sec_1",
            Host = "https://sectl.cn",
            SitePublicId = "SC-6M34RQ",
            SiteName = "示例中学",
        });
        await using var service2 = new ControlCenterService(new ControlCenterOptions
        {
            CredentialStore = credStore,
            StateStore = new NoopControlStateStore(),
            HttpHandler = handler,
        });
        var runtime2 = new ControlRuntime(
            true, service2, new NoopControlSettingsStore(), credStore, new NoopControlStateStore(), ControlSettings.Default);
        using var vm2 = new ControlViewModel(runtime2);

        await vm2.RefreshDeviceTreeAsync();
        Assert.DoesNotContain("socket", vm2.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.0.0.1", vm2.FeedbackMessage, StringComparison.Ordinal);
        Assert.Contains("失败", vm2.FeedbackMessage, StringComparison.Ordinal);

        vm2.TreeClasses.Add(new ControlClassOption("c1", "高一", "高一1班"));
        vm2.SelectedTreeClass = vm2.TreeClasses[0];
        await vm2.AssignSelectedClassAsync();
        Assert.DoesNotContain("socket", vm2.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("失败", vm2.FeedbackMessage, StringComparison.Ordinal);

        await vm2.PreviewDeviceSeatsSummaryAsync();
        Assert.DoesNotContain("socket", vm2.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("失败", vm2.FeedbackMessage, StringComparison.Ordinal);

        await vm2.OccupySelectedClassAsync();
        Assert.Contains("尚未连接", vm2.FeedbackMessage, StringComparison.Ordinal);

        await vm2.ReleaseClassAsync();
        Assert.Contains("尚未连接", vm2.FeedbackMessage, StringComparison.Ordinal);

        await runtime.Service!.DisposeAsync();
    }

    [Fact]
    public void ErrorText_ControlException_PreservesChineseDetailVerbatim_AndFallsBackWhenEmpty()
    {
        var withDetail = new ControlException("code_expired", "配对码已过期，请到 SECTL 网页端重新生成。");
        Assert.Equal("配对码已过期，请到 SECTL 网页端重新生成。", CloudErrorText.DescribeForUi(withDetail, "接入"));

        var emptyDetail = new ControlException("code_expired", string.Empty);
        var fallback = CloudErrorText.DescribeForUi(emptyDetail, "接入");
        Assert.StartsWith("接入失败：", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlException", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlCenterService_Enroll_WhenNonControlExceptionThrown_ReturnsFixedChineseWithoutRawMessage()
    {
        var handler = new ThrowingHttpHandler(_ => throw new InvalidOperationException("The remote name could not be resolved"));
        var logs = new List<string>();
        await using var loggingService = new ControlCenterService(new ControlCenterOptions
        {
            Host = "https://sectl.cn",
            CredentialStore = new NoopControlCredentialStore(),
            StateStore = new NoopControlStateStore(),
            HttpHandler = handler,
            Log = logs.Add,
        });

        var (ok1, msg1, cred1) = await loggingService.EnrollAsync("SC-6M34RQ", "ABCD-7KQ2");
        Assert.False(ok1);
        Assert.Null(cred1);
        Assert.Contains("失败", msg1, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", msg1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The remote", msg1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, l => l.Contains("The remote name could not be resolved", StringComparison.Ordinal));

        var (ok2, msg2, cred2) = await loggingService.EnrollWithAccountAsync("jwt-token", "SC-6M34RQ");
        Assert.False(ok2);
        Assert.Null(cred2);
        Assert.Contains("失败", msg2, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", msg2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The remote", msg2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ControlCenterService_Enroll_WhenControlExceptionThrown_PreservesChineseDetail()
    {
        var handler = new ThrowingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"ok":false,"code":"code_expired"}""", Encoding.UTF8, "application/json"),
        });
        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Host = "https://sectl.cn",
            CredentialStore = new NoopControlCredentialStore(),
            StateStore = new NoopControlStateStore(),
            HttpHandler = handler,
        });

        var (ok1, msg1, _) = await service.EnrollAsync("SC-6M34RQ", "ABCD-7KQ2");
        Assert.False(ok1);
        Assert.Equal("配对码已过期，请到 SECTL 网页端重新生成。", msg1);

        var (ok2, msg2, _) = await service.EnrollWithAccountAsync("jwt-token", "SC-6M34RQ");
        Assert.False(ok2);
        Assert.Equal("配对码已过期，请到 SECTL 网页端重新生成。", msg2);
    }
}
