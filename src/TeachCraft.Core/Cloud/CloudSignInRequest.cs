namespace TeachCraft.Core.Cloud;

/// <summary>
/// 一次登录的起点。verifier 与 state 只留在内存里，回调回来时凭 state 配对；
/// CreatedAtUtc 用于判断这次请求是否已经等太久。
/// </summary>
public sealed record CloudSignInRequest(
    Uri AuthorizeUrl,
    string State,
    string CodeVerifier,
    string RedirectUri,
    DateTimeOffset CreatedAtUtc);
