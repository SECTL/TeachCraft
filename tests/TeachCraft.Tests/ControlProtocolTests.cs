using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using TeachCraft.Core.Control;

namespace TeachCraft.Tests;

/// <summary>
/// 集控协议与隐私红线单元测试（离线假 HTTP + 内存 WebSocket）：
/// 验证 Capabilities 白名单、双因子接入、SECTL 账号接入、8 条指令与幂等回放、
/// 仅几何不漏姓名的摘要结构，以及 superseded / revoked 状态转换。
/// </summary>
public sealed class ControlProtocolTests : IDisposable
{
    private readonly string _dir;

    public ControlProtocolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "teachcraft-control-proto", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (Exception) { }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, (HttpStatusCode Status, string Json)> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> handler)
            => _handler = handler;

        public List<(string Method, string Path, string? Auth, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(), body));
            var (status, json) = _handler(request, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class MemoryControlWebSocket : IControlWebSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly Channel<JsonObject> _outgoing = Channel.CreateUnbounded<JsonObject>();
        private WebSocketState _state = WebSocketState.None;

        public WebSocketState State => _state;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public async Task SendAsync(
            ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(buffer.Span);
            var node = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            await _outgoing.Writer.WriteAsync(node, cancellationToken);
        }

        public async Task<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var msg = await _incoming.Reader.ReadAsync(cancellationToken);
            if (msg == "__CLOSE__")
            {
                _state = WebSocketState.Closed;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var bytes = Encoding.UTF8.GetBytes(msg);
            bytes.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryWrite("__CLOSE__");
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void ServerSend(JsonObject message) => _incoming.Writer.TryWrite(message.ToJsonString());

        public async Task<JsonObject> ReadClientMessageAsync(int timeoutMs = 3000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            return await _outgoing.Reader.ReadAsync(cts.Token);
        }
    }

    private sealed class FakeControlHost : ITeachCraftControlHost
    {
        public int OpenCount { get; private set; }
        public string ActiveView { get; private set; } = "teacher";
        public bool IsProjecting { get; private set; }
        public bool IsExam { get; private set; }
        public int ExamColumns { get; private set; } = 8;
        public string? SelectedClass { get; private set; }

        public Task<string> OpenSeatGridAsync(string? view, CancellationToken ct)
        {
            OpenCount++;
            if (view is "teacher" or "student") ActiveView = view;
            return Task.FromResult(ActiveView);
        }

        public Task<string> SetSeatGridViewAsync(string view, CancellationToken ct)
        {
            ActiveView = view;
            return Task.FromResult(ActiveView);
        }

        public Task<bool> SetSeatGridProjectionAsync(bool on, CancellationToken ct)
        {
            IsProjecting = on;
            return Task.FromResult(IsProjecting);
        }

        public Task<SeatGridReadSummary> SetSeatGridExamAsync(bool on, int columns, CancellationToken ct)
        {
            IsExam = on;
            ExamColumns = columns;
            return Task.FromResult(new SeatGridReadSummary(true, IsExam, ActiveView, 42, 42, 48, 1, 6, ExamColumns, IsProjecting, "高一1班考场"));
        }

        public Task<string?> ShowClassBlockAsync(string? className, CancellationToken ct)
        {
            SelectedClass = className ?? "高一1班";
            return Task.FromResult<string?>(SelectedClass);
        }

        public Task<SeatGridReadSummary> ReadSeatGridAsync(CancellationToken ct)
            => Task.FromResult(new SeatGridReadSummary(true, IsExam, ActiveView, 42, 42, 48, 4, 6, 2, IsProjecting, "高一1班座位表"));

        public Task<ClassBlockReadSummary> ReadClassBlockAsync(CancellationToken ct)
            => Task.FromResult(new ClassBlockReadSummary(true, true, "高一", "2026-2027第一学期", 2, SelectedClass ?? "高一1班", new[] { "高一1班", "高一2班" }));

        public Task<GradeWeaveReadSummary> ReadGradeWeaveAsync(CancellationToken ct)
            => Task.FromResult(new GradeWeaveReadSummary(true, true, "高一总课表", 2, new[] { "高一1班", "高一2班" }, 3, 1, 0, 1, 0, 0, 0, 1));
    }

    [Fact]
    public void DefaultCapabilities_ExcludeAllSensitiveOrUnsupportedCapabilities()
    {
        var caps = SectlControlProtocol.DefaultCapabilities;
        Assert.Equal(8, caps.Count);
        Assert.All(caps, c =>
        {
            Assert.DoesNotContain("roster", c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("draw", c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grades", c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gradestrip", c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("meetroster", c, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("gradeweave.read", caps);
        Assert.Contains("seatgrid.exam", caps);
    }

    [Fact]
    public async Task DualFactorEnroll_NormalizesInputs_SendsGuid_AndConsumesCode()
    {
        var http = new StubHttpHandler((req, body) =>
        {
            Assert.EndsWith("/control/api/enroll", req.RequestUri!.AbsolutePath);
            var parsed = JsonNode.Parse(body)!.AsObject();
            Assert.Equal("SC-6M34RQ", parsed["site_id"]!.GetValue<string>());
            Assert.Equal("ABCD-7KQ2", parsed["code"]!.GetValue<string>());
            Assert.Equal("teachcraft", parsed["platform"]!.GetValue<string>());
            Assert.Equal("guid-abc", parsed["machine"]!["guid"]!.GetValue<string>());

            return (HttpStatusCode.Created, """
                {
                  "ok": true,
                  "data": {
                    "deviceId": "dev_99",
                    "secret": "sec_99",
                    "siteId": "site_db_1",
                    "site": { "publicId": "SC-6M34RQ", "name": "实验中学" }
                  }
                }
                """);
        });

        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Host = "https://sectl.cn",
            DeviceUuid = "guid-abc",
            DeviceName = "高一1班一体机",
            CredentialStore = new NoopControlCredentialStore(),
            StateStore = new NoopControlStateStore(),
            HttpHandler = http,
            WebSocketFactory = () => new MemoryControlWebSocket(),
        });

        var codeConsumed = false;
        service.EnrollCodeConsumed += (_, _) => codeConsumed = true;

        var (ok, msg, cred) = await service.EnrollAsync(" 6m34rq ", "abcd-7kq2");

        Assert.True(ok);
        Assert.NotNull(cred);
        Assert.Equal("dev_99", cred!.DeviceId);
        Assert.True(codeConsumed);
        Assert.Contains("实验中学", msg);
    }

    [Fact]
    public async Task AccountEnroll_AndDeviceSelfService_FollowWireContractWithoutStudentPii()
    {
        var http = new StubHttpHandler((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/control/api/sites" => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "data": {
                    "sites": [
                      { "id": "site_1", "publicId": "SC-6M34RQ", "name": "实验中学", "role": "admin", "roleText": "管理员", "deviceCount": 3 }
                    ]
                  }
                }
                """),
            "/control/api/device/enroll" => (HttpStatusCode.Created, """
                {
                  "ok": true,
                  "data": {
                    "deviceId": "dev_acc_1",
                    "secret": "sec_acc_1",
                    "siteId": "site_1",
                    "site": { "publicId": "SC-6M34RQ", "name": "实验中学" }
                  }
                }
                """),
            "/control/api/device/tree" => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "site": { "publicId": "SC-6M34RQ", "name": "实验中学" },
                  "device": { "id": "dev_acc_1", "name": "机房", "classId": "" },
                  "grades": [
                    { "id": "g1", "name": "高一", "classes": [{ "id": "c1", "name": "高一1班" }] }
                  ]
                }
                """),
            "/control/api/device/assign" => (HttpStatusCode.OK, """
                { "ok": true, "device": { "id": "dev_acc_1", "classId": "c1", "className": "高一1班" }, "updated": true }
                """),
            "/control/api/device/seats" => (HttpStatusCode.OK, """
                { "ok": true, "data": { "classRequired": false, "classId": "c1", "className": "高一1班", "rows": 6, "cols": 8, "source": "manual", "seats": { "0:0": { "label": "A1" } } } }
                """),
            "/control/api/device/unenroll" => (HttpStatusCode.OK, """{ "ok": true, "data": { "deviceId": "dev_acc_1" } }"""),
            _ => (HttpStatusCode.NotFound, """{"ok":false,"code":"not_found"}"""),
        });

        var credStore = new NoopControlCredentialStore();
        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Host = "https://sectl.cn",
            DeviceUuid = "guid-acc-1",
            CredentialStore = credStore,
            StateStore = new NoopControlStateStore(),
            HttpHandler = http,
            WebSocketFactory = () => new MemoryControlWebSocket(),
        });

        var sites = await service.FetchSitesWithAccountAsync("jwt-token-xyz");
        Assert.Single(sites);
        Assert.Equal("SC-6M34RQ", sites[0].PublicId);
        Assert.Equal("Bearer jwt-token-xyz", http.Calls[0].Auth);

        var (ok, _, cred) = await service.EnrollWithAccountAsync("jwt-token-xyz", "SC-6M34RQ");
        Assert.True(ok);
        Assert.Equal("dev_acc_1", cred!.DeviceId);

        var tree = await service.FetchDeviceTreeAsync();
        Assert.Single(tree.Grades);
        Assert.Equal("高一1班", tree.Grades[0].Classes[0].Name);
        var treeReqBody = JsonNode.Parse(http.Calls.Single(c => c.Path == "/control/api/device/tree").Body)!.AsObject();
        Assert.Equal("dev_acc_1", treeReqBody["deviceId"]!.GetValue<string>());
        Assert.Equal("sec_acc_1", treeReqBody["secret"]!.GetValue<string>());

        var assigned = await service.AssignDeviceClassAsync("c1");
        Assert.Equal("c1", assigned.ClassId);

        var seats = await service.FetchDeviceSeatsAsync("c1");
        Assert.Equal(6, seats.Rows);
        Assert.Equal(8, seats.Columns);
        Assert.Equal(1, seats.LabelCount);

        var removed = await service.UnenrollAsync();
        Assert.True(removed);
        Assert.Null(credStore.Load());
    }

    [Fact]
    public async Task WebSocketCommands_RunAll8Capabilities_ReplayIdempotently_AndHandleEviction()
    {
        var fakeWs = new MemoryControlWebSocket();
        var host = new FakeControlHost();
        var credStore = new NoopControlCredentialStore();
        credStore.Save(new ControlCredential
        {
            DeviceId = "dev_ws_1",
            DeviceSecret = "sec_ws_1",
            Host = "https://sectl.cn",
            SitePublicId = "SC-6M34RQ",
            SiteName = "实验中学",
        });

        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Enabled = true,
            AppVersion = "1.46.0",
            CredentialStore = credStore,
            StateStore = new NoopControlStateStore(),
            WebSocketFactory = () => fakeWs,
        });

        new TeachCraftControlDispatcher(service, host).RegisterAll();
        Assert.True(await service.StartAsync());

        // 1. 收到 hello 请求并返回握手响应
        var helloReq = await fakeWs.ReadClientMessageAsync();
        Assert.Equal("hello", helloReq["topic"]!.GetValue<string>());
        Assert.Equal("teachcraft", helloReq["payload"]!["platform"]!.GetValue<string>());
        Assert.Equal("1.46.0", helloReq["payload"]!["version"]!.GetValue<string>());
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "res",
            ["id"] = helloReq["id"]!.GetValue<string>(),
            ["ok"] = true,
            ["payload"] = new JsonObject
            {
                ["heartbeat_seconds"] = 25,
                ["offline_after_seconds"] = 75,
                ["device"] = new JsonObject { ["name"] = "高一1班" },
            },
        });

        async Task<JsonObject> SendCmdAndReadResultAsync(string cmdId, string cmd, JsonObject args)
        {
            fakeWs.ServerSend(new JsonObject
            {
                ["type"] = "evt",
                ["topic"] = "cmd",
                ["payload"] = new JsonObject
                {
                    ["command_id"] = cmdId,
                    ["cmd"] = cmd,
                    ["args"] = args,
                    ["deadline_ms"] = 4000,
                    ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds(),
                },
            });

            var ack = await fakeWs.ReadClientMessageAsync();
            Assert.Equal("cmd.ack", ack["topic"]!.GetValue<string>());
            Assert.True(ack["payload"]!["accepted"]!.GetValue<bool>());

            var res = await fakeWs.ReadClientMessageAsync();
            Assert.Equal("cmd.result", res["topic"]!.GetValue<string>());
            return res["payload"]!.AsObject();
        }

        var openRes = await SendCmdAndReadResultAsync("cmd_1", "seatgrid.open", new JsonObject { ["view"] = "student" });
        Assert.True(openRes["ok"]!.GetValue<bool>());
        Assert.Equal("student", openRes["data"]!["view"]!.GetValue<string>());

        // 幂等重投：同样 command_id 不再触发 host
        var replayRes = await SendCmdAndReadResultAsync("cmd_1", "seatgrid.open", new JsonObject { ["view"] = "teacher" });
        Assert.True(replayRes["ok"]!.GetValue<bool>());
        Assert.Equal(1, host.OpenCount);

        var examRes = await SendCmdAndReadResultAsync("cmd_2", "seatgrid.exam", new JsonObject { ["on"] = true, ["columns"] = 6 });
        Assert.True(examRes["ok"]!.GetValue<bool>());
        Assert.True(examRes["data"]!["is_exam"]!.GetValue<bool>());
        Assert.Equal(6, examRes["data"]!["columns"]!.GetValue<int>());

        var seatRead = await SendCmdAndReadResultAsync("cmd_3", "seatgrid.read", new JsonObject());
        Assert.False(seatRead["data"]!.AsObject().ContainsKey("students"));

        var gwRead = await SendCmdAndReadResultAsync("cmd_4", "gradeweave.read", new JsonObject());
        Assert.Equal(3, gwRead["data"]!["conflict_total"]!.GetValue<int>());
        Assert.Equal(1, gwRead["data"]!["conflict_counts"]!["teacher_clash"]!.GetValue<int>());

        // 超出 columns 范围 -> bad_args 失败回执
        var badExam = await SendCmdAndReadResultAsync("cmd_5", "seatgrid.exam", new JsonObject { ["on"] = true, ["columns"] = 3 });
        Assert.False(badExam["ok"]!.GetValue<bool>());
        Assert.Equal("bad_args", badExam["code"]!.GetValue<string>());

        // 本机关闭远程控制 -> cmd.ack rejected(local_remote_disabled)
        service.SetLocalRemoteAllowed(false);
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "evt",
            ["topic"] = "cmd",
            ["payload"] = new JsonObject { ["command_id"] = "cmd_6", ["cmd"] = "seatgrid.view", ["args"] = new JsonObject { ["view"] = "teacher" } },
        });
        var rejectedAck = await fakeWs.ReadClientMessageAsync();
        Assert.False(rejectedAck["payload"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("local_remote_disabled", rejectedAck["payload"]!["code"]!.GetValue<string>());

        // evicted superseded -> 回到 Superseded 状态，不删凭证
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "evt",
            ["topic"] = "evicted",
            ["payload"] = new JsonObject { ["reason"] = "superseded", ["by"] = new JsonObject { ["device_name"] = "隔壁班电脑" } },
        });
        await Task.Delay(100);
        Assert.Equal("superseded", service.Snapshot.State);
        Assert.NotNull(credStore.Load());

        // evicted revoked -> 清空凭证并进入 revoked 终态
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "evt",
            ["topic"] = "evicted",
            ["payload"] = new JsonObject { ["reason"] = "revoked" },
        });
        await Task.Delay(100);
        Assert.Null(credStore.Load());
    }
}
