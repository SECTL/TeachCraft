namespace TeachCraft.App.Cloud;

/// <summary>回调请求里携带的 OAuth 参数。Error / ErrorDescription 非空表示授权端返回了错误。</summary>
public sealed record OAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// 解析回环回调地址。只认路径恰为 /callback 的请求，state 必须按序数比较一致，
/// 且必须带授权码；任何不合法都返回 false 并给出可直接进状态栏的中文原因。
/// </summary>
public static class OAuthCallbackParser
{
    private const string CallbackPath = "/callback";
    private const string StateMismatchMessage = "登录校验失败，请重新发起登录。";

    public static bool TryParse(
        Uri requestUri,
        string expectedState,
        out OAuthCallback callback,
        out string? error)
    {
        callback = new OAuthCallback(null, null, null, null);
        error = null;

        if (requestUri is null || !requestUri.IsAbsoluteUri)
        {
            error = "登录回调地址无效。";
            return false;
        }

        if (!string.Equals(requestUri.AbsolutePath, CallbackPath, StringComparison.Ordinal))
        {
            error = "登录回调路径不正确。";
            return false;
        }

        var query = ParseQuery(requestUri.Query);
        query.TryGetValue("code", out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("error", out var oauthError);
        query.TryGetValue("error_description", out var errorDescription);

        if (state is null || !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            error = StateMismatchMessage;
            // state 不一致可能是 CSRF，授权码一律丢弃，绝不交给上层当成成功结果。
            callback = new OAuthCallback(null, state, StateMismatchMessage, errorDescription);
            return false;
        }

        if (string.IsNullOrEmpty(code))
        {
            error = string.IsNullOrEmpty(oauthError)
                ? "登录回调缺少授权码。"
                : (errorDescription ?? oauthError);
            callback = new OAuthCallback(null, state, oauthError ?? "missing_code", errorDescription);
            return false;
        }

        callback = new OAuthCallback(code, state, null, null);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Decode(key)] = Decode(value);
        }

        return result;
    }

    // 查询串里 '+' 表示空格；%XX 交给 Uri 解码。先替换 '+' 再解码，%2B 才能还原成 '+'。
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
