namespace TeachCraft.Core.Cloud;

/// <summary>
/// SECTL 平台的公开常量。Client ID 是 public client 的公开标识（32 位 hex），
/// 平台不签发 client_secret，所以它随代码分发不需要任何保密处理；
/// 代码只判非空，绝不校验前缀。
/// </summary>
public static class SectlPlatformDefaults
{
    public const string ClientId = "6aefdc780032bb88a5c4";
    /// <summary>平台后台登记的回调模式是 http://localhost:*/callback；固定端口 53140，监听仍只绑回环地址。</summary>
    public const string RedirectUri = "http://localhost:53140/callback";
    public const string Scopes = "user:read cloud:read cloud:write";

    /// <summary>
    /// 浏览器授权 UI 所在主机（SPA）。appwrite.sectl.cn/oauth/authorize 是监控事件端点（405），
    /// 不能用于浏览器授权；浏览器必须先到 sectl.cn/login，登录后再跳 /oauth/authorize。
    /// </summary>
    public const string WebBaseUrl = "https://sectl.cn";

    /// <summary>API 端点主机。端点直接接 /api/...，不拼 /v1。</summary>
    public const string ApiBaseUrl = "https://appwrite.sectl.cn";

    /// <summary>
    /// 兼容别名，指向 API 主机。OAuth token / refresh / userinfo / logout 与云 KV 都走这里；
    /// 浏览器授权页走 <see cref="WebBaseUrl"/>，不要用它拼授权 URL。
    /// </summary>
    public static readonly Uri BaseUrl = new(ApiBaseUrl);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
