using System.Net;
using System.Net.Http;
using System.Text;
using TeachCraft.Core.Updating;

namespace TeachCraft.Tests;

/// <summary>
/// 测试用 HTTP 桩。任何未被显式接管的请求都会抛异常，
/// 于是「跑测试不需要网络」变成一条会被打红的断言，而不是一句承诺。
/// </summary>
internal sealed class GuardedHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RequestedUrls { get; } = new();

    public GuardedHttpHandler Respond(string url, HttpStatusCode status, string body, string contentType = "application/json")
    {
        _routes[url] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        return this;
    }

    public GuardedHttpHandler RespondWith(string url, Func<HttpResponseMessage> factory)
    {
        _routes[url] = factory;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        RequestedUrls.Add(url);

        cancellationToken.ThrowIfCancellationRequested();

        if (_routes.TryGetValue(url, out var factory))
            return Task.FromResult(factory());

        throw new InvalidOperationException($"测试中出现了真实网络请求：{url}");
    }
}

/// <summary>
/// 检查更新服务。全部走注入的 handler，绝不触碰真实网络，也绝不碰真实的 %LOCALAPPDATA%。
/// </summary>
public sealed class UpdateCheckServiceTests
{
    // 与 UpdateCheckService.DefaultFeedUrl 完全一致（含 per_page 查询串），
    // URL 对不上会让桩抛「真实网络请求」，测试会误判成业务失败。
    private const string FeedUrl = UpdateCheckService.DefaultFeedUrl;

    private static readonly string FeedJson = """
    [
      { "tag_name": "v1.18.0", "draft": false, "prerelease": false, "assets": [] },
      { "tag_name": "v1.17.0", "draft": false, "prerelease": false, "assets": [] }
    ]
    """;

    [Fact]
    public async Task Check_ReturnsHigherVersion()
    {
        var handler = new GuardedHttpHandler().Respond(FeedUrl, HttpStatusCode.OK, FeedJson);
        var service = new UpdateCheckService(handler);

        var result = await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.True(result.IsSuccess);
        Assert.Equal("v1.18.0", result.Best!.TagName);
    }

    [Fact]
    public async Task Check_NeverContactsAnyOtherHost()
    {
        var handler = new GuardedHttpHandler().Respond(FeedUrl, HttpStatusCode.OK, FeedJson);
        var service = new UpdateCheckService(handler);

        await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.All(handler.RequestedUrls, url => Assert.StartsWith("https://api.github.com/", url));
    }

    [Fact]
    public async Task Check_Maps403ToRateLimitReason()
    {
        var handler = new GuardedHttpHandler().Respond(FeedUrl, (HttpStatusCode)403, "rate limited");
        var service = new UpdateCheckService(handler);

        var result = await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.False(result.IsSuccess);
        Assert.Contains("频繁", result.Failure!);
    }

    [Fact]
    public async Task Check_Maps407ToProxyLogin()
    {
        var handler = new GuardedHttpHandler().Respond(FeedUrl, HttpStatusCode.ProxyAuthenticationRequired, "");
        var service = new UpdateCheckService(handler);

        var result = await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.Contains("认证", result.Failure!);
    }

    [Fact]
    public async Task Check_MapsHtml200ToBlockedByPortal()
    {
        var handler = new GuardedHttpHandler().Respond(FeedUrl, HttpStatusCode.OK, "<html>login</html>", "text/html");
        var service = new UpdateCheckService(handler);

        var result = await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.Contains("浏览器", result.Failure!);
    }

    [Fact]
    public async Task Check_MapsNetworkFailure()
    {
        var handler = new GuardedHttpHandler().RespondWith(FeedUrl,
            () => throw new HttpRequestException("down", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        var service = new UpdateCheckService(handler);

        var result = await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.Contains("连不上网", result.Failure!);
    }

    [Fact]
    public async Task Check_MapsTimeout()
    {
        var handler = new GuardedHttpHandler().RespondWith(FeedUrl, () => throw new TaskCanceledException("slow"));
        var service = new UpdateCheckService(handler);

        var result = await service.CheckAsync(BuildIdentity.Parse("1.17.0.sc.win-x64"));

        Assert.Contains("超时", result.Failure!);
    }
}

/// <summary>
/// 本机更新状态。必须原子写：写到一半崩掉不能留下半截 JSON。
/// 目录一律用注入的临时目录，绝不碰开发者真实的 %LOCALAPPDATA%。
/// </summary>
public sealed class UpdateStateStoreTests : IDisposable
{
    private readonly string _root;

    public UpdateStateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TeachCraft.Tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 清理失败不影响结论 */ }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyState()
    {
        var store = new UpdateStateStore(_root);

        var state = store.Load();

        Assert.Null(state.LastCheckUtc);
        Assert.Empty(state.SkippedVersions);
    }

    [Fact]
    public void Save_CreatesDirectoryThenRoundTrips()
    {
        var store = new UpdateStateStore(_root);

        store.Save(new UpdateState(DateTimeOffset.Parse("2026-11-03T10:00:00Z"), new[] { "1.17.0-preview.9" }));
        var reloaded = store.Load();

        Assert.Equal(DateTimeOffset.Parse("2026-11-03T10:00:00Z"), reloaded.LastCheckUtc);
        Assert.Contains("1.17.0-preview.9", reloaded.SkippedVersions);
    }

    [Fact]
    public void Load_CorruptFile_DegradesToEmptyWithoutThrowing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "state.json"), "{\"LastCheckUtc\":");
        var store = new UpdateStateStore(_root);

        var state = store.Load();

        Assert.Null(state.LastCheckUtc);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var store = new UpdateStateStore(_root);

        store.Save(new UpdateState(DateTimeOffset.UtcNow, Array.Empty<string>()));

        var leftovers = Directory.GetFiles(_root, "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void SaveTwice_ProducesReadableFullDocument()
    {
        var store = new UpdateStateStore(_root);

        store.Save(new UpdateState(DateTimeOffset.UtcNow, new[] { "1.17.0-preview.1" }));
        store.Save(new UpdateState(DateTimeOffset.UtcNow, new[] { "1.17.0-preview.2" }));

        var reloaded = store.Load();
        Assert.Single(reloaded.SkippedVersions);
        Assert.Equal("1.17.0-preview.2", reloaded.SkippedVersions[0]);
    }
}

/// <summary>
/// 离线保证的结构断言。
///
/// 允许「用注入的 handler 建 client」（UpdateCheckService 与 UpdateDownloader 都这么做，
/// 这正是可测性的来源）；禁止的是**自己新建 handler**——那会绕过测试桩。
/// </summary>
public sealed class NoAdHocHttpClientTests
{
    [Fact]
    public void OnlySharedHandlersCreatesHttpHandlers()
    {
        // 允许「用注入的 handler 建 client」（这正是可测性的来源，UpdateCheckService /
        // UpdateDownloader 都这么做）；禁止的是**自己新建 handler**——那会绕过测试桩，
        // 「跑测试不需要网络」就成了空话。
        var owners = new[] { "SharedHandlers.cs" };
        var offenders = RepoRoot
            .ReadAll("src", ".cs")
            .Where(f => !owners.Contains(Path.GetFileName(f.Path)))
            .Where(f => f.Text.Contains("new HttpClientHandler(", StringComparison.Ordinal)
                     || f.Text.Contains("new System.Net.Http.HttpClientHandler(", StringComparison.Ordinal)
                     || f.Text.Contains("new SocketsHttpHandler(", StringComparison.Ordinal)
                     || f.Text.Contains("new System.Net.Http.SocketsHttpHandler(", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void HttpClientIsOnlyConstructedFromInjectedHandlers()
    {
        // 三处 new HttpClient( 都必须是 (handler, ...) 或 (SharedHandlers.Default, ...)，
        // 不允许出现无参构造（那会用默认 handler，绕过桩）。
        var sites = RepoRoot
            .ReadAll(Path.Combine("src", "TeachCraft.Core", "Updating"), ".cs")
            .SelectMany(f => f.Text.Split('\n').Select(l => (f.Path, Line: l.Trim())))
            .Where(x => x.Line.Contains("new HttpClient(", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(sites);
        foreach (var (path, line) in sites)
        {
            Assert.True(
                line.Contains("handler", StringComparison.Ordinal) || line.Contains("SharedHandlers", StringComparison.Ordinal),
                $"{path} 里出现了用默认 handler 构造的 HttpClient：{line}");
        }
    }
}
