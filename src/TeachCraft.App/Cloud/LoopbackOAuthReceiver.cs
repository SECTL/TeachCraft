using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TeachCraft.App.Cloud;

/// <summary>
/// 在 127.0.0.1 上开一个一次性 HTTP 服务，接住浏览器重定向回来的授权码。
/// 只绑定回环地址，只接一个请求，回完纸墨风格的小页面就关监听；
/// 120 秒超时与调用方取消都会释放端口，绝不留下悬挂的 socket。
/// </summary>
public sealed class LoopbackOAuthReceiver : IOAuthCallbackReceiver
{
    private const int DefaultPort = 53140;
    private const string CallbackPath = "/callback";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);

    private readonly int _port;
    private readonly TimeProvider _time;

    public LoopbackOAuthReceiver(int port = DefaultPort, TimeProvider? time = null)
    {
        _port = port;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>实际绑定的地址，供测试断言只绑了回环。监听启动后才有值。</summary>
    internal IPAddress? BoundAddress { get; private set; }

    public async Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct)
    {
        var port = !redirectUri.IsDefaultPort ? redirectUri.Port : _port;

        // localhost 同时解析到 ::1 与 127.0.0.1，浏览器可能只走其中一个而不回退。
        // 单个绑在 ::1 的 socket 即使关掉 IPv6Only 也接不到 127.0.0.1，而绑 Any 会把
        // 回调端口暴露到局域网。所以两个回环地址各开一个监听，先到的合法回调直接完成登录。
        var ipv6 = StartIpv6(port);
        TcpListener ipv4;
        try
        {
            ipv4 = new TcpListener(IPAddress.Loopback, port);
            ipv4.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ipv6?.Stop();
            throw new InvalidOperationException($"端口 {port} 被占用，请关闭占用该端口的程序后重试。", ex);
        }
        catch (SocketException ex)
        {
            ipv6?.Stop();
            throw new InvalidOperationException($"端口 {port} 无法启动本地回调监听，请关闭占用或检查端口设置后重试。", ex);
        }

        using (ipv4)
        using (ipv6)
        {
            BoundAddress = (ipv6?.LocalEndpoint as IPEndPoint)?.Address
                           ?? (ipv4.LocalEndpoint as IPEndPoint)?.Address;

            // 用带 TimeProvider 的 CTS 计时：真实环境走墙钟，测试可注入虚拟时间。
            using var timeoutCts = new CancellationTokenSource(DefaultTimeout, _time);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            Task<TcpClient>? accept4 = null;
            Task<TcpClient>? accept6 = null;
            try
            {
                // 浏览器可能会先发预连接或请求 /favicon.ico；只忽略非回调连接并继续等待，
                // 直到收到真正的 /callback，或整体超时 / 被取消。
                while (!linkedCts.IsCancellationRequested)
                {
                    accept4 ??= ipv4.AcceptTcpClientAsync(linkedCts.Token).AsTask();
                    if (ipv6 is not null)
                        accept6 ??= ipv6.AcceptTcpClientAsync(linkedCts.Token).AsTask();

                    var winner = accept6 is null
                        ? accept4
                        : await Task.WhenAny(accept4, accept6).ConfigureAwait(false);
                    var client = await winner.ConfigureAwait(false);
                    if (ReferenceEquals(winner, accept4)) accept4 = null;
                    else accept6 = null;

                    using (client)
                    {
                        var callback = await TryHandleClientAsync(
                                client, redirectUri, expectedState, linkedCts.Token)
                            .ConfigureAwait(false);
                        if (callback is not null)
                            return callback;
                    }
                }

                linkedCts.Token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(linkedCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException("登录等待超时，请重新发起登录。");
            }
            finally
            {
                // 停止监听后再统一观察尚未取走的等待任务，防止退出后触发 UnobservedTaskException。
                ipv4.Stop();
                ipv6?.Stop();
                ObservePendingAccept(accept4);
                ObservePendingAccept(accept6);
            }
        }
    }

    private static TcpListener? StartIpv6(int port)
    {
        if (!Socket.OSSupportsIPv6) return null;

        TcpListener? ipv6 = null;
        try
        {
            ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
            ipv6.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            ipv6.Start();
            return ipv6;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // 若 ::1 端口已被占用而继续只监听 127.0.0.1，浏览器访问 localhost 时可能打到占用 ::1 的进程。
            ipv6?.Stop();
            throw new InvalidOperationException($"端口 {port} 被占用，请关闭占用该端口的程序后重试。", ex);
        }
        catch (SocketException)
        {
            // IPv6 在本机不可用或被策略禁用时优雅降级为仅 IPv4 监听。
            ipv6?.Stop();
            return null;
        }
    }

    private static void ObservePendingAccept(Task<TcpClient>? task)
    {
        if (task is null) return;
        if (task.IsCompletedSuccessfully)
            task.Result.Dispose();
        else
            _ = task.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
                else
                    _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static async Task<OAuthCallback?> TryHandleClientAsync(
        TcpClient client,
        Uri redirectUri,
        string expectedState,
        CancellationToken ct)
    {
        using var stream = client.GetStream();
        using var readTimeoutCts = new CancellationTokenSource(RequestReadTimeout);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeoutCts.Token);

        string? requestLine;
        try
        {
            requestLine = await ReadRequestLineAsync(stream, readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && readTimeoutCts.IsCancellationRequested)
        {
            // 浏览器预连接打开后未及时发送 HTTP 首行：忽略该连接继续等待真实回调。
            return null;
        }

        var requestUri = ParseRequestTarget(requestLine, redirectUri);
        if (requestUri is null)
            return null;

        if (!string.Equals(requestUri.AbsolutePath, CallbackPath, StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, "HTTP/1.1 404 Not Found", "页面不存在。", ct).ConfigureAwait(false);
            return null;
        }

        OAuthCallbackParser.TryParse(requestUri, expectedState, out var callback, out var error);
        await WriteResponseAsync(
            stream,
            "HTTP/1.1 200 OK",
            error is null ? "登录已完成，可以关闭此页。" : error,
            ct).ConfigureAwait(false);
        return callback;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (requestLine is null)
            return null;

        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct).ConfigureAwait(false)))
        {
            // 跳过请求头；GET 回调没有请求体。
        }

        return requestLine;
    }

    private static Uri? ParseRequestTarget(string? requestLine, Uri redirectUri)
    {
        if (string.IsNullOrWhiteSpace(requestLine))
            return null;

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var target = parts[1];
        if (target.StartsWith('/'))
        {
            var authority = redirectUri.GetLeftPart(UriPartial.Authority);
            return Uri.TryCreate(new Uri(authority), target, out var combined) ? combined : null;
        }

        return Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute : null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            BuildHtml(status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), message));
        var header = Encoding.ASCII.GetBytes(
            status + "\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string BuildHtml(bool success, string message)
    {
        // message 可能含服务端返回的 error_description，必须先编码再嵌入。
        var text = WebUtility.HtmlEncode(message);
        var title = success ? "知微 · 登录成功" : "知微 · 登录未完成";
        var accent = success ? "#1a73e8" : "#d93025";
        var barGrad = success
            ? "linear-gradient(90deg,#2c64ed,#4285f4,#8ab4f8)"
            : "linear-gradient(90deg,#d93025,#ea4335,#f28b82)";
        var color1 = success ? "#2c64ed" : "#d93025";
        var color2 = success ? "#f84242" : "#a34a36";
        var color3 = success ? "#ffcf03" : "#f28b82";
        var heading = success ? "您已成功完成身份认证。" : "身份认证未能完成。";
        var bodyCopy = success
            ? $"{text}请返回知微桌面端继续使用。"
            : text;
        var msgClass = success ? "redirect-message" : "redirect-message is-error";
        var footerAction = success
            ? "<span class=\"meta-text\">SECTL 账号授权</span>"
            : "<span class=\"meta-text\">请返回知微重新登录</span>";

        // 内联 SVG 不写 xmlns：会引入 http:// 串，破坏离线自包含。
        return $$$"""
                <!doctype html>
                <html lang="zh-CN">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{{title}}}</title>
                <style>
                :root{--bg:#ffffff;--ink:#202124;--muted:#5f6368;--line:#dadce0;--accent:{{{accent}}}}
                *{box-sizing:border-box}
                html,body{margin:0;height:100%;min-height:100%}
                body{background:var(--bg);color:var(--ink);font-family:'Google Sans',-apple-system,BlinkMacSystemFont,'Segoe UI','PingFang SC','Hiragino Sans GB','Microsoft YaHei UI','Microsoft YaHei',sans-serif;-webkit-font-smoothing:antialiased;overflow:hidden}
                .nav-progress{position:fixed;top:0;left:0;width:100%;height:2px;z-index:10;pointer-events:none;background:{{{barGrad}}};transform-origin:0% 50%;transform:scaleX(0);opacity:0;animation:navSweep 1.35s cubic-bezier(.16,1,.3,1) forwards}
                .auth-success-container{position:relative;width:100%;min-height:100vh;height:100vh;display:flex;justify-content:center;align-items:center;overflow:hidden;padding:24px}
                .particles-container{position:absolute;inset:0;width:100%;height:100%;z-index:0;pointer-events:none;overflow:hidden}
                #particles{position:absolute;inset:0;width:100%;height:100%;display:block;pointer-events:none}
                .content{position:relative;z-index:1;display:flex;flex-direction:column;align-items:center;gap:24px;text-align:center;max-width:560px;padding:16px 24px;animation:contentRise .42s cubic-bezier(.22,.61,.36,1) both}
                .content::before{content:'';position:absolute;inset:-64px -88px;z-index:-1;pointer-events:none;background:radial-gradient(closest-side,rgba(255,255,255,.92) 34%,rgba(255,255,255,.58) 64%,rgba(255,255,255,0) 100%)}
                .brand{display:inline-flex;align-items:center;gap:12px;height:48px;user-select:none}
                .brand-mark{width:40px;height:40px;flex-shrink:0;display:block}
                .brand-wordmark{font-size:26px;font-weight:500;letter-spacing:-.02em;line-height:1;color:var(--ink);display:inline-flex;align-items:baseline;gap:8px}
                .brand-en{font-weight:400;color:#3c4043;letter-spacing:-.015em}
                h1{margin:0;font-size:32px;font-weight:500;line-height:1.25;letter-spacing:-.015em;color:var(--ink)}
                .redirect-message{margin:0;font-size:14px;line-height:1.65;color:var(--muted);max-width:500px}
                .redirect-message.is-error{color:#b3261e}
                .links{display:flex;align-items:center;justify-content:center;flex-wrap:wrap;gap:8px;font-size:14px;line-height:1.4}
                .meta-text{color:var(--muted);font-weight:400}
                .separator{color:var(--line);user-select:none}
                .docs-link{appearance:none;background:none;border:0;padding:0;margin:0;font:inherit;font-size:14px;font-weight:500;color:var(--accent);cursor:pointer;text-decoration:none}
                .docs-link:hover{text-decoration:underline}
                .docs-link:focus-visible{outline:2px solid var(--accent);outline-offset:3px;border-radius:4px}
                .docs-link.is-muted{color:var(--muted);cursor:default;text-decoration:none}
                @keyframes navSweep{0%{opacity:1;transform:scaleX(0)}60%{opacity:1;transform:scaleX(.78)}82%{opacity:1;transform:scaleX(1)}100%{opacity:0;transform:scaleX(1)}}
                @keyframes contentRise{from{opacity:0;transform:translateY(10px) scale(.985)}to{opacity:1;transform:none}}
                @media (max-width:520px){.content{padding:24px 20px;gap:20px}h1{font-size:26px}.brand-wordmark{font-size:22px}}
                @media (prefers-reduced-motion:reduce){.nav-progress{display:none}.content{animation:none}#particles{opacity:.85}}
                </style>
                </head>
                <body>
                <div class="nav-progress" role="progressbar" aria-hidden="true"></div>
                <main class="auth-success-container">
                  <div class="particles-container" aria-hidden="true">
                    <canvas id="particles"></canvas>
                  </div>
                  <div class="content">
                    <div class="brand">
                      <svg class="brand-mark" viewBox="0 0 120 120" width="40" height="40" fill="none" aria-hidden="true">
                        <rect x="10" y="10" width="100" height="100" rx="24" fill="#FAF9F5" stroke="#E7E3D8" stroke-width="3"/>
                        <rect x="26" y="30" width="46" height="12" rx="3" fill="#141413"/>
                        <rect x="76" y="30" width="18" height="12" rx="3" fill="#D97757"/>
                        <rect x="26" y="48" width="68" height="16" rx="3" fill="#E7E3D8"/>
                        <rect x="28" y="50" width="22" height="12" rx="2" fill="#3D3B36"/>
                        <rect x="54" y="50" width="38" height="12" rx="2" fill="#FAF9F5"/>
                        <rect x="58" y="53" width="20" height="6" rx="1.5" fill="#85806F"/>
                        <rect x="26" y="70" width="20" height="14" rx="3" fill="#3D3B36"/>
                        <rect x="50" y="70" width="20" height="14" rx="3" fill="#85806F"/>
                        <rect x="74" y="70" width="20" height="14" rx="3" fill="#141413"/>
                        <line x1="74" y1="22" x2="74" y2="94" stroke="#D97757" stroke-width="3" stroke-linecap="round"/>
                        <circle cx="74" cy="22" r="3.5" fill="#D97757"/>
                        <circle cx="74" cy="94" r="2.5" fill="#D97757"/>
                      </svg>
                      <span class="brand-wordmark">知微 <span class="brand-en">TeachCraft</span></span>
                    </div>
                    <h1>{{{heading}}}</h1>
                    <p class="{{{msgClass}}}">{{{bodyCopy}}}</p>
                    <div class="links">
                      <span class="meta-text">知微 · TeachCraft</span>
                      <span class="separator" aria-hidden="true">|</span>
                      {{{footerAction}}}
                      <span class="separator" aria-hidden="true">|</span>
                      <button type="button" class="docs-link" id="close-tab">关闭此页</button>
                    </div>
                  </div>
                </main>
                <script>
                (function(){
                  var closeBtn=document.getElementById('close-tab');
                  if(closeBtn){
                    closeBtn.addEventListener('click',function(){
                      window.close();
                      setTimeout(function(){
                        if(!window.closed){
                          closeBtn.textContent='请手动关闭此标签页';
                          closeBtn.classList.add('is-muted');
                        }
                      },150);
                    });
                  }
                  var canvas=document.getElementById('particles');
                  if(!canvas)return;
                  var ctx=canvas.getContext('2d');
                  if(!ctx)return;
                  var mq=window.matchMedia?window.matchMedia('(prefers-reduced-motion: reduce)'):null;
                  var reduce=!!(mq&&mq.matches);

                  // 3D Simplex 噪声：复刻 Antigravity 着色器的有机波浪场与色阶平滑迁移，无外部依赖。
                  var grad3=[[1,1,0],[-1,1,0],[1,-1,0],[-1,-1,0],[1,0,1],[-1,0,1],[1,0,-1],[-1,0,-1],[0,1,1],[0,-1,1],[0,1,-1],[0,-1,-1]];
                  var perm=new Uint8Array(512),perm12=new Uint8Array(512);
                  (function(){
                    var s=1337,p=new Uint8Array(256);
                    for(var i=0;i<256;i++)p[i]=i;
                    for(var i=255;i>0;i--){s=(s*16807)%2147483647;var j=s%(i+1),tmp=p[i];p[i]=p[j];p[j]=tmp;}
                    for(var i=0;i<512;i++){perm[i]=p[i&255];perm12[i]=perm[i]%12;}
                  })();
                  var F3=1/3,G3=1/6;
                  function snoise(xin,yin,zin){
                    var n0,n1,n2,n3,s=(xin+yin+zin)*F3;
                    var i=Math.floor(xin+s),j=Math.floor(yin+s),k=Math.floor(zin+s);
                    var t=(i+j+k)*G3,X0=i-t,Y0=j-t,Z0=k-t;
                    var x0=xin-X0,y0=yin-Y0,z0=zin-Z0;
                    var i1,j1,k1,i2,j2,k2;
                    if(x0>=y0){
                      if(y0>=z0){i1=1;j1=0;k1=0;i2=1;j2=1;k2=0;}
                      else if(x0>=z0){i1=1;j1=0;k1=0;i2=1;j2=0;k2=1;}
                      else{i1=0;j1=0;k1=1;i2=1;j2=0;k2=1;}
                    }else{
                      if(y0<z0){i1=0;j1=0;k1=1;i2=0;j2=1;k2=1;}
                      else if(x0<z0){i1=0;j1=1;k1=0;i2=0;j2=1;k2=1;}
                      else{i1=0;j1=1;k1=0;i2=1;j2=1;k2=0;}
                    }
                    var x1=x0-i1+G3,y1=y0-j1+G3,z1=z0-k1+G3;
                    var x2=x0-i2+2*G3,y2=y0-j2+2*G3,z2=z0-k2+2*G3;
                    var x3=x0-1+3*G3,y3=y0-1+3*G3,z3=z0-1+3*G3;
                    var ii=i&255,jj=j&255,kk=k&255;
                    var t0=0.6-x0*x0-y0*y0-z0*z0;
                    if(t0<0)n0=0;else{t0*=t0;var g0=grad3[perm12[ii+perm[jj+perm[kk]]]];n0=t0*t0*(g0[0]*x0+g0[1]*y0+g0[2]*z0);}
                    var t1=0.6-x1*x1-y1*y1-z1*z1;
                    if(t1<0)n1=0;else{t1*=t1;var g1=grad3[perm12[ii+i1+perm[jj+j1+perm[kk+k1]]]];n1=t1*t1*(g1[0]*x1+g1[1]*y1+g1[2]*z1);}
                    var t2=0.6-x2*x2-y2*y2-z2*z2;
                    if(t2<0)n2=0;else{t2*=t2;var g2=grad3[perm12[ii+i2+perm[jj+j2+perm[kk+k2]]]];n2=t2*t2*(g2[0]*x2+g2[1]*y2+g2[2]*z2);}
                    var t3=0.6-x3*x3-y3*y3-z3*z3;
                    if(t3<0)n3=0;else{t3*=t3;var g3=grad3[perm12[ii+1+perm[jj+1+perm[kk+1]]]];n3=t3*t3*(g3[0]*x3+g3[1]*y3+g3[2]*z3);}
                    return 32*(n0+n1+n2+n3);
                  }
                  function smoothstep(e0,e1,x){
                    var t=(x-e0)/(e1-e0);
                    if(t<0)t=0;else if(t>1)t=1;
                    return t*t*(3-2*t);
                  }
                  function rgb(hex){return [parseInt(hex.slice(1,3),16),parseInt(hex.slice(3,5),16),parseInt(hex.slice(5,7),16)];}
                  function mix(a,b,t){return [a[0]+(b[0]-a[0])*t,a[1]+(b[1]-a[1])*t,a[2]+(b[2]-a[2])*t];}

                  // 预计算 128 级色彩查找表（#2c64ed -> #f84242 -> #ffcf03），渲染循环零字符串分配。
                  var PALETTE=[];
                  (function(){
                    var c1=rgb('{{{color1}}}'),c2=rgb('{{{color2}}}'),c3=rgb('{{{color3}}}'),h=0.8;
                    for(var i=0;i<128;i++){
                      var p=i/127;
                      var c=p<h?mix(c1,c2,p/h):mix(c2,c3,(p-h)/(1-h));
                      PALETTE.push('rgb('+Math.round(c[0])+','+Math.round(c[1])+','+Math.round(c[2])+')');
                    }
                  })();

                  var w=0,h=0,dpr=1,unit=300,cx=0,cy=0,count=0;
                  var refX,refY,posX,posY,scaleArr,velArr,seedArr;
                  var ringX=0,ringY=0,pointerX=0,pointerY=0,pointerActive=false,clickPulse=0;
                  var simTime=0,raf=0,lastTs=0,resizeTimer=0;
                  var NORM_SCALE=0.25,RING_WIDTH=0.011,RING_WIDTH2=0.078,RING_DISP=0.36;

                  // Bridson 泊松盘采样：全屏均匀蓝噪声分布，避免规则网格感与局部结团。
                  function init(){
                    dpr=Math.min(window.devicePixelRatio||1,2);
                    w=window.innerWidth;h=window.innerHeight;
                    canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);
                    canvas.style.width=w+'px';canvas.style.height=h+'px';
                    ctx.setTransform(dpr,0,0,dpr,0,0);
                    ctx.lineCap='round';
                    cx=w*0.5;cy=h*0.5;
                    unit=Math.max(220,Math.min(w,h)*0.5);

                    var pad=28,pw=w+pad*2,ph=h+pad*2;
                    var target=Math.max(850,Math.min(2350,Math.round((w*h)/480)));
                    var r=Math.max(17,Math.min(26,Math.sqrt((pw*ph)/(target*1.45))));
                    var cell=r/Math.SQRT2,gw=Math.ceil(pw/cell),gh=Math.ceil(ph/cell);
                    var grid=new Int32Array(gw*gh);
                    grid.fill(-1);
                    var pts=[],active=[];
                    var seed=48271;
                    function rnd(){seed=(seed*16807)%2147483647;return (seed-1)/2147483646;}
                    function insert(x,y){
                      var idx=pts.length>>1;
                      pts.push(x,y);
                      active.push(idx);
                      grid[Math.floor(y/cell)*gw+Math.floor(x/cell)]=idx;
                    }
                    insert(pw*0.5,ph*0.5);
                    var r2=r*r;
                    while(active.length>0&&(pts.length>>1)<2500){
                      var ai=Math.floor(rnd()*active.length);
                      var pi=active[ai],px=pts[pi*2],py=pts[pi*2+1],found=false;
                      for(var k=0;k<14;k++){
                        var ang=rnd()*6.2831853,rad=r*(1+rnd());
                        var nx=px+Math.cos(ang)*rad,ny=py+Math.sin(ang)*rad;
                        if(nx<0||nx>=pw||ny<0||ny>=ph)continue;
                        var gx=Math.floor(nx/cell),gy=Math.floor(ny/cell),ok=true;
                        var x0=Math.max(0,gx-2),x1=Math.min(gw-1,gx+2);
                        var y0=Math.max(0,gy-2),y1=Math.min(gh-1,gy+2);
                        for(var yy=y0;yy<=y1&&ok;yy++){
                          for(var xx=x0;xx<=x1;xx++){
                            var ni=grid[yy*gw+xx];
                            if(ni>=0){
                              var dx=pts[ni*2]-nx,dy=pts[ni*2+1]-ny;
                              if(dx*dx+dy*dy<r2){ok=false;break;}
                            }
                          }
                        }
                        if(ok){insert(nx,ny);found=true;break;}
                      }
                      if(!found){active[ai]=active[active.length-1];active.pop();}
                    }

                    count=pts.length>>1;
                    refX=new Float32Array(count);refY=new Float32Array(count);
                    posX=new Float32Array(count);posY=new Float32Array(count);
                    scaleArr=new Float32Array(count);velArr=new Float32Array(count);
                    seedArr=new Float32Array(count);
                    for(var i=0;i<count;i++){
                      var sx=pts[i*2]-pad,sy=pts[i*2+1]-pad;
                      refX[i]=((sx-cx)/unit)*NORM_SCALE;
                      refY[i]=((sy-cy)/unit)*NORM_SCALE;
                      seedArr[i]=0.88+rnd()*0.24;
                    }
                    stepSim(0.016,true);
                  }

                  function stepSim(dt,first){
                    simTime+=dt;
                    clickPulse*=Math.exp(-4.2*dt);
                    var tHalf=simTime*0.5;
                    var wx=snoise(simTime*0.42+94.234,0,0);
                    var wy=snoise(simTime*0.48+21.028,0,0);
                    if(pointerActive){
                      var mx=((pointerX-cx)/unit)*NORM_SCALE;
                      var my=((pointerY-cy)/unit)*NORM_SCALE;
                      var tx=mx*0.92+wx*0.016,ty=my*0.92+wy*0.016;
                      var follow=1-Math.exp(-5.6*dt);
                      ringX+=(tx-ringX)*follow;
                      ringY+=(ty-ringY)*follow;
                    }else{
                      var tx=wx*0.042,ty=wy*0.024;
                      var drift=first?1:(1-Math.exp(-2.1*dt));
                      ringX+=(tx-ringX)*drift;
                      ringY+=(ty-ringY)*drift;
                    }

                    var ringRadius=0.122+Math.sin(simTime)*0.014+Math.cos(simTime*2.5)*0.008+clickPulse;
                    var damp=Math.pow(0.8,dt*60);
                    var impulseScale=(1-damp)/0.2;
                    var scaleEase=1-damp;
                    var velEase=1-Math.pow(0.5,dt*60);

                    for(var i=0;i<count;i++){
                      var rx=refX[i],ry=refY[i];
                      var dxr=rx-ringX,dyr=ry-ringY;
                      var dist=Math.sqrt(dxr*dxr+dyr*dyr);
                      var noise0=snoise(rx*0.2+18.4924,ry*0.2+72.9744,tHalf*0.5);
                      var d1x=rx+noise0*0.005-ringX,d1y=ry+noise0*0.005-ringY;
                      var dist1=Math.sqrt(d1x*d1x+d1y*d1y);

                      var t=smoothstep(ringRadius-RING_WIDTH*2,ringRadius,dist)-smoothstep(ringRadius,ringRadius+RING_WIDTH,dist1);
                      var t2=smoothstep(ringRadius-RING_WIDTH2*2,ringRadius,dist)-smoothstep(ringRadius,ringRadius+RING_WIDTH2,dist1);
                      var t3=smoothstep(ringRadius+RING_WIDTH2,ringRadius,dist);
                      if(t<0)t=0;if(t2<0)t2=0;
                      t=t*t;
                      t2=t2*t2*t2;
                      t+=t2*3.1+t3*0.22;
                      t+=snoise(rx*30+11.4924,ry*30+12.9744,tHalf*0.5)*t3*0.32;
                      var nS=snoise(rx*2+18.4924,ry*2+72.9744,tHalf*0.5);
                      var bWave=(nS+1.35)*0.5;
                      t+=bWave*bWave*0.34;

                      var n1=snoise(rx*4+88.494,ry*4+32.4397,tHalf*0.35);
                      var n2=snoise(rx*4+50.904,ry*4+120.947,tHalf*0.35);
                      var n3=snoise(rx*20+18.4924,ry*20+72.9744,tHalf*0.5);
                      var n4=snoise(rx*20+50.904,ry*20+120.947,tHalf*0.5);
                      var cDist=dist<1?dist:1;
                      var dispX=n1*0.024+n3*0.004+Math.sin(rx*20+tHalf*4)*0.014*cDist;
                      var dispY=n2*0.024+n4*0.004+Math.cos(ry*20+tHalf*3)*0.014*cDist;

                      var push=Math.pow(t2,0.75)*RING_DISP;
                      var fxPush=-(ringX-(rx+dispX))*push;
                      var fyPush=-(ringY-(ry+dispY))*push;
                      if(first){
                        posX[i]=fxPush*4.2;
                        posY[i]=fyPush*4.2;
                        scaleArr[i]=t;
                        velArr[i]=t*0.5;
                      }else{
                        posX[i]=posX[i]*damp+fxPush*impulseScale;
                        posY[i]=posY[i]*damp+fyPush*impulseScale;
                        scaleArr[i]+=(t-scaleArr[i])*scaleEase;
                        velArr[i]+=(scaleArr[i]*0.5-velArr[i])*velEase;
                      }
                    }
                  }

                  // 绘制沿环径向朝向的圆角短棒（Antigravity 标志性胶囊粒子）。
                  function draw(){
                    ctx.clearRect(0,0,w,h);
                    var tHalf=simTime*0.5;
                    var baseLen=Math.max(2.6,Math.min(3.8,Math.min(w,h)*0.0034));
                    var invNorm=unit/NORM_SCALE;
                    for(var i=0;i<count;i++){
                      var sc=scaleArr[i]*seedArr[i];
                      if(sc<=0.18)continue;
                      var rx=refX[i],ry=refY[i];
                      var n1=snoise(rx*4+88.494,ry*4+32.4397,tHalf*0.35);
                      var n2=snoise(rx*4+50.904,ry*4+120.947,tHalf*0.35);
                      var fx=rx+n1*0.024+posX[i]*0.25;
                      var fy=ry+n2*0.024+posY[i]*0.25;
                      var sx=cx+fx*invNorm,sy=cy+fy*invNorm;
                      if(sx<-24||sx>w+24||sy<-24||sy>h+24)continue;

                      var vis=smoothstep(0.18,0.44,sc);
                      var len=sc*baseLen;
                      if(len>16)len=16;
                      var thick=len*0.37;
                      if(thick<1.15)thick=1.15;
                      var halfSeg=Math.max(0.35,(len-thick)*0.5);

                      var noiseAngle=snoise(fx*10+18.4924,fy*10+72.9744,simTime*0.85);
                      var theta=Math.atan2(fy-ringY,fx-ringX)+noiseAngle*0.45;
                      var ux=Math.cos(theta)*halfSeg,uy=Math.sin(theta)*halfSeg;

                      var nc=(snoise(fx*2+74.664,fy*2+91.556,tHalf)+1)*0.5;
                      var prog=smoothstep(0,0.78,Math.pow(nc,1.45));
                      var cIdx=Math.min(127,Math.max(0,(prog*127)|0));
                      var alpha=vis*Math.min(0.94,0.34+sc*0.24);

                      ctx.lineWidth=thick;
                      ctx.strokeStyle=PALETTE[cIdx];
                      ctx.globalAlpha=alpha;
                      ctx.beginPath();
                      ctx.moveTo(sx-ux,sy-uy);
                      ctx.lineTo(sx+ux,sy+uy);
                      ctx.stroke();
                    }
                    ctx.globalAlpha=1;
                  }

                  function step(ts){
                    raf=requestAnimationFrame(step);
                    var dt=lastTs?Math.min(0.05,(ts-lastTs)*0.001):0.016;
                    lastTs=ts;
                    if(dt<=0)dt=0.016;
                    stepSim(dt,false);
                    draw();
                  }
                  function start(){
                    cancelAnimationFrame(raf);lastTs=0;
                    if(!reduce&&!document.hidden)raf=requestAnimationFrame(step);
                  }
                  function onMove(e){
                    pointerX=e.clientX;pointerY=e.clientY;
                    pointerActive=true;
                  }
                  function onTouch(e){
                    if(e.touches&&e.touches.length>0){
                      pointerX=e.touches[0].clientX;pointerY=e.touches[0].clientY;
                      pointerActive=true;
                    }
                  }
                  window.addEventListener('pointermove',onMove,{passive:true});
                  window.addEventListener('mousemove',onMove,{passive:true});
                  window.addEventListener('touchstart',onTouch,{passive:true});
                  window.addEventListener('touchmove',onTouch,{passive:true});
                  window.addEventListener('pointerdown',function(){clickPulse=0.024;},{passive:true});
                  document.addEventListener('mouseleave',function(){pointerActive=false;});
                  window.addEventListener('blur',function(){pointerActive=false;});

                  init();draw();start();
                  if(mq&&mq.addEventListener){
                    mq.addEventListener('change',function(e){
                      reduce=e.matches;
                      if(reduce){cancelAnimationFrame(raf);draw();}else start();
                    });
                  }
                  window.addEventListener('resize',function(){
                    clearTimeout(resizeTimer);
                    resizeTimer=setTimeout(function(){
                      init();draw();start();
                    },140);
                  });
                  document.addEventListener('visibilitychange',function(){
                    if(document.hidden)cancelAnimationFrame(raf);else start();
                  });
                })();
                </script>
                </body>
                </html>
                """;
    }
}
