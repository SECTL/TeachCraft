using System.Net;
using System.Text.Json;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 云 KV 客户端的请求形状与错误映射。全程假 handler + 假账号，不联网、不落盘。
/// </summary>
public sealed class SectlCloudKvClientTests
{
    private sealed class FakeAccount : ICloudAccountService
    {
        private readonly Queue<string> _tokens;

        public FakeAccount(params string[] tokens) => _tokens = new Queue<string>(tokens);

        public int Calls { get; private set; }
        public bool IsSignedIn => true;
        public string? LastSignOutNotice => null;
        public CloudAccount? Current => null;
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_tokens.Count > 0 ? _tokens.Dequeue() : "synthetic-token");
        }

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (SectlCloudKvClient Client, FakeSectlApiHandler Handler, FakeAccount Account) Create(
        params string[] tokens)
    {
        var handler = new FakeSectlApiHandler();
        var account = new FakeAccount(tokens.Length == 0 ? new[] { "synthetic-token" } : tokens);
        return (new SectlCloudKvClient(handler, SectlTestData.Options(), account), handler, account);
    }

    [Fact]
    public async Task Get_UsesGetOnKeyPathWithBearer_AndParsesValue()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv/synthetic.key", HttpStatusCode.OK,
            """{"client_id":"synthetic-client","key":"synthetic.key","value":{"name":"合成名单"}}""");

        var entry = await client.GetAsync("synthetic.key", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/cloud/kv/synthetic.key", request.Path);
        Assert.Equal("Bearer synthetic-token", request.Authorization);
        Assert.Null(request.ContentType);
        Assert.Equal("synthetic.key", entry!.Key);
        Assert.Equal("合成名单", entry.Value!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_UsesGetOnCollection_AndToleratesKeysOnly()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.OK, """{"keys":["synthetic.a","synthetic.b"]}""");

        var entries = (await client.ListAsync(CancellationToken.None)).Entries;

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/cloud/kv", request.Path);
        Assert.Equal(new[] { "synthetic.a", "synthetic.b" }, entries.Select(entry => entry.Key));
        Assert.All(entries, entry => Assert.Null(entry.Value));
    }

    [Fact]
    public async Task List_ToleratesItemsWithValues()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.OK,
            """{"items":[{"key":"synthetic.a","value":{"v":1}},{"key":"synthetic.b"}]}""");

        var entries = (await client.ListAsync(CancellationToken.None)).Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Value!.Value.GetProperty("v").GetInt32());
        Assert.Null(entries[1].Value);
    }

    [Fact]
    public async Task List_ParsesActualSectlKvListShape()
    {
        // SECTL 实际返回 {"kv_list":[{key,value,size,is_json,created_at,...}],"total":2,"has_more":false}
        // 不认识 kv_list 会让共享名单库永远显示为空（上传成功但看不到）。
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.OK, """
            {"kv_list":[{"key":"teachcraft.roster.v1.index","value":{"V":1,"Items":[]},"size":10,"is_json":true},
            {"key":"teachcraft.roster.v1.abc","value":{"v":1,"name":"示例班级"},"size":20,"is_json":true}],"total":2,"has_more":false}
            """);

        var entries = (await client.ListAsync(CancellationToken.None)).Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal("teachcraft.roster.v1.index", entries[0].Key);
        Assert.Equal(1, entries[0].Value!.Value.GetProperty("V").GetInt32());
        Assert.Equal("teachcraft.roster.v1.abc", entries[1].Key);
        Assert.Equal("示例班级", entries[1].Value!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_FollowsPaginationUntilHasMoreFalse()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Respond("/api/cloud/kv", (_, _) =>
        {
            calls++;
            var body = calls == 1
                ? """{"kv_list":[{"key":"synthetic.a"}],"total":2,"has_more":true}"""
                : """{"kv_list":[{"key":"synthetic.b"}],"total":2,"has_more":false}""";
            return Task.FromResult(FakeSectlApiHandler.Response(HttpStatusCode.OK, body));
        });

        var result = await client.ListAsync(CancellationToken.None);

        Assert.Equal(new[] { "synthetic.a", "synthetic.b" }, result.Entries.Select(entry => entry.Key));
        Assert.False(result.HasMore);
        Assert.Equal(2, result.Total);
        var gets = handler.Requests.Where(request => request.Method == "GET").ToArray();
        Assert.Equal(2, gets.Length);
        Assert.Equal("?offset=0&limit=100", gets[0].Query);
        Assert.Equal("?offset=1&limit=100", gets[1].Query);
    }

    [Fact]
    public async Task List_AdvancesOffsetByRawCountWhenItemsAreUnparseable()
    {
        var (client, handler, _) = Create();
        var calls = 0;
        handler.Respond("/api/cloud/kv", (_, _) =>
        {
            calls++;
            var body = calls == 1
                ? """{"kv_list":[{"key":"synthetic.a"},{"unparseable":true}],"total":2,"has_more":true}"""
                : """{"kv_list":[{"key":"synthetic.b"}],"total":2,"has_more":false}""";
            return Task.FromResult(FakeSectlApiHandler.Response(HttpStatusCode.OK, body));
        });

        var result = await client.ListAsync(CancellationToken.None);

        Assert.Equal(new[] { "synthetic.a", "synthetic.b" }, result.Entries.Select(entry => entry.Key));
        var gets = handler.Requests.Where(request => request.Method == "GET").ToArray();
        Assert.Equal(2, gets.Length);
        Assert.Equal("?offset=2&limit=100", gets[1].Query);
    }

    [Fact]
    public async Task List_StopsWhenPageReturnsZeroItems()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.OK,
            """{"kv_list":[],"total":0,"has_more":true}""");

        var result = await client.ListAsync(CancellationToken.None);

        Assert.Empty(result.Entries);
        Assert.Equal(1, handler.CountOf("/api/cloud/kv"));
    }

    [Fact]
    public async Task List_CapsAtMaxPages_WithChineseError()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.OK,
            """{"kv_list":[{"key":"synthetic.a"}],"total":9999,"has_more":true}""");

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ListAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.ListPageLimitExceeded, ex.Message);
        Assert.Equal(20, handler.CountOf("/api/cloud/kv"));
    }

    [Fact]
    public async Task Put_PostsClientIdKeyAndJsonObjectValue()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.OK, """{}""");
        using var value = JsonDocument.Parse("""{"v":1,"name":"合成文档"}""");

        await client.PutAsync("synthetic.key", value.RootElement, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/cloud/kv", request.Path);
        Assert.Equal("Bearer synthetic-token", request.Authorization);
        Assert.Equal("application/json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(SectlPlatformDefaults.ClientId, body.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("synthetic.key", body.RootElement.GetProperty("key").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("value").GetProperty("v").GetInt32());
        Assert.Equal("合成文档", body.RootElement.GetProperty("value").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_UsesDeleteOnKeyPathWithoutBody()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv/synthetic.key", HttpStatusCode.OK, """{}""");

        await client.DeleteAsync("synthetic.key", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("/api/cloud/kv/synthetic.key", request.Path);
        Assert.Equal("Bearer synthetic-token", request.Authorization);
        Assert.Null(request.ContentType);
        Assert.Equal("", request.Body);
    }

    [Fact]
    public async Task TooManyRequests_MapsSecondsFromRetryAfterMs()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.TooManyRequests,
            """{"error":"rate_limited","retry_after_ms":2500}""");

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ListAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.RateLimited(2), ex.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RepeatedRateLimit_DoesNotRetry()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.TooManyRequests,
            """{"error":"rate_limited","retry_after_ms":500}""");

        await Assert.ThrowsAsync<CloudAuthException>(() => client.ListAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Disabled_MapsToCloudDisabled()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.Forbidden, """{"error":"cloud_service_disabled"}""");

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ListAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.CloudDisabled, ex.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ServerError_MapsToServerError()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv", HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ListAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.ServerError, ex.Message);
    }

    [Fact]
    public async Task NotFound_ReturnsNull()
    {
        var handler = new FakeSectlApiHandler();
        handler.Respond("/api/cloud/kv/synthetic.missing", HttpStatusCode.NotFound, """{"error":"not_found"}""");
        var client = new SectlCloudKvClient(handler, SectlTestData.Options(), new FakeAccount("synthetic-token"));

        var entry = await client.GetAsync("synthetic.missing", CancellationToken.None);

        Assert.Null(entry);
    }

    [Fact]
    public async Task NotFound_WithDifferentBodyCode_StillReturnsNull()
    {
        var handler = new FakeSectlApiHandler();
        handler.Respond("/api/cloud/kv/synthetic.missing", HttpStatusCode.NotFound,
            """{"error":"key_missing","detail":"no such entry"}""");
        var client = new SectlCloudKvClient(handler, SectlTestData.Options(), new FakeAccount("synthetic-token"));

        var entry = await client.GetAsync("synthetic.missing", CancellationToken.None);

        Assert.Null(entry);
    }

    [Fact]
    public async Task NotFound_WrappedIn200ErrorBody_ReturnsNull()
    {
        var (client, handler, _) = Create();
        handler.Respond("/api/cloud/kv/synthetic.missing", HttpStatusCode.OK, """{"error":"not_found"}""");

        var entry = await client.GetAsync("synthetic.missing", CancellationToken.None);

        Assert.Null(entry);
        Assert.Equal(1, handler.CountOf("/api/cloud/kv/synthetic.missing"));
    }

    [Fact]
    public async Task Unauthorized_RefreshesOnceThenSucceeds()
    {
        var (client, handler, account) = Create("synthetic-old", "synthetic-new");
        var calls = 0;
        handler.Respond("/api/cloud/kv", (_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? FakeSectlApiHandler.Response(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""")
                : FakeSectlApiHandler.Response(HttpStatusCode.OK, """{"keys":["synthetic.a"]}"""));
        });

        var entries = (await client.ListAsync(CancellationToken.None)).Entries;

        Assert.Equal("synthetic.a", Assert.Single(entries).Key);
        Assert.Equal(2, account.Calls);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("Bearer synthetic-old", handler.Requests[0].Authorization);
        Assert.Equal("Bearer synthetic-new", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task SecondUnauthorized_ThrowsWithoutFurtherRetry()
    {
        var (client, handler, account) = Create("synthetic-old", "synthetic-new");
        handler.Respond("/api/cloud/kv", HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""");

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => client.ListAsync(CancellationToken.None));

        Assert.Equal(CloudErrorText.Unauthorized, ex.Message);
        Assert.Equal(2, account.Calls);
        Assert.Equal(2, handler.RequestCount);
    }
}
