using Avalonia.Controls;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>桌面登录编排：只验证顺序与失败话术，浏览器与回调都是假件。</summary>
public sealed class DesktopAccountSignInFlowTests
{
    private sealed class FakeAccount : ICloudAccountService
    {
        public CloudAccount? Current => null;
        public bool IsSignedIn => false;
        public string? LastSignOutNotice => null;
        public event EventHandler? StateChanged;
        public Exception? CompleteFailure { get; set; }
        public int CompleteCalls { get; private set; }
        public string? ReceivedCode { get; private set; }
        public string? ReceivedState { get; private set; }

        public CloudSignInRequest CreateSignInRequest() => new(
            new Uri("https://example.invalid/oauth/authorize?state=abc"), "abc", "verifier",
            "http://localhost:53140/callback", DateTimeOffset.UtcNow);

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
        {
            CompleteCalls++;
            ReceivedCode = code;
            ReceivedState = state;
            if (CompleteFailure is not null) throw CompleteFailure;
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
        public Exception? Failure { get; set; }
        public int OpenCalls { get; private set; }
        public Uri? Url { get; private set; }

        public Task OpenAsync(Uri url, CancellationToken ct)
        {
            OpenCalls++;
            Url = url;
            if (Failure is not null) throw Failure;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiver : IOAuthCallbackReceiver
    {
        public OAuthCallback Callback { get; set; } = new("code-1", "abc", null, null);
        public Exception? Failure { get; set; }
        public int WaitCalls { get; private set; }

        public Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
        {
            WaitCalls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Callback);
        }
    }

    private sealed class RecordingReceiver : IOAuthCallbackReceiver
    {
        private readonly List<string> _log;

        public RecordingReceiver(List<string> log) => _log = log;

        public Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
        {
            _log.Add("wait");
            return Task.FromResult(new OAuthCallback("code-1", "abc", null, null));
        }
    }

    private sealed class RecordingBrowser : IBrowserLauncher
    {
        private readonly List<string> _log;

        public RecordingBrowser(List<string> log) => _log = log;

        public Task OpenAsync(Uri url, CancellationToken ct)
        {
            _log.Add("open");
            return Task.CompletedTask;
        }
    }

    private sealed class CancellableReceiver : IOAuthCallbackReceiver
    {
        public CancellationToken CapturedToken { get; private set; }

        public Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
        {
            CapturedToken = ct;
            var tcs = new TaskCompletionSource<OAuthCallback>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
    }

    private static DesktopAccountSignInFlow Create(
        FakeAccount account, FakeBrowser browser, FakeReceiver receiver)
        => new(account, browser, receiver);

    [Fact]
    public async Task Success_OpensBrowserWaitsForCallbackAndCompletes()
    {
        var account = new FakeAccount();
        var browser = new FakeBrowser();
        var receiver = new FakeReceiver();

        var outcome = await Create(account, browser, receiver).SignInAsync(null!, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains("示例教师", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(1, browser.OpenCalls);
        Assert.Equal(1, receiver.WaitCalls);
        Assert.Equal(1, account.CompleteCalls);
        Assert.Equal("code-1", account.ReceivedCode);
        Assert.Equal("abc", account.ReceivedState);
    }

    [Fact]
    public async Task BrowserFailure_ReturnsCopyableUrl()
    {
        var browser = new FakeBrowser { Failure = new InvalidOperationException("shell failed") };
        var receiver = new FakeReceiver();
        var account = new FakeAccount();

        var outcome = await Create(account, browser, receiver).SignInAsync(null!, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains(CloudErrorText.BrowserOpenFailed, outcome.Message, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid/oauth/authorize", outcome.Message, StringComparison.Ordinal);
        // 监听先于浏览器启动，所以失败时 WaitCalls 也是 1；端口由 CancelAndObserve 收掉。
        Assert.Equal(1, receiver.WaitCalls);
        Assert.Equal(0, account.CompleteCalls);
    }

    [Fact]
    public async Task CallbackError_UsesCloudErrorTextWording()
    {
        var receiver = new FakeReceiver
        {
            Callback = new OAuthCallback(null, "abc", "access_denied", "用户拒绝授权"),
        };
        var account = new FakeAccount();

        var outcome = await Create(account, new FakeBrowser(), receiver).SignInAsync(null!, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(CloudErrorText.SignInCancelled, outcome.Message);
        Assert.Equal(0, account.CompleteCalls);
    }

    [Fact]
    public async Task CompleteSignInThrowsCloudAuthException_ReturnsItsMessage()
    {
        var account = new FakeAccount
        {
            CompleteFailure = new CloudAuthException(CloudErrorText.StateMismatch),
        };

        var outcome = await Create(account, new FakeBrowser(), new FakeReceiver())
            .SignInAsync(null!, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(CloudErrorText.StateMismatch, outcome.Message);
    }

    [Fact]
    public async Task Cancellation_ReturnsSignInCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await Create(new FakeAccount(), new FakeBrowser(), new FakeReceiver())
            .SignInAsync(null!, cts.Token);

        Assert.False(outcome.Success);
        Assert.Equal(CloudErrorText.SignInCancelled, outcome.Message);
    }

    [Fact]
    public async Task CallbackListener_StartsBeforeBrowserOpens()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var outcome = await new DesktopAccountSignInFlow(
                new FakeAccount(), new RecordingBrowser(log), new RecordingReceiver(log))
            .SignInAsync(null!, cts.Token);

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "wait", "open" }, log);
    }

    [Fact]
    public async Task BrowserFailure_CancelsAlreadyStartedListener()
    {
        var receiver = new CancellableReceiver();
        var browser = new FakeBrowser { Failure = new InvalidOperationException("shell failed") };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var outcome = await new DesktopAccountSignInFlow(new FakeAccount(), browser, receiver)
            .SignInAsync(null!, cts.Token);

        Assert.False(outcome.Success);
        Assert.Contains(CloudErrorText.BrowserOpenFailed, outcome.Message, StringComparison.Ordinal);
        Assert.True(receiver.CapturedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("access_denied", "已取消登录")]
    [InlineData("invalid_state", "校验失败")]
    [InlineData("timeout", "超时")]
    [InlineData("server_error", "暂时不可用")]
    public void DescribeCallbackError_MapsKnownCodes(string code, string expected)
    {
        var message = DesktopAccountSignInFlow.DescribeCallbackError(
            new OAuthCallback(null, "abc", code, null));

        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortBusyInvalidOperation_SurfacesChinesePortMessageVerbatim()
    {
        const string portBusy = "端口 53140 被占用，请关闭占用该端口的程序后重试。";
        var receiver = new FakeReceiver { Failure = new InvalidOperationException(portBusy) };

        var outcome = await Create(new FakeAccount(), new FakeBrowser(), receiver)
            .SignInAsync(null!, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(portBusy, outcome.Message);
    }
}
