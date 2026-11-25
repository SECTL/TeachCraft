using System.IO;
using System.Net;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 账号服务的单飞刷新、先落盘后返回、失败不误清凭据与退出清理。
/// 全程内存存储 + 假 handler，不碰真实网络与本机 %LOCALAPPDATA%。
/// </summary>
public sealed class SectlAccountServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-24T10:00:00Z");

    private sealed class RecordingTokenStore : ICloudTokenStore
    {
        private readonly List<string>? _events;
        private CloudTokens? _current;

        public RecordingTokenStore(List<string>? events = null) => _events = events;

        public List<CloudTokens> Saves { get; } = new();
        public int ClearCount { get; private set; }
        public string Path => "memory://tokens";

        /// <summary>模拟另一个写入者在本次 Save 之后抢先落盘。</summary>
        public Action<CloudTokens>? OnSave { get; set; }

        public Exception? SaveException { get; set; }

        public CloudTokens? Load() => _current;

        public void Save(CloudTokens tokens)
        {
            if (SaveException is not null) throw SaveException;
            _current = tokens;
            Saves.Add(new CloudTokens
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                TokenType = tokens.TokenType,
                ObtainedAtUtc = tokens.ObtainedAtUtc,
                ExpiresAtUtc = tokens.ExpiresAtUtc,
                Protector = tokens.Protector,
            });
            _events?.Add("token.save");
            OnSave?.Invoke(tokens);
        }

        public void Clear()
        {
            _current = null;
            ClearCount++;
            _events?.Add("token.clear");
        }

        public void Seed(CloudTokens tokens) => _current = tokens;
    }

    private sealed class MemoryAccountStore : ICloudAccountStore
    {
        private readonly List<string>? _events;
        private CloudAccount? _current;

        public MemoryAccountStore(List<string>? events = null) => _events = events;

        public int ClearCount { get; private set; }
        public string Path => "memory://account";

        public CloudAccount? Load() => _current;

        public void Save(CloudAccount account)
        {
            _current = account;
            _events?.Add("account.save");
        }

        public void Clear()
        {
            _current = null;
            ClearCount++;
            _events?.Add("account.clear");
        }

        public void Seed(CloudAccount account) => _current = account;
    }

    private static CloudTokens NearExpiryTokens(TimeProvider time, string refreshToken = "synthetic-refresh-old") => new()
    {
        AccessToken = "synthetic-access-old",
        RefreshToken = refreshToken,
        TokenType = "Bearer",
        ObtainedAtUtc = time.GetUtcNow().AddMinutes(-30),
        ExpiresAtUtc = time.GetUtcNow().AddMinutes(2),
        Protector = "none",
    };

    private static CloudAccount SampleAccount(TimeProvider time) => new(
        "synthetic-sub", "合成用户", new[] { "user:read", "cloud:read", "cloud:write" },
        SectlPlatformDefaults.ClientId, time.GetUtcNow());

    private static (
        SectlAccountService Service,
        FakeSectlApiHandler Handler,
        RecordingTokenStore Tokens,
        MemoryAccountStore Accounts,
        ManualTimeProvider Time) CreateService(
            List<string>? events = null,
            CloudTokens? seedTokens = null,
            CloudAccount? seedAccount = null)
    {
        var time = new ManualTimeProvider(Start);
        var tokens = new RecordingTokenStore(events);
        var accounts = new MemoryAccountStore(events);
        if (seedTokens is not null) tokens.Seed(seedTokens);
        if (seedAccount is not null) accounts.Seed(seedAccount);

        var handler = new FakeSectlApiHandler();
        var service = new SectlAccountService(handler, SectlTestData.Options(), tokens, accounts, time);
        return (service, handler, tokens, accounts, time);
    }

    [Fact]
    public async Task ValidToken_UsesZeroHttp()
    {
        var time = new ManualTimeProvider(Start);
        var fresh = new CloudTokens
        {
            AccessToken = "synthetic-valid",
            RefreshToken = "synthetic-refresh",
            ExpiresAtUtc = time.GetUtcNow().AddHours(1),
        };
        var (service, handler, _, _, _) = CreateService(seedTokens: fresh, seedAccount: SampleAccount(time));

        var access = await service.GetValidAccessTokenAsync(CancellationToken.None);

        Assert.Equal("synthetic-valid", access);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentGetValidAccessToken_RefreshesExactlyOnce()
    {
        var (service, handler, tokens, _, time) = CreateService(
            seedTokens: NearExpiryTokens(new ManualTimeProvider(Start)),
            seedAccount: SampleAccount(new ManualTimeProvider(Start)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handler.Respond("/api/oauth/refresh", async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return FakeSectlApiHandler.Response(
                HttpStatusCode.OK, SectlTestData.TokenJson("synthetic-access-new", "synthetic-refresh-new"));
        });

        var calls = Enumerable.Range(0, 20)
            .Select(_ => service.GetValidAccessTokenAsync(CancellationToken.None))
            .ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, value => Assert.Equal("synthetic-access-new", value));
        Assert.Equal(1, handler.CountOf("/api/oauth/refresh"));
        Assert.Equal("synthetic-refresh-new", tokens.Load()!.RefreshToken);
        Assert.True(time.GetUtcNow() >= Start);
    }

    [Fact]
    public async Task Refresh_PersistsRotatedRefreshTokenBeforeReturningAccessToken()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, _, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/refresh", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access-new", "synthetic-refresh-new"));

        var access = await service.GetValidAccessTokenAsync(CancellationToken.None);

        Assert.Equal("synthetic-access-new", access);
        var saved = Assert.Single(tokens.Saves);
        Assert.Equal("synthetic-access-new", saved.AccessToken);
        Assert.Equal("synthetic-refresh-new", saved.RefreshToken);
        Assert.Equal("synthetic-refresh-new", tokens.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ResponseWithoutNewRefreshToken_KeepsOldOne()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, _, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/refresh", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access-new"));

        await service.GetValidAccessTokenAsync(CancellationToken.None);

        Assert.Equal("synthetic-refresh-old", tokens.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Refresh_AdoptsNewerTokenWrittenByAnotherCaller()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, _, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handler.Respond("/api/oauth/refresh", async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return FakeSectlApiHandler.Response(
                HttpStatusCode.OK, SectlTestData.TokenJson("synthetic-access-new", "synthetic-refresh-new"));
        });

        tokens.OnSave = _ => tokens.Seed(new CloudTokens
        {
            AccessToken = "synthetic-access-elsewhere",
            RefreshToken = "synthetic-refresh-elsewhere",
            ExpiresAtUtc = time.GetUtcNow().AddHours(1),
        });

        var calls = Enumerable.Range(0, 20)
            .Select(_ => service.GetValidAccessTokenAsync(CancellationToken.None))
            .ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, handler.CountOf("/api/oauth/refresh"));
        Assert.Contains("synthetic-access-elsewhere", results);
    }

    [Fact]
    public async Task InvalidGrant_ClearsStoresAndSubsequentCallThrows()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/refresh", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.RefreshRevoked, ex.Message);
        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.False(service.IsSignedIn);
        Assert.Equal(1, tokens.ClearCount);
        Assert.Equal(1, accounts.ClearCount);

        var again = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));
        Assert.Contains("重新登录", again.Message);
    }

    [Fact]
    public async Task NetworkFailure_RetainsTokensAndMapsError()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Fail("/api/oauth/refresh", new HttpRequestException("synthetic network detail"));

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.NoNetwork, ex.Message);
        Assert.Equal("synthetic-refresh-old", tokens.Load()!.RefreshToken);
        Assert.NotNull(accounts.Load());
        Assert.True(service.IsSignedIn);
    }

    [Fact]
    public async Task MissingRefreshToken_ThrowsChineseReLoginError_WithoutInventingToken()
    {
        var time = new ManualTimeProvider(Start);
        var tokens = new RecordingTokenStore();
        tokens.Seed(new CloudTokens
        {
            AccessToken = "synthetic-access-old",
            RefreshToken = "",
            ExpiresAtUtc = time.GetUtcNow().AddMinutes(2),
        });
        var handler = new FakeSectlApiHandler();
        var accounts = new MemoryAccountStore();
        accounts.Seed(SampleAccount(time));
        var service = new SectlAccountService(
            handler, SectlTestData.Options(), tokens, accounts, time);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));

        Assert.Contains("登录已到期，请重新登录", ex.Message);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal("synthetic-access-old", tokens.Load()!.AccessToken);
        Assert.Empty(tokens.Saves);
    }

    [Fact]
    public async Task NoExpiry_ReturnsTokenWithoutRefresh()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, _, _, _) = CreateService(
            seedTokens: new CloudTokens { AccessToken = "synthetic-no-expiry", RefreshToken = "synthetic-refresh" },
            seedAccount: SampleAccount(time));

        var access = await service.GetValidAccessTokenAsync(CancellationToken.None);

        Assert.Equal("synthetic-no-expiry", access);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CompleteSignIn_StateMismatch_ThrowsBeforeAnyTokenHttp()
    {
        var (service, handler, tokens, accounts, _) = CreateService();
        var request = service.CreateSignInRequest();

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.CompleteSignInAsync(request, "synthetic-code", "wrong-state", CancellationToken.None));

        Assert.Equal(CloudErrorText.StateMismatch, ex.Message);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(tokens.Saves);
        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public async Task CompleteSignIn_PersistsTokensBeforeAccount_AndFallsBackDisplayName()
    {
        var events = new List<string>();
        var (service, handler, tokens, accounts, _) = CreateService(events: events);
        handler.Respond("/api/oauth/token", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access", "synthetic-refresh", 3600));
        handler.Respond("/api/oauth/userinfo", HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""");
        var request = service.CreateSignInRequest();

        var account = await service.CompleteSignInAsync(request, "synthetic-code", request.State, CancellationToken.None);

        Assert.Equal(new[] { "token.save", "account.save" }, events);
        Assert.Equal("已登录", account.DisplayName);
        Assert.Equal("synthetic-refresh", tokens.Load()!.RefreshToken);
        Assert.Equal(account, accounts.Load());
        Assert.True(service.IsSignedIn);
        Assert.Equal(account, service.Current);
    }

    [Fact]
    public async Task SignOut_ClearsLocalEvenWhenRevokeFails()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Fail("/api/oauth/logout", new HttpRequestException("synthetic revoke failure"));
        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        await service.SignOutAsync(CancellationToken.None);

        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.False(service.IsSignedIn);
        Assert.Equal(1, raised);
        Assert.NotNull(service.LastSignOutNotice);
        Assert.Contains("SECTL", service.LastSignOutNotice!);
    }

    [Fact]
    public async Task SignOut_Success_ClearsLocalWithoutNotice()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/logout", HttpStatusCode.OK, """{}""");

        await service.SignOutAsync(CancellationToken.None);

        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.Null(service.LastSignOutNotice);
        Assert.Equal(1, handler.CountOf("/api/oauth/logout"));
    }

    [Fact]
    public async Task InvalidGrant_RaisesStateChangedAndClearsState()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/refresh", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));

        Assert.True(raised >= 1);
        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public async Task TokenPersistFailure_DoesNotReturnNewAccessToken()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/refresh", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access-new", "synthetic-refresh-new"));
        tokens.SaveException = new IOException("synthetic persist failure");
        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.TokenPersistFailed, ex.Message);
        Assert.True(raised >= 1);
        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public async Task SignOutDuringRefresh_DoesNotResurrectToken()
    {
        var time = new ManualTimeProvider(Start);
        var (service, handler, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: SampleAccount(time));
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handler.Respond("/api/oauth/refresh", async (_, ct) =>
        {
            refreshStarted.TrySetResult();
            await releaseRefresh.Task.WaitAsync(ct).ConfigureAwait(false);
            return FakeSectlApiHandler.Response(
                HttpStatusCode.OK, SectlTestData.TokenJson("synthetic-access-new", "synthetic-refresh-new"));
        });
        handler.Respond("/api/oauth/logout", HttpStatusCode.OK, """{}""");

        var refresh = service.GetValidAccessTokenAsync(CancellationToken.None);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var signOut = service.SignOutAsync(CancellationToken.None);
        releaseRefresh.SetResult();

        await Task.WhenAll(refresh, signOut).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public void Constructor_OrphanAccountWithoutTokens_ClearsSignedInState()
    {
        var time = new ManualTimeProvider(Start);
        var (service, _, _, accounts, _) = CreateService(seedTokens: null, seedAccount: SampleAccount(time));

        Assert.False(service.IsSignedIn);
        Assert.Null(service.Current);
        Assert.Null(accounts.Load());
    }

    [Fact]
    public async Task Constructor_OrphanTokensWithoutAccount_ClearsTokensAndReportsSignedOut()
    {
        var time = new ManualTimeProvider(Start);
        var (service, _, tokens, accounts, _) = CreateService(
            seedTokens: NearExpiryTokens(time), seedAccount: null);

        Assert.False(service.IsSignedIn);
        Assert.Null(service.Current);
        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));
        Assert.Equal(CloudErrorText.Unauthorized, ex.Message);
    }

    [Fact]
    public async Task ExpiredAccessTokenWithoutRefreshToken_ClearsLocalStateAndRaisesChanged()
    {
        var time = new ManualTimeProvider(Start);
        var expired = new CloudTokens
        {
            AccessToken = "synthetic-expired",
            RefreshToken = "",
            ExpiresAtUtc = Start.AddMinutes(-1),
            Protector = "none",
        };
        var (service, _, tokens, accounts, _) = CreateService(
            seedTokens: expired, seedAccount: SampleAccount(time));
        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => service.GetValidAccessTokenAsync(CancellationToken.None));

        Assert.Contains("重新登录", ex.Message);
        Assert.Null(tokens.Load());
        Assert.Null(accounts.Load());
        Assert.False(service.IsSignedIn);
        Assert.True(raised >= 1);
    }

    [Fact]
    public async Task ForceRefresh_WhenNotNearExpiry_RefreshesToken()
    {
        var time = new ManualTimeProvider(Start);
        var validNotNearExpiry = new CloudTokens
        {
            AccessToken = "synthetic-revoked-early",
            RefreshToken = "synthetic-refresh-old",
            ExpiresAtUtc = Start.AddHours(1),
            Protector = "none",
        };
        var (service, handler, tokens, _, _) = CreateService(
            seedTokens: validNotNearExpiry, seedAccount: SampleAccount(time));
        handler.Respond("/api/oauth/refresh", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access-refreshed", "synthetic-refresh-new"));

        var refreshed = await service.ForceRefreshAccessTokenAsync(
            "synthetic-revoked-early", CancellationToken.None);

        Assert.Equal("synthetic-access-refreshed", refreshed);
        Assert.Equal(1, handler.CountOf("/api/oauth/refresh"));
        Assert.Equal("synthetic-access-refreshed", tokens.Load()!.AccessToken);
    }

    [Fact]
    public async Task KvClient_WithRealAccountService_ForcesRefreshOn401AndClearsOnDouble401()
    {
        var time = new ManualTimeProvider(Start);
        var valid = new CloudTokens
        {
            AccessToken = "synthetic-old",
            RefreshToken = "synthetic-refresh",
            ExpiresAtUtc = Start.AddHours(1),
            Protector = "none",
        };
        var tokens = new RecordingTokenStore();
        var accounts = new MemoryAccountStore();
        tokens.Seed(valid);
        accounts.Seed(SampleAccount(time));
        var handler = new FakeSectlApiHandler();
        var service = new SectlAccountService(handler, SectlTestData.Options(), tokens, accounts, time);
        var kv = new SectlCloudKvClient(handler, SectlTestData.Options(), service, time);

        handler.Respond("/api/oauth/refresh", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-new", "synthetic-refresh-2"));
        handler.Respond("/api/cloud/kv", HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""");

        var raised = 0;
        service.StateChanged += (_, _) => raised++;

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => kv.ListAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.Unauthorized, ex.Message);
        Assert.Equal(1, handler.CountOf("/api/oauth/refresh"));
        Assert.False(service.IsSignedIn);
        Assert.Null(tokens.Load());
        Assert.True(raised >= 1);
    }
}
