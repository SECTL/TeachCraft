using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// SECTL 云 KV 的 HTTP 客户端。只做请求与错误映射，不决定文档布局、不碰本地存储。
/// HttpClient 只由注入的 handler 构造，handler 生命周期归调用方；
/// token 与响应体不写日志、不进异常消息。
/// </summary>
public sealed class SectlCloudKvClient : ICloudKvClient
{
    private readonly HttpClient _client;
    private readonly SectlCloudOptions _options;
    private readonly ICloudAccountService _account;

    public SectlCloudKvClient(
        HttpMessageHandler handler,
        SectlCloudOptions options,
        ICloudAccountService account,
        TimeProvider? time = null)
    {
        _client = new HttpClient(handler, disposeHandler: false);
        _options = options;
        _account = account;
        // 保留签名供后续走查注入时钟；本客户端不做定时、不做重试等待。
        _ = time;
    }

    private const int ListPageSize = 100;
    private const int MaxListPages = 20;
    private const int MaxListEntries = 1000;

    private string Collection => $"{_options.BaseUrl.AbsoluteUri.TrimEnd('/')}/api/cloud/kv";

    public async Task<CloudKvEntry?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await SendWithAuthAsync(
                HttpMethod.Get, $"{Collection}/{Uri.EscapeDataString(key)}", contentFactory: null, ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // 部分网关把未找到包成 200 + 错误体，正文错误码只作次要信号。
            return IsNotFoundBody(body) ? null : ParseEntry(key, body);
        }
        catch (CloudAuthException ex) when (string.Equals(ex.ErrorCode, "not_found", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<CloudKvList> ListAsync(CancellationToken ct)
    {
        var all = new List<CloudKvEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var total = 0;

        for (var page = 0; page < MaxListPages; page++)
        {
            var url = $"{Collection}?offset={offset}&limit={ListPageSize}";
            using var response = await SendWithAuthAsync(
                HttpMethod.Get, url, contentFactory: null, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseList(body);
            total = parsed.Total;

            foreach (var entry in parsed.Entries)
            {
                if (seen.Add(entry.Key)) all.Add(entry);
            }

            // 按服务端原始条数推进：解析失败或被去重的条目仍占分页位置，否则会卡住或漏页。
            if (!parsed.HasMore || parsed.RawCount == 0)
                return new CloudKvList(all, false, total);

            offset += parsed.RawCount;
            if (all.Count >= MaxListEntries) break;
        }

        // has_more 在页数或条数上限处仍为真：宁可报错也不返回一份不完整的清单。
        throw new CloudAuthException(CloudErrorText.ListPageLimitExceeded, "list_page_limit");
    }

    public async Task PutAsync(string key, JsonElement value, CancellationToken ct)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new CloudAuthException(CloudErrorText.ServerError, "invalid_value");

        using var response = await SendWithAuthAsync(
            HttpMethod.Post,
            Collection,
            () => JsonContent(new { client_id = _options.ClientId, key, value }),
            ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var response = await SendWithAuthAsync(
            HttpMethod.Delete, $"{Collection}/{Uri.EscapeDataString(key)}", contentFactory: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 401 只重试一次：第二次向账号服务强制刷新（即使尚未临期），仍 401 就清掉无效会话并报登录失效。
    /// 没有循环、没有退避，避免把服务端故障放大成请求风暴。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthAsync(
        HttpMethod method,
        string url,
        Func<HttpContent?>? contentFactory,
        CancellationToken ct)
    {
        var token = await _account.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        var response = await SendOnceAsync(method, url, contentFactory?.Invoke(), token, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        var refreshed = await _account.ForceRefreshAccessTokenAsync(token, ct).ConfigureAwait(false);
        var retry = await SendOnceAsync(method, url, contentFactory?.Invoke(), refreshed, ct).ConfigureAwait(false);
        if (retry.StatusCode == HttpStatusCode.Unauthorized)
        {
            retry.Dispose();
            if (_account is SectlAccountService sectl)
                await sectl.InvalidateAfterUnauthorizedAsync(refreshed, ct).ConfigureAwait(false);
            throw new CloudAuthException(CloudErrorText.Unauthorized, "unauthorized");
        }
        return retry;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string url, HttpContent? content, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(url));
        if (content is not null) request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new CloudAuthException(CloudErrorText.Timeout, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudAuthException(CloudErrorText.NoNetwork, innerException: ex);
        }

        // 401 留给 SendWithAuthAsync 做唯一一次刷新重试，不在这里映射成异常。
        if (response.StatusCode == HttpStatusCode.Unauthorized) return response;
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var info = await ReadErrorInfoAsync(response, ct).ConfigureAwait(false);
            throw MapError(response.StatusCode, info);
        }
    }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        // 文档内容原样带过去：中文不转义，Get 回来的 GetRawText 才是原文。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static HttpContent JsonContent(object payload)
        => new StringContent(JsonSerializer.Serialize(payload, WireOptions), Encoding.UTF8, "application/json");

    private static async Task<ErrorInfo> ReadErrorInfoAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return default;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;

            string? code = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                code = error.GetString();

            var retryMs = 0;
            if (root.TryGetProperty("retry_after_ms", out var retry) && retry.ValueKind == JsonValueKind.Number
                && retry.TryGetInt32(out var parsed))
                retryMs = parsed;

            return new ErrorInfo(code, retryMs);
        }
        catch (Exception)
        {
            // 错误体读不出来就只按状态码映射，不把响应内容带进异常。
            return default;
        }
    }

    private static CloudAuthException MapError(HttpStatusCode status, ErrorInfo info)
    {
        if (string.Equals(info.Code, "cloud_service_disabled", StringComparison.Ordinal))
            return new CloudAuthException(CloudErrorText.CloudDisabled, info.Code);

        if (status == HttpStatusCode.TooManyRequests)
        {
            var seconds = info.RetryAfterMs > 0 ? Math.Max(1, info.RetryAfterMs / 1000) : 1;
            return new CloudAuthException(CloudErrorText.RateLimited(seconds), info.Code ?? "rate_limited");
        }

        if (status == HttpStatusCode.Unauthorized)
            return new CloudAuthException(CloudErrorText.Unauthorized, info.Code ?? "unauthorized");

        if (status == HttpStatusCode.Forbidden)
            return new CloudAuthException(CloudErrorText.Forbidden, info.Code ?? "forbidden");

        if (status == HttpStatusCode.NotFound)
            return new CloudAuthException(CloudErrorText.NotFound, "not_found");

        if (status == HttpStatusCode.RequestEntityTooLarge)
            return new CloudAuthException(CloudErrorText.PayloadTooLarge, info.Code ?? "payload_too_large");

        return new CloudAuthException(CloudErrorText.ServerError, info.Code ?? "server_error");
    }

    private static bool IsNotFoundBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // 正常 KV 响应带 value/key；只有纯错误体才按错误码判定，避免误伤文档自身的同名字段。
            if (root.TryGetProperty("value", out _) || root.TryGetProperty("key", out _)) return false;
            return root.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                   && string.Equals(error.GetString(), "not_found", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CloudKvEntry ParseEntry(string key, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new CloudKvEntry(key, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new CloudKvEntry(key, root.Clone());

            // 常规形状是 {client_id, key, value}；也容忍直接把 value 对象作为响应体。
            if (root.TryGetProperty("value", out var value))
                return new CloudKvEntry(key, value.ValueKind == JsonValueKind.Null ? null : value.Clone());

            return new CloudKvEntry(key, root.Clone());
        }
        catch (JsonException ex)
        {
            throw new CloudAuthException(CloudErrorText.ServerError, "invalid_json", ex);
        }
    }

    /// <summary>
    /// List 的返回形状未确认：kv_list、keys 数组、keys+values、data 包装、裸数组都兼容。
    /// has_more / total 缺失时按 false / 0 处理，由调用方决定是否继续翻页。
    /// </summary>
    private static ParsedList ParseList(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new ParsedList(Array.Empty<CloudKvEntry>(), false, 0, 0);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var hasMore = ReadBool(root, "has_more", "hasMore");
            var total = ReadInt(root, "total", "count");
            var array = FindArray(root, depth: 0);
            var rawCount = array?.GetArrayLength() ?? 0;
            var result = new List<CloudKvEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (array is not null)
            {
                foreach (var item in array.Value.EnumerateArray())
                {
                    var entry = ParseListItem(item);
                    if (entry is not null && seen.Add(entry.Key)) result.Add(entry);
                }
            }
            return new ParsedList(result, hasMore, total, rawCount);
        }
        catch (JsonException ex)
        {
            throw new CloudAuthException(CloudErrorText.ServerError, "invalid_json", ex);
        }
    }

    private static bool ReadBool(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in names)
        {
            if (!TryGetCI(obj, name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var b) && b,
                _ => false,
            };
        }
        return false;
    }

    private static int ReadInt(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0;
        foreach (var name in names)
        {
            if (!TryGetCI(obj, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var s)) return s;
        }
        return 0;
    }

    private static bool TryGetCI(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static JsonElement? FindArray(JsonElement root, int depth)
    {
        if (depth > 3) return null;
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        // SECTL 实际返回 {"kv_list":[{key,value,size,is_json,...}]}；其余为历史容错形状。
        foreach (var name in new[] { "kv_list", "items", "values", "entries", "kv", "keys", "data", "result" })
        {
            if (!root.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Array) return prop;
            if (prop.ValueKind == JsonValueKind.Object)
            {
                var nested = FindArray(prop, depth + 1);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static CloudKvEntry? ParseListItem(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var stringKey = item.GetString();
            return string.IsNullOrWhiteSpace(stringKey) ? null : new CloudKvEntry(stringKey!, null);
        }
        if (item.ValueKind != JsonValueKind.Object) return null;

        string? key = null;
        foreach (var name in new[] { "key", "name", "id" })
        {
            if (item.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.String)
            {
                key = candidate.GetString();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(key)) return null;

        JsonElement? value = null;
        if (item.TryGetProperty("value", out var raw) && raw.ValueKind != JsonValueKind.Null)
            value = raw.Clone();

        return new CloudKvEntry(key!, value);
    }

    private sealed record ParsedList(IReadOnlyList<CloudKvEntry> Entries, bool HasMore, int Total, int RawCount);

    private readonly record struct ErrorInfo(string? Code, int RetryAfterMs);
}
