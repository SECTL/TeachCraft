using System.Net;
using System.Text;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>一次 HTTP 请求的快照，供断言请求形状。</summary>
internal sealed class SectlRequestRecord
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string Query { get; init; } = "";
    public string Body { get; init; } = "";
    public string? Authorization { get; init; }
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 本轮唯一的 HTTP 网络替身。按路径接管，未接管的请求直接抛异常——
/// 「测试不联网」因此是一条会被打红的断言，而不是一句承诺。
/// </summary>
internal sealed class FakeSectlApiHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes =
        new(StringComparer.Ordinal);
    private readonly List<SectlRequestRecord> _requests = new();

    public IReadOnlyList<SectlRequestRecord> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public int RequestCount
    {
        get { lock (_gate) return _requests.Count; }
    }

    public int CountOf(string path)
    {
        lock (_gate) return _requests.Count(record => string.Equals(record.Path, path, StringComparison.Ordinal));
    }

    public FakeSectlApiHandler Respond(string path, HttpStatusCode status, string body)
        => Respond(path, (_, _) => Task.FromResult(Response(status, body)));

    public FakeSectlApiHandler Respond(
        string path,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> factory)
    {
        lock (_gate) _routes[path] = factory;
        return this;
    }

    public FakeSectlApiHandler Fail(string path, Exception error)
        => Respond(path, (_, _) => Task.FromException<HttpResponseMessage>(error));

    public static HttpResponseMessage Response(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var contentType = request.Content?.Headers.ContentType?.MediaType;

        lock (_gate)
        {
            _requests.Add(new SectlRequestRecord
            {
                Method = request.Method.Method,
                Path = path,
                Query = request.RequestUri?.Query ?? "",
                Body = body,
                Authorization = request.Headers.Authorization?.ToString(),
                ContentType = contentType,
                Form = ParseForm(body, contentType),
            });
        }

        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? factory;
        lock (_gate) _routes.TryGetValue(path, out factory);

        if (factory is null)
            throw new InvalidOperationException($"测试中出现未接管的请求：{path}");

        return await factory(request, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseForm(string body, string? contentType)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.Equals(contentType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return result;

        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index < 0) continue;
            var key = Uri.UnescapeDataString(part[..index].Replace('+', ' '));
            var value = Uri.UnescapeDataString(part[(index + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }
}

/// <summary>可手动推进的 TimeProvider。测试里不引入自定义时钟接口。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public ManualTimeProvider(DateTimeOffset now) => _ticks = now.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
}

internal static class SectlTestData
{
    public static SectlCloudOptions Options(string deviceUuid = "synthetic-device-uuid") => new(
        SectlPlatformDefaults.BaseUrl,
        SectlPlatformDefaults.ClientId,
        SectlPlatformDefaults.RedirectUri,
        SectlPlatformDefaults.Scopes,
        deviceUuid);

    public static string TokenJson(
        string accessToken,
        string? refreshToken = null,
        int expiresIn = 3600,
        string scope = "user:read cloud:read cloud:write")
    {
        var refresh = refreshToken is null ? "" : $"\"refresh_token\": \"{refreshToken}\",\n  ";
        return "{\n  \"access_token\": \"" + accessToken + "\",\n  " + refresh
               + "\"token_type\": \"Bearer\",\n  \"expires_in\": " + expiresIn
               + ",\n  \"scope\": \"" + scope + "\",\n  \"unexpected_future_field\": {\"ignored\": true}\n}";
    }
}
