namespace TeachCraft.App.Cloud;

/// <summary>在回环地址上等待浏览器把授权结果送回来。</summary>
public interface IOAuthCallbackReceiver
{
    Task<OAuthCallback> WaitForCallbackAsync(Uri redirectUri, string expectedState, CancellationToken ct);
}
