using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// SECTL 的 OAuth 客户端。只做 HTTP 与错误映射，不碰本地存储、不决定登录态。
/// HttpClient 只由注入的 handler 构造，handler 的生命周期归调用方，这里不负责释放。
/// 任何 token 与响应体都不写日志、不进异常消息。
/// </summary>
public sealed class SectlOAuthClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly SectlCloudOptions _options;
    private readonly TimeProvider _time;

    public SectlOAuthClient(HttpMessageHandler handler, SectlCloudOptions options, TimeProvider? time = null)
    {
        _client = new HttpClient(handler, disposeHandler: false);
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 浏览器打开的地址：先到 sectl.cn/login，登录后由 SPA 跳到 /oauth/authorize。
    /// appwrite.sectl.cn/oauth/authorize 是监控事件端点（405），不能用于浏览器授权。
    /// </summary>
    public Uri BuildAuthorizeUrl(string state, string codeChallenge)
    {
        var pairs = new[]
        {
            ("client_id", _options.ClientId),
            ("redirect_uri", _options.RedirectUri),
            ("response_type", "code"),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            ("scope", _options.Scopes),
            ("state", state),
        };

        var query = string.Join("&", pairs.Select(pair
            => $"{Uri.EscapeDataString(pair.Item1)}={Uri.EscapeDataString(pair.Item2)}"));
        var authorizePath = $"/oauth/authorize?{query}";

        return new Uri(
            $"{SectlPlatformDefaults.WebBaseUrl}/login?redirect={Uri.EscapeDataString(authorizePath)}");
    }

    public async Task<SectlTokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = codeVerifier,
            ["device_uuid"] = _options.DeviceUuid,
        };
        return await PostTokenAsync("api/oauth/token", fields, ct).ConfigureAwait(false);
    }

    public async Task<SectlTokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
            ["device_uuid"] = _options.DeviceUuid,
        };
        return await PostTokenAsync("api/oauth/refresh", fields, ct).ConfigureAwait(false);
    }

    public async Task<SectlUserInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get, "api/oauth/userinfo", content: null, accessToken, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<UserInfoResponseDto>(body, JsonOptions)?.ToUserInfo()
                   ?? new SectlUserInfo();
        }
        catch (JsonException ex)
        {
            throw new CloudAuthException(CloudErrorText.ServerError, innerException: ex);
        }
    }

    public async Task LogoutAsync(string accessToken, string refreshToken, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
        };
        using var content = new FormUrlEncodedContent(fields);
        using var response = await SendAsync(
            HttpMethod.Post, "api/oauth/logout", content, accessToken, ct).ConfigureAwait(false);
    }

    private string Root => _options.BaseUrl.AbsoluteUri.TrimEnd('/');

    private async Task<SectlTokenSet> PostTokenAsync(
        string relativePath, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await SendAsync(
            HttpMethod.Post, relativePath, content, accessToken: null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        TokenResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TokenResponseDto>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CloudAuthException(CloudErrorText.ServerError, innerException: ex);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.AccessToken))
            throw new CloudAuthException(CloudErrorText.ServerError);

        return dto.ToTokenSet(_time.GetUtcNow());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        string? accessToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri($"{Root}/{relativePath}"));
        if (content is not null) request.Content = content;
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var info = await ReadErrorInfoAsync(response, ct).ConfigureAwait(false);
            throw MapError(response.StatusCode, info);
        }
    }

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
            return new CloudAuthException(CloudErrorText.RateLimited(seconds), info.Code);
        }

        if (status == HttpStatusCode.Unauthorized)
            return new CloudAuthException(CloudErrorText.Unauthorized, info.Code);

        if (status == HttpStatusCode.Forbidden)
            return new CloudAuthException(CloudErrorText.Forbidden, info.Code);

        if (status == HttpStatusCode.NotFound)
            return new CloudAuthException(CloudErrorText.NotFound, info.Code);

        if (status == HttpStatusCode.BadRequest
            && string.Equals(info.Code, "invalid_grant", StringComparison.Ordinal))
            return new CloudAuthException(CloudErrorText.RefreshRevoked, info.Code);

        return new CloudAuthException(CloudErrorText.ServerError, info.Code);
    }

    private readonly record struct ErrorInfo(string? Code, int RetryAfterMs);
}
