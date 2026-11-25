using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 命名空间文档存储的端到端行为。HTTP 走 T2 的假 handler，
/// 在其公开路由 API 上搭一个内存 KV，不联网、不落盘、不碰真实凭据。
/// </summary>
public sealed class SectlCloudDocumentStoreTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-24T10:00:00Z");

    private sealed class TokenAccount : ICloudAccountService
    {
        public bool IsSignedIn => true;
        public string? LastSignOutNotice => null;
        public CloudAccount? Current => null;
        public event EventHandler? StateChanged { add { } remove { } }

        public CloudSignInRequest CreateSignInRequest() => throw new NotSupportedException();

        public Task<CloudAccount> CompleteSignInAsync(
            CloudSignInRequest request, string code, string state, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("synthetic-token");

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>在 FakeSectlApiHandler 上搭出的有状态 KV；键是动态的，路由在写入时注册。</summary>
    private sealed class KvApi
    {
        private readonly FakeSectlApiHandler _handler = new();
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failPut = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failDelete = new(StringComparer.Ordinal);
        private int _pageSize;

        public KvApi(string indexKey)
        {
            _handler.Respond("/api/cloud/kv", async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (_pageSize <= 0)
                        return FakeSectlApiHandler.Response(HttpStatusCode.OK,
                            JsonSerializer.Serialize(new { keys = _values.Keys.ToArray() }));

                    var offset = ReadQueryInt(request.RequestUri?.Query, "offset");
                    var limit = _pageSize;
                    var keys = _values.Keys.OrderBy(key => key, StringComparer.Ordinal)
                        .Skip(offset).Take(limit).ToArray();
                    return FakeSectlApiHandler.Response(HttpStatusCode.OK,
                        JsonSerializer.Serialize(new
                        {
                            kv_list = keys,
                            total = _values.Count,
                            has_more = offset + keys.Length < _values.Count,
                        }));
                }

                var body = await request.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var key = doc.RootElement.GetProperty("key").GetString()!;
                if (_failPut.Contains(key))
                    return FakeSectlApiHandler.Response(HttpStatusCode.InternalServerError, """{"error":"boom"}""");

                _values[key] = doc.RootElement.GetProperty("value").GetRawText();
                RegisterKey(key);
                return FakeSectlApiHandler.Response(HttpStatusCode.OK, """{}""");
            });
            RegisterKey(indexKey);
        }

        public FakeSectlApiHandler Handler => _handler;
        public IReadOnlyDictionary<string, string> Values => _values;
        public int PostCount => _handler.Requests.Count(request => request.Method == "POST");

        public void FailPut(string key) => _failPut.Add(key);
        public void FailDelete(string key) => _failDelete.Add(key);

        /// <summary>让集合 GET 按 offset/limit 分页；0 表示不分页（保持旧测试的单页形状）。</summary>
        public void EnablePagination(int pageSize) => _pageSize = pageSize;

        private static int ReadQueryInt(string? query, string name)
        {
            foreach (var part in (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (string.Equals(pair[0], name, StringComparison.Ordinal) && int.TryParse(pair[1], out var value))
                    return value;
            }
            return 0;
        }

        public void Seed(string key, string valueJson)
        {
            _values[key] = valueJson;
            RegisterKey(key);
        }

        /// <summary>模拟索引丢失：值没了，但路由仍在，GET 会返回 404。</summary>
        public void RemoveDirect(string key) => _values.Remove(key);

        private void RegisterKey(string key)
        {
            var path = "/api/cloud/kv/" + Uri.EscapeDataString(key);
            _handler.Respond(path, (request, _) =>
            {
                if (request.Method == HttpMethod.Delete)
                {
                    if (_failDelete.Contains(key))
                        return Task.FromResult(FakeSectlApiHandler.Response(
                            HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
                    _values.Remove(key);
                    return Task.FromResult(FakeSectlApiHandler.Response(HttpStatusCode.OK, """{}"""));
                }

                if (_values.TryGetValue(key, out var value))
                    return Task.FromResult(FakeSectlApiHandler.Response(HttpStatusCode.OK,
                        "{\"key\":" + JsonSerializer.Serialize(key) + ",\"value\":" + value + "}"));

                return Task.FromResult(FakeSectlApiHandler.Response(
                    HttpStatusCode.NotFound, """{"error":"not_found"}"""));
            });
        }
    }

    private static (SectlCloudDocumentStore Store, KvApi Kv, ManualTimeProvider Time) CreateStore(
        CloudDocumentNamespace? ns = null)
    {
        var chosen = ns ?? CloudDocumentNamespace.SeatGrid;
        var kv = new KvApi(chosen.IndexKey);
        var time = new ManualTimeProvider(Start);
        var store = new SectlCloudDocumentStore(
            kv.Handler, SectlTestData.Options(), new TokenAccount(), chosen, time);
        return (store, kv, time);
    }

    private static string SampleJson(string name = "合成座位表")
        => "{\"name\":" + JsonSerializer.Serialize(name) + ",\"kind\":\"seatgrid.daily\",\"students\":[]}";

    private static string JsonOfSize(int targetBytes, bool random)
    {
        const string shell = "{\"name\":\"合成\",\"body\":\"\"}";
        var bodyLength = targetBytes - Encoding.UTF8.GetByteCount(shell);
        var body = random
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(bodyLength * 3 / 4 + 16))[..bodyLength]
            : new string('x', bodyLength);
        return "{\"name\":\"合成\",\"body\":\"" + body + "\"}";
    }

    [Fact]
    public async Task Create_Generates32HexId_AndWritesDocBeforeIndex()
    {
        var (store, kv, _) = CreateStore();
        var json = SampleJson();

        var info = await store.PutAsync(null, "合成座位表", CloudDocumentNamespace.SeatGrid.Kind, json, 0, CancellationToken.None);

        Assert.Equal(32, info.Id.Length);
        Assert.All(info.Id, ch => Assert.True(Uri.IsHexDigit(ch)));
        Assert.Equal(1, info.Revision);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), info.Bytes);
        Assert.Equal(Start, info.UpdatedAtUtc);
        Assert.True(kv.Values.ContainsKey(CloudDocumentNamespace.SeatGrid.KeyPrefix + info.Id));
        Assert.True(kv.Values.ContainsKey(CloudDocumentNamespace.SeatGrid.IndexKey));

        var posts = kv.Handler.Requests.Where(request => request.Method == "POST").ToArray();
        Assert.Equal(2, posts.Length);
        using var first = JsonDocument.Parse(posts[0].Body);
        using var second = JsonDocument.Parse(posts[1].Body);
        Assert.Equal(CloudDocumentNamespace.SeatGrid.KeyPrefix + info.Id,
            first.RootElement.GetProperty("key").GetString());
        Assert.Equal(CloudDocumentNamespace.SeatGrid.IndexKey,
            second.RootElement.GetProperty("key").GetString());
        Assert.Equal("seatgrid.daily",
            second.RootElement.GetProperty("value").GetProperty("Items")[0].GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task Overwrite_WithMatchingRevision_IncrementsRevision()
    {
        var (store, _, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);

        var updated = await store.PutAsync(
            created.Id, "合成座位表（改）", "seatgrid.daily", SampleJson("合成座位表（改）"), 1, CancellationToken.None);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(2, updated.Revision);
        Assert.Equal("合成座位表（改）", updated.Name);
        var loaded = await store.GetAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, loaded!.Info.Revision);
        Assert.Contains("合成座位表（改）", loaded.Json);
    }

    [Fact]
    public async Task StaleRevision_ThrowsConflict_AndWritesNothing()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        var postsBefore = kv.PostCount;

        var ex = await Assert.ThrowsAsync<CloudConflictException>(() => store.PutAsync(
            created.Id, "合成座位表", "seatgrid.daily", SampleJson(), 99, CancellationToken.None));

        Assert.Equal(1, ex.Remote.Revision);
        Assert.Equal(postsBefore, kv.PostCount);
    }

    [Fact]
    public async Task CreateWithExistingId_ThrowsConflict_AndWritesNothing()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        var postsBefore = kv.PostCount;

        await Assert.ThrowsAsync<CloudConflictException>(() => store.PutAsync(
            created.Id, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None));

        Assert.Equal(postsBefore, kv.PostCount);
    }

    [Fact]
    public async Task MissingDocument_ReturnsNull()
    {
        var (store, kv, _) = CreateStore();
        var missingId = new string('a', 32);
        kv.Handler.Respond("/api/cloud/kv/" + CloudDocumentNamespace.SeatGrid.KeyPrefix + missingId,
            HttpStatusCode.NotFound, """{"error":"not_found"}""");

        Assert.Null(await store.GetAsync(missingId, CancellationToken.None));
    }

    [Fact]
    public async Task OrphanDocument_AppearsAfterIndexLoss()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        kv.RemoveDirect(CloudDocumentNamespace.SeatGrid.IndexKey);

        var listed = await store.ListAsync(CancellationToken.None);

        var recovered = Assert.Single(listed);
        Assert.Equal(created.Id, recovered.Id);
        Assert.Equal("合成座位表", recovered.Name);
        Assert.Equal("seatgrid.daily", recovered.Kind);
        Assert.Equal(1, recovered.Revision);
    }

    [Fact]
    public async Task OrphanDocument_WithVField_RecoversRevisionFromV()
    {
        var (store, kv, _) = CreateStore();
        var id = new string('b', 32);
        kv.Seed(CloudDocumentNamespace.SeatGrid.KeyPrefix + id,
            "{\"name\":\"合成座位表\",\"kind\":\"seatgrid.daily\",\"v\":7,\"students\":[]}");
        kv.RemoveDirect(CloudDocumentNamespace.SeatGrid.IndexKey);

        var recovered = Assert.Single(await store.ListAsync(CancellationToken.None));

        Assert.Equal(id, recovered.Id);
        Assert.Equal(7, recovered.Revision);
    }

    [Fact]
    public async Task OrphanDocument_WithLegacyVersionField_StillRecoversRevision()
    {
        var (store, kv, _) = CreateStore();
        var id = new string('c', 32);
        kv.Seed(CloudDocumentNamespace.SeatGrid.KeyPrefix + id,
            "{\"name\":\"合成座位表\",\"kind\":\"seatgrid.daily\",\"version\":5,\"students\":[]}");
        kv.RemoveDirect(CloudDocumentNamespace.SeatGrid.IndexKey);

        var recovered = Assert.Single(await store.ListAsync(CancellationToken.None));

        Assert.Equal(id, recovered.Id);
        Assert.Equal(5, recovered.Revision);
    }

    [Fact]
    public async Task StaleIndexEntry_Dropped()
    {
        var (store, kv, _) = CreateStore();
        var stale = Enumerable.Range(0, 1).Select(i => new
        {
            Id = i.ToString("x32"),
            Name = "合成旧条目",
            Kind = "seatgrid.daily",
            UpdatedAtUtc = Start.ToString("O"),
            Bytes = 10,
            Revision = 1,
        }).ToArray();
        kv.Seed(CloudDocumentNamespace.SeatGrid.IndexKey,
            JsonSerializer.Serialize(new { V = 1, Items = stale }));

        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptIndex_IsTolerated_AndRebuiltFromKeys()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        kv.Seed(CloudDocumentNamespace.SeatGrid.IndexKey, "not-json-at-all");

        var listed = await store.ListAsync(CancellationToken.None);

        Assert.Equal(created.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task PlainBoundary_At40KB_StoresRawJson()
    {
        var (store, kv, _) = CreateStore();
        var json = JsonOfSize(SectlCloudDocumentStore.CompressThresholdBytes, random: false);

        var info = await store.PutAsync(null, "合成座位表", "seatgrid.daily", json, 0, CancellationToken.None);

        Assert.Equal(SectlCloudDocumentStore.CompressThresholdBytes, info.Bytes);
        var stored = kv.Values[CloudDocumentNamespace.SeatGrid.KeyPrefix + info.Id];
        Assert.DoesNotContain("Encoded", stored);
        Assert.Equal(json, (await store.GetAsync(info.Id, CancellationToken.None))!.Json);
    }

    [Fact]
    public async Task Above40KB_UsesGzipEnvelope_AndRoundTrips()
    {
        var (store, kv, _) = CreateStore();
        var json = JsonOfSize(SectlCloudDocumentStore.CompressThresholdBytes + 200, random: false);

        var info = await store.PutAsync(null, "合成座位表", "seatgrid.daily", json, 0, CancellationToken.None);

        var stored = kv.Values[CloudDocumentNamespace.SeatGrid.KeyPrefix + info.Id];
        using var envelope = JsonDocument.Parse(stored);
        Assert.Equal("gzip+base64", envelope.RootElement.GetProperty("Encoded").GetString());
        Assert.Equal(json, (await store.GetAsync(info.Id, CancellationToken.None))!.Json);
    }

    [Fact]
    public async Task IncompressibleDocumentBetween40And64KB_StoresRawInsteadOfRejecting()
    {
        var (store, kv, _) = CreateStore();
        // 随机 base64 几乎压不动：原文 64000 字节仍在 64 KB 硬上限内，但 gzip 信封会超过 60 KB。
        var json = JsonOfSize(64_000, random: true);

        var info = await store.PutAsync(null, "合成座位表", "seatgrid.daily", json, 0, CancellationToken.None);

        Assert.Equal(64_000, info.Bytes);
        var stored = kv.Values[CloudDocumentNamespace.SeatGrid.KeyPrefix + info.Id];
        Assert.DoesNotContain("Encoded", stored);
        Assert.Equal(json, (await store.GetAsync(info.Id, CancellationToken.None))!.Json);
    }

    [Fact]
    public async Task Above60KBStored_ThrowsPayloadTooLarge_BeforeAnyHttp()
    {
        var (store, kv, _) = CreateStore();
        var json = JsonOfSize(70_000, random: true);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => store.PutAsync(
            null, "合成座位表", "seatgrid.daily", json, 0, CancellationToken.None));

        Assert.Equal(CloudErrorText.PayloadTooLarge, ex.Message);
        Assert.Equal(0, kv.Handler.RequestCount);
    }

    [Fact]
    public async Task KeyTooLong_ThrowsBeforeAnyHttp()
    {
        var longNs = new CloudDocumentNamespace(
            new string('k', 250) + ".", new string('i', 250) + ".index", "synthetic.kind");
        var (store, kv, _) = CreateStore(longNs);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => store.PutAsync(
            null, "合成座位表", longNs.Kind, SampleJson(), 0, CancellationToken.None));

        Assert.Equal(CloudErrorText.KeyTooLong, ex.Message);
        Assert.Equal(0, kv.Handler.RequestCount);
    }

    [Fact]
    public async Task DocumentLimit_ThrowsKeyLimitReached_WithoutWriting()
    {
        var (store, kv, _) = CreateStore();
        var items = Enumerable.Range(0, SectlCloudDocumentStore.SoftDocumentLimit).Select(i => new
        {
            Id = i.ToString("x32"),
            Name = "合成" + i,
            Kind = "seatgrid.daily",
            UpdatedAtUtc = Start.ToString("O"),
            Bytes = 10,
            Revision = 1,
        }).ToArray();
        kv.Seed(CloudDocumentNamespace.SeatGrid.IndexKey,
            JsonSerializer.Serialize(new { V = 1, Items = items }));

        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => store.PutAsync(
            null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None));

        Assert.Equal(CloudErrorText.KeyLimitReached, ex.Message);
        Assert.Equal(0, kv.PostCount);
    }

    [Fact]
    public async Task Delete_RemovesDocAndIndex()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);

        await store.DeleteAsync(created.Id, CancellationToken.None);

        Assert.False(kv.Values.ContainsKey(CloudDocumentNamespace.SeatGrid.KeyPrefix + created.Id));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Delete_WithFailedIndexWrite_ReportsCounts()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        kv.FailPut(CloudDocumentNamespace.SeatGrid.IndexKey);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => store.DeleteAsync(created.Id, CancellationToken.None));

        Assert.Contains("成功 1 项，失败 1 项", ex.Message);
    }

    [Fact]
    public async Task DeleteAll_RemovesEverything()
    {
        var (store, kv, _) = CreateStore();
        await store.PutAsync(null, "合成座位表甲", "seatgrid.daily", SampleJson("合成座位表甲"), 0, CancellationToken.None);
        await store.PutAsync(null, "合成座位表乙", "seatgrid.daily", SampleJson("合成座位表乙"), 0, CancellationToken.None);

        await store.DeleteAllAsync(CancellationToken.None);

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        using var index = JsonDocument.Parse(kv.Values[CloudDocumentNamespace.SeatGrid.IndexKey]);
        Assert.Empty(index.RootElement.GetProperty("Items").EnumerateArray());
    }

    [Fact]
    public async Task List_AndDeleteAll_UseAllPages()
    {
        var (store, kv, _) = CreateStore();
        var first = await store.PutAsync(null, "合成座位表甲", "seatgrid.daily", SampleJson("合成座位表甲"), 0, CancellationToken.None);
        await store.PutAsync(null, "合成座位表乙", "seatgrid.daily", SampleJson("合成座位表乙"), 0, CancellationToken.None);
        var last = await store.PutAsync(null, "合成座位表丙", "seatgrid.daily", SampleJson("合成座位表丙"), 0, CancellationToken.None);

        // 4 个键（3 份文档 + 索引）每页只给 2 个，单页会漏掉最后一份文档。
        kv.EnablePagination(2);

        var listed = await store.ListAsync(CancellationToken.None);

        Assert.Equal(3, listed.Count);
        Assert.Contains(listed, item => item.Name == "合成座位表丙");
        Assert.Equal(2, kv.Handler.Requests.Count(request =>
            request.Method == "GET" && request.Path == "/api/cloud/kv"));

        await store.DeleteAllAsync(CancellationToken.None);

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(CloudDocumentNamespace.SeatGrid.KeyPrefix + first.Id, kv.Values.Keys);
        Assert.DoesNotContain(CloudDocumentNamespace.SeatGrid.KeyPrefix + last.Id, kv.Values.Keys);
    }

    [Fact]
    public async Task DeleteAll_WithPartialFailure_ReportsCounts()
    {
        var (store, kv, _) = CreateStore();
        var first = await store.PutAsync(null, "合成座位表甲", "seatgrid.daily", SampleJson("合成座位表甲"), 0, CancellationToken.None);
        var second = await store.PutAsync(null, "合成座位表乙", "seatgrid.daily", SampleJson("合成座位表乙"), 0, CancellationToken.None);
        kv.FailDelete(CloudDocumentNamespace.SeatGrid.KeyPrefix + first.Id);

        var ex = await Assert.ThrowsAsync<CloudAuthException>(
            () => store.DeleteAllAsync(CancellationToken.None));

        Assert.Contains("成功 2 项，失败 1 项", ex.Message);
        Assert.True(kv.Values.ContainsKey(CloudDocumentNamespace.SeatGrid.KeyPrefix + first.Id));
        Assert.False(kv.Values.ContainsKey(CloudDocumentNamespace.SeatGrid.KeyPrefix + second.Id));
    }

    [Fact]
    public async Task BothNamespaces_UseTheirOwnPrefixes()
    {
        var (seatStore, seatKv, _) = CreateStore(CloudDocumentNamespace.SeatGrid);
        var (rosterStore, rosterKv, _) = CreateStore(CloudDocumentNamespace.Roster);

        var seat = await seatStore.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        var roster = await rosterStore.PutAsync(
            null, "合成名单", "teachcraft.roster.v1", "{\"name\":\"合成名单\",\"kind\":\"teachcraft.roster.v1\"}",
            0, CancellationToken.None);

        Assert.Contains(CloudDocumentNamespace.SeatGrid.KeyPrefix + seat.Id, seatKv.Values.Keys);
        Assert.Contains(CloudDocumentNamespace.SeatGrid.IndexKey, seatKv.Values.Keys);
        Assert.Contains(CloudDocumentNamespace.Roster.KeyPrefix + roster.Id, rosterKv.Values.Keys);
        Assert.Contains(CloudDocumentNamespace.Roster.IndexKey, rosterKv.Values.Keys);
        Assert.DoesNotContain(CloudDocumentNamespace.Roster.KeyPrefix, seatKv.Values.Keys);
    }

    [Fact]
    public async Task StoredJson_ContainsNoToken()
    {
        var (store, kv, _) = CreateStore();
        await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);

        Assert.All(kv.Values.Values, value =>
        {
            Assert.DoesNotContain("synthetic-token", value);
            Assert.DoesNotContain("synthetic-refresh", value);
        });
    }

    [Fact]
    public async Task Put_WithExplicitIdAfterIndexLoss_ThrowsConflictInsteadOfOverwriting()
    {
        var (store, kv, _) = CreateStore();
        var original = await store.PutAsync(null, "原座位表", "seatgrid.daily", SampleJson("原座位表"), 0, CancellationToken.None);
        kv.RemoveDirect(CloudDocumentNamespace.SeatGrid.IndexKey);

        await Assert.ThrowsAsync<CloudConflictException>(() => store.PutAsync(
            original.Id, "不应覆盖", "seatgrid.daily", SampleJson("不应覆盖"), 0, CancellationToken.None));

        var loaded = await store.GetAsync(original.Id, CancellationToken.None);
        Assert.Contains("原座位表", loaded!.Json);
    }

    [Fact]
    public async Task Put_UpdateRecoveredDocumentAfterIndexLoss_SucceedsAndRebuildsIndex()
    {
        var (store, kv, _) = CreateStore();
        var original = await store.PutAsync(null, "原座位表", "seatgrid.daily", SampleJson("原座位表"), 0, CancellationToken.None);
        kv.RemoveDirect(CloudDocumentNamespace.SeatGrid.IndexKey);

        var listed = Assert.Single(await store.ListAsync(CancellationToken.None));
        var updated = await store.PutAsync(
            listed.Id, "更新座位表", "seatgrid.daily", SampleJson("更新座位表"), listed.Revision, CancellationToken.None);

        Assert.Equal(2, updated.Revision);
        Assert.True(kv.Values.ContainsKey(CloudDocumentNamespace.SeatGrid.IndexKey));
        Assert.Equal("更新座位表", Assert.Single(await store.ListAsync(CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Delete_WhenDocDeleteFails_PreservesIndexEntry()
    {
        var (store, kv, _) = CreateStore();
        var created = await store.PutAsync(null, "合成座位表", "seatgrid.daily", SampleJson(), 0, CancellationToken.None);
        kv.FailDelete(CloudDocumentNamespace.SeatGrid.KeyPrefix + created.Id);

        await Assert.ThrowsAsync<CloudAuthException>(() => store.DeleteAsync(created.Id, CancellationToken.None));

        var listed = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(created.Id, listed.Id);
    }

    [Fact]
    public async Task LargeIndex_Over40KB_CompressesAndRoundTrips()
    {
        var (store, kv, _) = CreateStore();
        var many = Enumerable.Range(0, 199).Select(i => new
        {
            Id = i.ToString("x32"),
            Name = "超长合成班级名称用于把索引体积撑过四十千字节阈值以测试透明压缩" + new string('甲', 90) + i,
            Kind = "seatgrid.daily",
            UpdatedAtUtc = Start.ToString("O"),
            Bytes = 100,
            Revision = 1,
        }).ToArray();
        kv.Seed(CloudDocumentNamespace.SeatGrid.IndexKey,
            JsonSerializer.Serialize(new { V = 1, Items = many }));

        var created = await store.PutAsync(null, "第200份", "seatgrid.daily", SampleJson("第200份"), 0, CancellationToken.None);

        Assert.True(kv.Values[CloudDocumentNamespace.SeatGrid.IndexKey].Contains("gzip+base64", StringComparison.Ordinal));
        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Contains(listed, item => item.Id == created.Id);
    }

    [Fact]
    public async Task SharedRosterList_DoesNotReReadIndexPerRoster()
    {
        var (store, kv, time) = CreateStore(CloudDocumentNamespace.Roster);
        var source = new SectlSharedRosterSource(store, time);
        for (var i = 1; i <= 3; i++)
        {
            await source.SaveAsync(new SharedRosterDocument
            {
                Name = $"合成{i}班",
                Students =
                [
                    new SharedRosterStudent { Index = 1, Name = $"学生{i}", Gender = "男", Number = $"S0{i}" },
                ],
            }, RosterFieldSelection.Minimal, null, 0, CancellationToken.None);
        }

        var before = kv.Handler.Requests.Count;
        var list = await source.ListAsync(CancellationToken.None);
        var indexGets = kv.Handler.Requests.Skip(before)
            .Count(r => r.Method == "GET" && r.Path.EndsWith(CloudDocumentNamespace.Roster.IndexKey, StringComparison.Ordinal));

        Assert.Equal(3, list.Count);
        Assert.Equal(1, indexGets);
    }
}
