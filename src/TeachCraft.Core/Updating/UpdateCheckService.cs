using System.Net;
using System.Net.Http;

namespace TeachCraft.Core.Updating;

/// <summary>一次检查的结果。失败时给出整句中文原因，成功时给出最高候选版本。</summary>
public sealed record UpdateCheckResult(bool IsSuccess, ReleaseEntry? Best, IReadOnlyList<ReleaseEntry> Candidates, string? Failure)
{
    public static UpdateCheckResult Ok(ReleaseEntry? best, IReadOnlyList<ReleaseEntry> candidates)
        => new(true, best, candidates, null);

    public static UpdateCheckResult Fail(string reason)
        => new(false, null, Array.Empty<ReleaseEntry>(), reason);
}

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default);
}

/// <summary>
/// 从 GitHub Releases 读取版本列表并挑出可升级的候选。
///
/// 约束：
///   - 只读 api.github.com 的 releases 列表，不调用 /latest（它永远解析到最新正式版，预览永远看不到）。
///   - 不发送任何本机标识，不持久化 Cookie。
///   - HttpClient 由外部注入且应全进程复用；构造时只接受 HttpMessageHandler，
///     不接受现成的 HttpClient，这样测试的 GuardedHttpHandler 无法被绕过。
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    public const string DefaultFeedUrl = "https://api.github.com/repos/Evan0148/TeachCraft/releases?per_page=30";

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient(SharedHandlers.Default, disposeHandler: false)
        {
            // 检查更新不该让用户等太久；超时由 CancellationTokenSource 统一控制。
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TeachCraft-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    });

    private readonly HttpClient _client;
    private readonly string _feedUrl;
    private readonly string _action;

    public UpdateCheckService(HttpMessageHandler handler, string? feedUrl = null, string action = "检查更新")
        : this(new HttpClient(handler, disposeHandler: false), feedUrl, action)
    {
    }

    private UpdateCheckService(HttpClient client, string? feedUrl, string action)
    {
        _client = client;
        _feedUrl = feedUrl ?? DefaultFeedUrl;
        _action = action;
    }

    /// <summary>生产用实例（复用进程内唯一的 HttpClient）。</summary>
    public static UpdateCheckService CreateDefault()
        => new(SharedClient.Value, null, "检查更新");

    public async Task<UpdateCheckResult> CheckAsync(BuildIdentity current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync(_feedUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Fail(UpdateErrorText.ForStatus(response.StatusCode, _action));

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!LooksLikeJson(body))
                return UpdateCheckResult.Fail(UpdateErrorText.ForNonJsonBody(_action));

            List<ReleaseEntry> entries;
            try
            {
                entries = UpdateFeed.Parse(body).ToList();
            }
            catch (System.Text.Json.JsonException)
            {
                return UpdateCheckResult.Fail(UpdateErrorText.ForNonJsonBody(_action));
            }

            var candidates = UpdateFeed.SelectCandidates(entries, current);
            return UpdateCheckResult.Ok(candidates.FirstOrDefault(), candidates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动取消，不需要给失败原因。
            return UpdateCheckResult.Fail(string.Empty);
        }
        catch (Exception ex)
        {
            var reason = UpdateErrorText.DescribeForUi(ex, _action);
            return UpdateCheckResult.Fail(reason);
        }
    }

    private static bool LooksLikeJson(string body)
    {
        var span = body.AsSpan().TrimStart();
        return span.Length > 0 && (span[0] == '[' || span[0] == '{');
    }
}
