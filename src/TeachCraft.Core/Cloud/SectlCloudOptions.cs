namespace TeachCraft.Core.Cloud;

/// <summary>
/// 一次云端连接所需的全部配置。client id / redirect uri 都是公开标识，不是密钥。
/// </summary>
public sealed record SectlCloudOptions(
    Uri BaseUrl,
    string ClientId,
    string RedirectUri,
    string Scopes,
    string DeviceUuid)
{
    /// <summary>配置不全时返回 null，由调用方给出「尚未配置」的中文提示。</summary>
    public static SectlCloudOptions? TryCreate(
        string? clientId,
        string? redirectUri,
        string deviceUuid,
        Uri? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri)) return null;

        return new SectlCloudOptions(
            baseUrl ?? SectlPlatformDefaults.BaseUrl,
            clientId.Trim(),
            redirectUri.Trim(),
            SectlPlatformDefaults.Scopes,
            deviceUuid);
    }
}
