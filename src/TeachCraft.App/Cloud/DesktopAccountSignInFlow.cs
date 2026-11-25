using Avalonia.Controls;
using TeachCraft.Core.Cloud;
using TeachCraft.Shared;

namespace TeachCraft.App.Cloud;

/// <summary>一次登录尝试的终态。失败也带一句能直接进状态栏的中文，不抛给界面。</summary>
public sealed record SignInOutcome(bool Success, string Message)
{
    public static SignInOutcome Ok(string message) => new(true, message);
    public static SignInOutcome Fail(string message) => new(false, message);
}

/// <summary>
/// 桌面端登录编排：建请求 → 开系统浏览器 → 在回环地址等回调 → 换 token。
/// 只编排，不实现任何一步——浏览器、回调监听、账号服务都是注入的。
/// 没有重试循环：一次失败就是一次失败，重试由用户点按钮决定。
/// </summary>
public sealed class DesktopAccountSignInFlow
{
    private readonly ICloudAccountService _account;
    private readonly IBrowserLauncher _browser;
    private readonly IOAuthCallbackReceiver _receiver;

    public DesktopAccountSignInFlow(
        ICloudAccountService account,
        IBrowserLauncher browser,
        IOAuthCallbackReceiver receiver)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
    }

    public async Task<SignInOutcome> SignInAsync(Window owner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return SignInOutcome.Fail(CloudErrorText.SignInCancelled);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = _account.CreateSignInRequest();

        // 先启动回环监听再开浏览器：已登录的浏览器会话可能瞬间就重定向回来，
        // 回调若早于 socket 绑定到达会被拒连，然后只能等满 120 秒报「登录等待超时」。
        Task<OAuthCallback> callbackTask;
        try
        {
            callbackTask = _receiver.WaitForCallbackAsync(
                new Uri(request.RedirectUri), request.State, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SignInOutcome.Fail(CloudErrorText.SignInCancelled);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && HasChinese(ex.Message))
        {
            return SignInOutcome.Fail(ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return SignInOutcome.Fail(ErrorText.Describe(ex, "登录"));
        }

        try
        {
            await _browser.OpenAsync(request.AuthorizeUrl, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelAndObserve(callbackTask, linked);
            return SignInOutcome.Fail(CloudErrorText.SignInCancelled);
        }
        catch (Exception)
        {
            // 浏览器起不来时把地址原样交给用户，他还能复制去浏览器里完成登录。
            // 同时取消已启动的监听，别留下一个占着端口的悬挂 socket。
            CancelAndObserve(callbackTask, linked);
            return SignInOutcome.Fail($"{CloudErrorText.BrowserOpenFailed} 登录地址：{request.AuthorizeUrl}");
        }

        OAuthCallback callback;
        try
        {
            callback = await callbackTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SignInOutcome.Fail(CloudErrorText.SignInCancelled);
        }
        catch (TimeoutException)
        {
            return SignInOutcome.Fail(CloudErrorText.SignInTimeout);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return SignInOutcome.Fail(ErrorText.Describe(ex, "登录"));
        }

        if (!string.IsNullOrEmpty(callback.Error))
            return SignInOutcome.Fail(DescribeCallbackError(callback));

        if (string.IsNullOrEmpty(callback.Code))
            return SignInOutcome.Fail(CloudErrorText.SignInCancelled);

        try
        {
            var account = await _account.CompleteSignInAsync(
                request, callback.Code!, callback.State ?? request.State, linked.Token);

            // 浏览器把焦点抢走了，登录完成后把发起登录的窗口带回前台。
            owner?.Activate();
            return SignInOutcome.Ok($"登录成功：{account.DisplayName}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SignInOutcome.Fail(CloudErrorText.SignInCancelled);
        }
        catch (CloudAuthException ex)
        {
            return SignInOutcome.Fail(ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return SignInOutcome.Fail(ErrorText.Describe(ex, "登录"));
        }
    }

    /// <summary>浏览器失败时收掉已启动的监听：取消它并观察异常，不留悬挂 socket 或未观测异常。</summary>
    private static void CancelAndObserve(Task<OAuthCallback> callbackTask, CancellationTokenSource linked)
    {
        linked.Cancel();
        _ = callbackTask.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>把 OAuth 回调错误码映射成 CloudErrorText 里的成句中文。</summary>
    public static string DescribeCallbackError(OAuthCallback callback)
    {
        var code = callback.Error ?? string.Empty;
        if (code.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return CloudErrorText.SignInCancelled;
        // OAuthCallbackParser 把 state 不一致写成 CloudErrorText.StateMismatch 这句中文，
        // 它不含 "state" 字样，下面的英文匹配接不住，会误报成「云端服务暂时不可用」。
        if (string.Equals(code, CloudErrorText.StateMismatch, StringComparison.Ordinal))
            return CloudErrorText.StateMismatch;
        if (code.Contains("state", StringComparison.OrdinalIgnoreCase))
            return CloudErrorText.StateMismatch;
        if (code.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return CloudErrorText.SignInTimeout;

        var detail = callback.ErrorDescription;
        return string.IsNullOrWhiteSpace(detail)
            ? CloudErrorText.ServerError
            : $"{CloudErrorText.ServerError}（{detail}）";
    }

    private static bool HasChinese(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Any(c => c is >= '\u4E00' and <= '\u9FFF');
}
