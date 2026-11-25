using System.Net;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// OAuth 客户端的请求形状与错误映射。全部走 FakeSectlApiHandler，不碰真实网络。
/// </summary>
public sealed class SectlOAuthClientTests
{
    private static SectlOAuthClient CreateClient(FakeSectlApiHandler handler, TimeProvider? time = null)
        => new(handler, SectlTestData.Options(), time);

    [Fact]
    public void Pkce_Rfc7636AppendixB_MatchesKnownChallenge()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.CreateChallenge(verifier));
    }

    [Fact]
    public void Pkce_Generates43CharBase64UrlValues()
    {
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();

        Assert.Equal(43, verifier.Length);
        Assert.Equal(43, state.Length);
        Assert.NotEqual(verifier, state);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", verifier);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", state);
    }

    [Fact]
    public void TryCreate_BlankClientIdOrRedirectUri_ReturnsNull()
    {
        Assert.Null(SectlCloudOptions.TryCreate(null, SectlPlatformDefaults.RedirectUri, "synthetic-device"));
        Assert.Null(SectlCloudOptions.TryCreate("   ", SectlPlatformDefaults.RedirectUri, "synthetic-device"));
        Assert.Null(SectlCloudOptions.TryCreate(SectlPlatformDefaults.ClientId, "", "synthetic-device"));

        var options = SectlCloudOptions.TryCreate(
            SectlPlatformDefaults.ClientId, SectlPlatformDefaults.RedirectUri, "synthetic-device");
        Assert.NotNull(options);
        Assert.Equal(SectlPlatformDefaults.BaseUrl, options!.BaseUrl);
        Assert.Equal(SectlPlatformDefaults.Scopes, options.Scopes);
    }

    [Fact]
    public void BuildAuthorizeUrl_IsLoginFirstOnWebHost_WithEncodedAuthorizeRedirect()
    {
        var client = CreateClient(new FakeSectlApiHandler());

        var url = client.BuildAuthorizeUrl("synthetic-state", "synthetic-challenge");

        Assert.Equal("sectl.cn", url.Host);
        Assert.Equal("/login", url.AbsolutePath);
        Assert.StartsWith(
            "https://sectl.cn/login?redirect=%2Foauth%2Fauthorize%3Fclient_id%3D", url.AbsoluteUri);

        var redirect = ParseQuery(url.Query)["redirect"];
        Assert.StartsWith("/oauth/authorize?", redirect);

        var inner = ParseQuery(redirect[(redirect.IndexOf('?') + 1)..]);
        Assert.Equal(SectlPlatformDefaults.ClientId, inner["client_id"]);
        Assert.Equal(SectlPlatformDefaults.RedirectUri, inner["redirect_uri"]);
        Assert.Equal("code", inner["response_type"]);
        Assert.Equal("synthetic-challenge", inner["code_challenge"]);
        Assert.Equal("S256", inner["code_challenge_method"]);
        Assert.Equal(SectlPlatformDefaults.Scopes, inner["scope"]);
        Assert.Equal("synthetic-state", inner["state"]);
    }

    [Fact]
    public async Task TokenExchange_StaysOnApiHost()
    {
        var handler = new UriRecordingHandler();
        var client = new SectlOAuthClient(handler, SectlTestData.Options());

        await client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal("appwrite.sectl.cn", handler.LastRequestUri!.Host);
        Assert.Equal("/api/oauth/token", handler.LastRequestUri.AbsolutePath);
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));

    /// <summary>记录完整请求 URI（含主机），用于钉死 API 调用仍走 appwrite 主机。</summary>
    private sealed class UriRecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(FakeSectlApiHandler.Response(
                HttpStatusCode.OK, SectlTestData.TokenJson("synthetic-access")));
        }
    }

    [Fact]
    public async Task ExchangeCode_UsesFormUrlEncodedWithExactFields()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.OK, SectlTestData.TokenJson("synthetic-access"));
        var client = CreateClient(handler);

        var result = await client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/oauth/token", request.Path);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("authorization_code", request.Form["grant_type"]);
        Assert.Equal("synthetic-code", request.Form["code"]);
        Assert.Equal(SectlPlatformDefaults.ClientId, request.Form["client_id"]);
        Assert.Equal(SectlPlatformDefaults.RedirectUri, request.Form["redirect_uri"]);
        Assert.Equal("synthetic-verifier", request.Form["code_verifier"]);
        Assert.Equal("synthetic-device-uuid", request.Form["device_uuid"]);
        Assert.DoesNotContain("{", request.Body); // 不是 JSON
        Assert.Equal("synthetic-access", result.AccessToken);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task Refresh_UsesFormUrlEncodedWithRefreshToken()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/refresh", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access-new", "synthetic-refresh-new"));
        var client = CreateClient(handler);

        await client.RefreshAsync("synthetic-refresh-old", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/oauth/refresh", request.Path);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("refresh_token", request.Form["grant_type"]);
        Assert.Equal("synthetic-refresh-old", request.Form["refresh_token"]);
        Assert.Equal(SectlPlatformDefaults.ClientId, request.Form["client_id"]);
        Assert.Equal("synthetic-device-uuid", request.Form["device_uuid"]);
    }

    [Fact]
    public async Task ExchangeCode_UnknownFieldsIgnored_AndMissingRefreshTokenAllowed()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access", expiresIn: 7200));
        var client = CreateClient(handler);

        var result = await client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None);

        Assert.Equal("synthetic-access", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(7200, result.ExpiresIn);
        Assert.Equal("Bearer", result.TokenType);
    }

    [Fact]
    public async Task TokenSet_ToString_NeverLeaksTokens()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.OK,
            SectlTestData.TokenJson("synthetic-access-secret", "synthetic-refresh-secret"));
        var client = CreateClient(handler);

        var result = await client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None);
        var text = result.ToString();

        Assert.DoesNotContain("synthetic-access-secret", text);
        Assert.DoesNotContain("synthetic-refresh-secret", text);
    }

    [Fact]
    public async Task GetUserInfo_UsesBearer_AndFallsBackToPreferredUsername()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/userinfo", HttpStatusCode.OK,
            """{"sub":"synthetic-sub","preferred_username":"synthetic-user","future_field":42}""");
        var client = CreateClient(handler);

        var info = await client.GetUserInfoAsync("synthetic-access", CancellationToken.None);

        Assert.Equal("synthetic-sub", info.Sub);
        Assert.Equal("synthetic-user", info.DisplayName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/oauth/userinfo", request.Path);
        Assert.Equal("Bearer synthetic-access", request.Authorization);
    }

    [Fact]
    public async Task GetUserInfo_NoNameFields_FallsBackToLoggedIn()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/userinfo", HttpStatusCode.OK, """{"sub":"synthetic-sub"}""");
        var client = CreateClient(handler);

        var info = await client.GetUserInfoAsync("synthetic-access", CancellationToken.None);

        Assert.Equal("已登录", info.DisplayName);
    }

    [Fact]
    public async Task Logout_UsesBearer_AndFormBody()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/logout", HttpStatusCode.OK, """{}""");
        var client = CreateClient(handler);

        await client.LogoutAsync("synthetic-access", "synthetic-refresh", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/oauth/logout", request.Path);
        Assert.Equal("Bearer synthetic-access", request.Authorization);
        Assert.Equal("synthetic-refresh", request.Form["refresh_token"]);
        Assert.Equal(SectlPlatformDefaults.ClientId, request.Form["client_id"]);
    }

    [Fact]
    public async Task RateLimited_MapsRetryAfterMillisecondsToSeconds()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.TooManyRequests, """{"error":"rate_limited","retry_after_ms":5000}""");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None));

        Assert.Equal(CloudErrorText.RateLimited(5), ex.Message);
    }

    [Fact]
    public async Task CloudServiceDisabled_MapsToCloudDisabled()
    {
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.Forbidden, """{"error":"cloud_service_disabled"}""");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None));

        Assert.Equal(CloudErrorText.CloudDisabled, ex.Message);
    }

    [Fact]
    public async Task UnauthorizedAndServerError_MapToChineseMessages()
    {
        var unauthorized = CreateClient(new FakeSectlApiHandler().Respond(
            "/api/oauth/userinfo", HttpStatusCode.Unauthorized, """{"error":"invalid_token"}"""));
        var ex401 = await Assert.ThrowsAsync<CloudAuthException>(
            () => unauthorized.GetUserInfoAsync("synthetic-access", CancellationToken.None));
        Assert.Equal(CloudErrorText.Unauthorized, ex401.Message);

        var serverError = CreateClient(new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
        var ex500 = await Assert.ThrowsAsync<CloudAuthException>(
            () => serverError.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None));
        Assert.Equal(CloudErrorText.ServerError, ex500.Message);
    }

    [Fact]
    public async Task NetworkFailure_MapsToNoNetwork_WithoutLeakingDetails()
    {
        var handler = new FakeSectlApiHandler().Fail(
            "/api/oauth/token", new HttpRequestException("synthetic network detail"));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None));

        Assert.Equal(CloudErrorText.NoNetwork, ex.Message);
        Assert.DoesNotContain("synthetic network detail", ex.Message);
    }

    [Fact]
    public async Task ExpiresIn_UsesInjectedTimeProvider()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-24T10:00:00Z"));
        var handler = new FakeSectlApiHandler().Respond(
            "/api/oauth/token", HttpStatusCode.OK, SectlTestData.TokenJson("synthetic-access", expiresIn: 600));
        var client = CreateClient(handler, time);

        var result = await client.ExchangeCodeAsync("synthetic-code", "synthetic-verifier", CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-11-24T10:10:00Z"), result.ExpiresAtUtc);
    }
}
