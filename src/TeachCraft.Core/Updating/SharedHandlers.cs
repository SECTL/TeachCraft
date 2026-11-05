namespace TeachCraft.Core.Updating;

/// <summary>
/// 全进程共享的出站 HTTP 处理器。
///
/// 只在这里创建一次，界面层拿到的是同一个实例——
/// 每次点击新建 HttpClient/Handler 会耗尽 socket，
/// 而「生产代码里不许出现 new HttpClientHandler」这条守护测试也要求集中在一处。
/// </summary>
public static class SharedHandlers
{
    private static readonly Lazy<HttpMessageHandler> _default = new(() =>
        new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

    public static HttpMessageHandler Default => _default.Value;
}
