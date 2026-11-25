using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TeachCraft.App.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 回环 OAuth 回调：解析规则 + 真实 socket 端到端。socket 只连 127.0.0.1，不碰外网。
/// </summary>
public sealed class LoopbackOAuthReceiverTests
{
    [Fact]
    public void OAuthCallbackParser_AcceptsCorrectPathStateCode()
    {
        var uri = new Uri("http://localhost:53140/callback?code=abc123&state=xyz789");

        var ok = OAuthCallbackParser.TryParse(uri, "xyz789", out var callback, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("abc123", callback.Code);
        Assert.Equal("xyz789", callback.State);
        Assert.Null(callback.Error);
        Assert.Null(callback.ErrorDescription);
    }

    [Fact]
    public void OAuthCallbackParser_RejectsWrongPath()
    {
        var uri = new Uri("http://localhost:53140/other?code=abc&state=xyz");

        var ok = OAuthCallbackParser.TryParse(uri, "xyz", out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void OAuthCallbackParser_RejectsWrongState()
    {
        var uri = new Uri("http://localhost:53140/callback?code=abc&state=wrong");

        var ok = OAuthCallbackParser.TryParse(uri, "expected", out var callback, out var error);

        Assert.False(ok);
        Assert.Null(callback.Code);
        Assert.Contains("登录校验失败", error);
    }

    [Fact]
    public void OAuthCallbackParser_RejectsMissingCode()
    {
        var uri = new Uri("http://localhost:53140/callback?state=xyz");

        var ok = OAuthCallbackParser.TryParse(uri, "xyz", out var callback, out var error);

        Assert.False(ok);
        Assert.Null(callback.Code);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void OAuthCallbackParser_DecodesPercentEncodedValues()
    {
        var uri = new Uri("http://localhost:53140/callback?code=a%2Bb+c&state=s%2B1");

        var ok = OAuthCallbackParser.TryParse(uri, "s+1", out var callback, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("a+b c", callback.Code);
        Assert.Equal("s+1", callback.State);
    }

    [Fact]
    public async Task Receiver_BindsLoopbackOnly()
    {
        var port = GetFreePort();
        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource();
        var task = receiver.WaitForCallbackAsync(Redirect(port), "state", cts.Token);

        var address = ReadBoundAddress(receiver);

        Assert.NotNull(address);
        Assert.True(IPAddress.IsLoopback(address!));
        Assert.True(address!.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback),
            $"只应绑定回环地址，实际为 {address}");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Receiver_EndToEnd_ReturnsCodeAndSuccessHtml()
    {
        var port = GetFreePort();
        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = receiver.WaitForCallbackAsync(Redirect(port), "synthetic-state", cts.Token);

        var response = await SendGetAsync(port, "/callback?code=synthetic-code&state=synthetic-state");
        var callback = await task;

        Assert.Equal("synthetic-code", callback.Code);
        Assert.Equal("synthetic-state", callback.State);
        Assert.Null(callback.Error);
        Assert.Contains("登录已完成，可以关闭此页。", response);
        Assert.Contains("text/html; charset=utf-8", response);
    }

    [Fact]
    public async Task Receiver_BothLoopbackFamilies_ReachParser()
    {
        var ipv4Port = GetFreePort();
        var ipv4Receiver = new LoopbackOAuthReceiver(ipv4Port);
        using var ipv4Cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ipv4Task = ipv4Receiver.WaitForCallbackAsync(Redirect(ipv4Port), "state", ipv4Cts.Token);
        await SendGetAsync(ipv4Port, "/callback?code=via-ipv4&state=state", IPAddress.Loopback);
        Assert.Equal("via-ipv4", (await ipv4Task).Code);

        if (!Socket.OSSupportsIPv6 || !CanConnectIPv6Loopback()) return;

        var ipv6Port = GetFreePort();
        var ipv6Receiver = new LoopbackOAuthReceiver(ipv6Port);
        using var ipv6Cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ipv6Task = ipv6Receiver.WaitForCallbackAsync(Redirect(ipv6Port), "state", ipv6Cts.Token);
        await SendGetAsync(ipv6Port, "/callback?code=via-ipv6&state=state", IPAddress.IPv6Loopback);
        Assert.Equal("via-ipv6", (await ipv6Task).Code);
    }

    [Fact]
    public async Task Receiver_IgnoresEmptyPreconnectAndFavicon_ThenAcceptsCallback()
    {
        var port = GetFreePort();
        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = receiver.WaitForCallbackAsync(Redirect(port), "expected-state", cts.Token);

        // 1) 模拟浏览器预连接打开后直接关闭而不发请求。
        using (var preconnect = new TcpClient())
        {
            await preconnect.ConnectAsync(IPAddress.Loopback, port);
        }

        // 2) 模拟浏览器自动请求 /favicon.ico：应返回 404 但不能提前关掉回调监听。
        var faviconResponse = await SendGetAsync(port, "/favicon.ico");
        Assert.Contains("404 Not Found", faviconResponse);

        // 3) 真正的 /callback 请求紧随其后，必须照常完成登录。
        var response = await SendGetAsync(port, "/callback?code=real-code&state=expected-state");
        var callback = await task;

        Assert.Equal("real-code", callback.Code);
        Assert.Equal("expected-state", callback.State);
        Assert.Contains("登录已完成，可以关闭此页。", response);
    }

    [Fact]
    public async Task Receiver_BusyOnIpv6Port_ThrowsActionablePortBusyError()
    {
        if (!CanConnectIPv6Loopback()) return;

        var port = GetFreePort();
        using var ipv6Blocker = new TcpListener(IPAddress.IPv6Loopback, port);
        ipv6Blocker.Start();

        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => receiver.WaitForCallbackAsync(Redirect(port), "state", cts.Token));

        Assert.Contains($"端口 {port} 被占用", ex.Message);
    }

    [Fact]
    public async Task Receiver_WrongState_ReturnsErrorCallback()
    {
        var port = GetFreePort();
        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = receiver.WaitForCallbackAsync(Redirect(port), "expected-state", cts.Token);

        var response = await SendGetAsync(port, "/callback?code=synthetic-code&state=wrong-state");
        var callback = await task;

        Assert.Null(callback.Code);
        Assert.NotNull(callback.Error);
        Assert.Contains("登录校验失败", response);
    }

    [Fact]
    public async Task Receiver_Cancellation_ReleasesPort()
    {
        var port = GetFreePort();
        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource();
        var task = receiver.WaitForCallbackAsync(Redirect(port), "state", cts.Token);
        Assert.NotNull(ReadBoundAddress(receiver));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start(); // 不抛异常即端口已释放
    }

    [Fact]
    public async Task Receiver_PortBusy_ThrowsActionableChineseException()
    {
        var port = GetFreePort();
        using var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();

        var receiver = new LoopbackOAuthReceiver(port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => receiver.WaitForCallbackAsync(Redirect(port), "state", cts.Token));

        Assert.Contains($"端口 {port} 被占用", ex.Message);
        Assert.Contains("请关闭占用该端口的程序后重试", ex.Message);
    }

    [Fact]
    public void BuildHtml_Success_IsSelfContainedGoogleStyleAuthPage()
    {
        var html = RenderHtml(true, "登录已完成，可以关闭此页。");

        Assert.Contains("您已成功完成身份认证。", html);
        Assert.Contains("登录已完成，可以关闭此页。", html);
        Assert.Contains("知微 · TeachCraft", html);
        Assert.Contains("min-height:100vh", html);
        Assert.Contains("--accent:#1a73e8}", html); // :root 规则必须闭合
        Assert.Contains("nav-progress", html);
        Assert.Contains("prefers-reduced-motion", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildHtml_Error_ShowsReasonAndErrorAccent()
    {
        var html = RenderHtml(false, "登录校验失败：state 不匹配");

        Assert.Contains("身份认证未能完成。", html);
        Assert.Contains("登录校验失败：state 不匹配", html);
        Assert.Contains("--accent:#d93025}", html);
        Assert.DoesNotContain("您已成功完成身份认证。", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildHtml_HtmlEncodesServerErrorDescription()
    {
        var html = RenderHtml(false, "<script>alert(1)</script> & 错误");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void BuildHtml_HasInlineAntigravityParticleRingAndMouseFollow()
    {
        var success = RenderHtml(true, "登录已完成，可以关闭此页。");
        var failure = RenderHtml(false, "登录校验失败：state 不匹配");

        Assert.Contains("<canvas", success);
        Assert.Contains("requestAnimationFrame", success);
        Assert.Contains("prefers-reduced-motion", success);
        // 标志性 Antigravity 粒子环：3D Simplex 噪声 + 径向旋转圆角胶囊 + 鼠标跟随。
        Assert.Contains("function snoise(", success);
        Assert.Contains("smoothstep", success);
        Assert.Contains("lineCap='round'", success);
        Assert.Contains("Math.atan2", success);
        Assert.Contains("pointermove", success);
        Assert.Contains("mousemove", success);
        Assert.Contains("mouseleave", success);
        Assert.Contains("#2c64ed", success);
        Assert.Contains("#f84242", success);
        Assert.Contains("#ffcf03", success);
        Assert.Contains("登录已完成，可以关闭此页。", success);
        Assert.Contains("<canvas", failure);
        Assert.Contains("function snoise(", failure);
        Assert.Contains("登录校验失败：state 不匹配", failure);
    }

    private static string RenderHtml(bool success, string message)
    {
        var method = typeof(LoopbackOAuthReceiver).GetMethod(
            "BuildHtml", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)method.Invoke(null, [success, message])!;
    }

    private static Uri Redirect(int port) => new($"http://127.0.0.1:{port}/callback");

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CanConnectIPv6Loopback()
    {
        if (!Socket.OSSupportsIPv6) return false;
        // 有些 Windows 策略/沙盒环境允许绑定 ::1，但出站连接 ::1 会抛出 SocketError.AccessDenied。
        // 这里用真实临时监听 + 客户端探活一次，不支持时按环境限制跳过，避免误报逻辑回归。
        try
        {
            using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var probe = new TcpClient(AddressFamily.InterNetworkV6);
            return probe.ConnectAsync(IPAddress.IPv6Loopback, port).Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException
        {
            SocketErrorCode: SocketError.AccessDenied or SocketError.AddressFamilyNotSupported
        })
        {
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressFamilyNotSupported)
        {
            return false;
        }
    }

    private static IPAddress? ReadBoundAddress(LoopbackOAuthReceiver receiver)
    {
        var property = typeof(LoopbackOAuthReceiver).GetProperty(
            "BoundAddress", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return (IPAddress?)property?.GetValue(receiver);
    }

    private static async Task<string> SendGetAsync(int port, string target, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        using var client = new TcpClient();
        await client.ConnectAsync(address, port);
        using var stream = client.GetStream();
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var request = Encoding.ASCII.GetBytes(
            $"GET {target} HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadToEndAsync(cts.Token);
    }
}
