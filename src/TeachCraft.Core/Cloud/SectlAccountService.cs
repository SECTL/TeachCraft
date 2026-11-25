namespace TeachCraft.Core.Cloud;

/// <summary>
/// 账号状态服务。登录、刷新、退出都走这里，界面不自己拼 OAuth 请求。
///
/// 刷新用单飞闸门：并发调用只有一个真正发 HTTP，其余在闸门后重读凭据，
/// 采纳别人已经写好的新 token。凭据先落盘再返回 access token，
/// 这样进程被杀掉也不会出现「用了新 token 但旧 refresh token 没轮换」的窗口。
/// </summary>
public sealed class SectlAccountService : ICloudAccountService
{
    private const string ReLoginMessage = "登录已到期，请重新登录。";
    private const string RevokeNotice = "已退出本机登录；云端授权可能仍然有效，请到 SECTL 控制台撤销。";

    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    private readonly SectlOAuthClient _oauth;
    private readonly SectlCloudOptions _options;
    private readonly ICloudTokenStore _tokens;
    private readonly ICloudAccountStore _accounts;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CloudAccount? _current;

    public SectlAccountService(
        HttpMessageHandler handler,
        SectlCloudOptions options,
        ICloudTokenStore tokens,
        ICloudAccountStore accounts,
        TimeProvider? time = null)
    {
        _oauth = new SectlOAuthClient(handler, options, time);
        _options = options;
        _tokens = tokens;
        _accounts = accounts;
        _time = time ?? TimeProvider.System;
        _current = _accounts.Load();
        var loadedTokens = _tokens.Load();
        if (_current is not null && loadedTokens is null)
        {
            // 账号摘要仍在但凭据缺失或解密失败：启动即清掉孤立账号，避免「假已登录」卡死界面。
            _accounts.Clear();
            _current = null;
        }
        else if (_current is null && loadedTokens is not null)
        {
            // 凭据仍在但账号摘要缺失：界面显示未登录却仍能取到可用 token，一律 fail-closed 清掉孤立凭据。
            _tokens.Clear();
        }
    }

    public bool IsSignedIn => _current is not null;

    public CloudAccount? Current => _current;

    public event EventHandler? StateChanged;

    /// <summary>退出时远端撤销失败才填这句提示；成功或从未登录时为空。</summary>
    public string? LastSignOutNotice { get; private set; }

    public CloudSignInRequest CreateSignInRequest()
    {
        var verifier = Pkce.CreateVerifier();
        var challenge = Pkce.CreateChallenge(verifier);
        var state = Pkce.CreateState();

        return new CloudSignInRequest(
            _oauth.BuildAuthorizeUrl(state, challenge),
            state,
            verifier,
            _options.RedirectUri,
            _time.GetUtcNow());
    }

    public async Task<CloudAccount> CompleteSignInAsync(
        CloudSignInRequest request,
        string code,
        string state,
        CancellationToken cancellationToken)
    {
        // state 必须在任何 token 请求之前比对，否则回调伪造会白换一次 token。
        if (!string.Equals(request.State, state, StringComparison.Ordinal))
            throw new CloudAuthException(CloudErrorText.StateMismatch);

        var tokenSet = await _oauth.ExchangeCodeAsync(code, request.CodeVerifier, cancellationToken)
            .ConfigureAwait(false);

        // userinfo 只是拿个显示名，失败不能让已经换到的登录作废。
        SectlUserInfo userInfo;
        try
        {
            userInfo = await _oauth.GetUserInfoAsync(tokenSet.AccessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CloudAuthException)
        {
            userInfo = new SectlUserInfo();
        }

        var now = _time.GetUtcNow();
        var tokens = new CloudTokens
        {
            AccessToken = tokenSet.AccessToken,
            RefreshToken = tokenSet.RefreshToken ?? "",
            TokenType = tokenSet.TokenType,
            ObtainedAtUtc = now,
            ExpiresAtUtc = tokenSet.ExpiresAtUtc,
            Protector = "",
        };

        var account = new CloudAccount(
            string.IsNullOrWhiteSpace(userInfo.Sub) ? "unknown" : userInfo.Sub,
            userInfo.DisplayName,
            SplitScopes(string.IsNullOrWhiteSpace(tokenSet.Scope) ? _options.Scopes : tokenSet.Scope),
            _options.ClientId,
            now);

        // 与刷新、退出共用同一把闸门：登录写盘期间不能被并发的退出清掉。
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 先凭据后账号：中途失败时最多是「有 token 没显示名」，不会反过来。
            PersistTokensOrFailClosed(tokens);
            _accounts.Save(account);

            _current = account;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshGate.Release();
        }
        return account;
    }

    public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
        => AcquireAccessTokenAsync(forceRefresh: false, failedAccessToken: null, cancellationToken);

    public Task<string> ForceRefreshAccessTokenAsync(string failedAccessToken, CancellationToken cancellationToken)
        => AcquireAccessTokenAsync(forceRefresh: true, failedAccessToken: failedAccessToken, cancellationToken);

    internal async Task InvalidateAfterUnauthorizedAsync(
        string failedAccessToken, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _tokens.Load();
            if (current is not null && !string.Equals(current.AccessToken, failedAccessToken, StringComparison.Ordinal))
                return;

            if (_current is not null || current is not null || _accounts.Load() is not null)
            {
                ClearLocalState();
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<string> AcquireAccessTokenAsync(
        bool forceRefresh, string? failedAccessToken, CancellationToken cancellationToken)
    {
        var tokens = _tokens.Load();
        if (tokens is null)
        {
            ClearStaleAccountIfSignedIn();
            throw new CloudAuthException(CloudErrorText.Unauthorized);
        }

        if (!forceRefresh && !IsNearExpiry(tokens, _time.GetUtcNow()))
            return tokens.AccessToken;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等闸门期间别的调用可能已经刷新并写回，必须重读再决定要不要发请求。
            tokens = _tokens.Load();
            if (tokens is null)
            {
                ClearStaleAccountIfSignedIn();
                throw new CloudAuthException(CloudErrorText.Unauthorized);
            }

            if (forceRefresh && failedAccessToken is not null
                && !string.Equals(tokens.AccessToken, failedAccessToken, StringComparison.Ordinal))
            {
                return tokens.AccessToken;
            }

            var now = _time.GetUtcNow();
            if (!forceRefresh && !IsNearExpiry(tokens, now))
                return tokens.AccessToken;

            // 常规取票且没有到期时间时无从判断是否需要刷新，按原样返回，绝不臆造新 token；
            // 但服务端已明确返回 401（forceRefresh）时必须尝试刷新或清理失效登录态。
            if (!forceRefresh && tokens.ExpiresAtUtc is null)
                return tokens.AccessToken;

            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                // 若 access token 已过实际截止时间或已被服务端 401 拒绝，清掉本地登录态让界面回到未登录。
                if (forceRefresh || IsExpired(tokens, now))
                {
                    ClearLocalState();
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    throw new CloudAuthException(forceRefresh ? CloudErrorText.Unauthorized : ReLoginMessage);
                }

                throw new CloudAuthException(ReLoginMessage);
            }

            SectlTokenSet refreshed;
            try
            {
                refreshed = await _oauth.RefreshAsync(tokens.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CloudAuthException ex) when (string.Equals(ex.ErrorCode, "invalid_grant", StringComparison.Ordinal))
            {
                ClearLocalState();
                StateChanged?.Invoke(this, EventArgs.Empty);
                throw new CloudAuthException(CloudErrorText.RefreshRevoked, ex.ErrorCode, ex);
            }
            catch (CloudAuthException ex) when (forceRefresh && string.Equals(ex.ErrorCode, "unauthorized", StringComparison.Ordinal))
            {
                ClearLocalState();
                StateChanged?.Invoke(this, EventArgs.Empty);
                throw new CloudAuthException(CloudErrorText.Unauthorized, ex.ErrorCode, ex);
            }
            // 网络失败不在这里兜底：原凭据必须留着，异常原样往上抛。

            var updated = new CloudTokens
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                    ? tokens.RefreshToken
                    : refreshed.RefreshToken,
                TokenType = string.IsNullOrWhiteSpace(refreshed.TokenType)
                    ? tokens.TokenType
                    : refreshed.TokenType,
                ObtainedAtUtc = _time.GetUtcNow(),
                ExpiresAtUtc = refreshed.ExpiresAtUtc,
                Protector = tokens.Protector,
            };

            // 轮换后的 refresh token 必须先落盘，调用方才能拿到新的 access token。
            PersistTokensOrFailClosed(updated);
            return updated.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ClearStaleAccountIfSignedIn()
    {
        if (_current is null && _accounts.Load() is null) return;
        ClearLocalState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        // 与刷新、登录共用同一把闸门：等刷新写完之后再清，避免 token 被复活。
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 拿到闸门后才重读凭据：等待期间刷新可能已经写入轮换后的 token。
            var tokens = _tokens.Load();
            var revokeFailed = false;

            try
            {
                if (tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    linked.CancelAfter(LogoutTimeout);
                    await _oauth.LogoutAsync(tokens.AccessToken, tokens.RefreshToken, linked.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 远端撤销是尽力而为，失败也要把本机凭据清干净。
                revokeFailed = true;
            }

            ClearLocalState();
            LastSignOutNotice = revokeFailed ? RevokeNotice : null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool IsNearExpiry(CloudTokens tokens, DateTimeOffset now)
        => tokens.ExpiresAtUtc is { } expires && expires - now < RefreshThreshold;

    private static bool IsExpired(CloudTokens tokens, DateTimeOffset now)
        => tokens.ExpiresAtUtc is { } expires && expires <= now;

    private static string[] SplitScopes(string scopes)
        => scopes.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void PersistTokensOrFailClosed(CloudTokens tokens)
    {
        try
        {
            _tokens.Save(tokens);
        }
        catch (Exception ex)
        {
            // 凭据没落盘就绝不能把新 token 交出去：清干净并要求重新登录。
            ClearLocalState();
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw new CloudAuthException(CloudErrorText.TokenPersistFailed, "token_persist_failed", ex);
        }
    }

    private void ClearLocalState()
    {
        _tokens.Clear();
        _accounts.Clear();
        _current = null;
    }
}
