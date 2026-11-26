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

    [Fact]
    public async Task CommandErrorReplies_RedactUnexpectedIOException_AndPreserveControlExceptionDetail()
    {
        var fakeWs = new MemoryControlWebSocket();
        var host = new FakeControlHost();
        var credStore = new NoopControlCredentialStore();
        credStore.Save(new ControlCredential
        {
            DeviceId = "dev_ws_err",
            DeviceSecret = "sec_ws_err",
            Host = "https://sectl.cn",
            SitePublicId = "SC-6M34RQ",
            SiteName = "实验中学",
        });

        var logs = new List<string>();
        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Enabled = true,
            CredentialStore = credStore,
            StateStore = new NoopControlStateStore(),
            WebSocketFactory = () => fakeWs,
            Log = logs.Add,
        });

        new TeachCraftControlDispatcher(service, host).RegisterAll();

        // 同步路径抛 IOException（SectlControlClient.HandleCommandAsync）
        service.RegisterHandler("seatgrid.open", _ => throw new IOException(@"C:\Users\alice\secret.xlsx 被占用"));
        // 同步路径抛 ControlException（保留中文与 code）
        service.RegisterHandler("classblock.read", _ => throw new ControlException("bad_args", "考场列数须在 4 至 8 之间"));

        Assert.True(await service.StartAsync());

        var helloReq = await fakeWs.ReadClientMessageAsync();
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "res",
            ["id"] = helloReq["id"]!.GetValue<string>(),
            ["ok"] = true,
            ["payload"] = new JsonObject { ["heartbeat_seconds"] = 25, ["offline_after_seconds"] = 75 },
        });

        async Task<JsonObject> SendAsync(string cmdId, string cmd)
        {
            fakeWs.ServerSend(new JsonObject
            {
                ["type"] = "evt",
                ["topic"] = "cmd",
                ["payload"] = new JsonObject
                {
                    ["command_id"] = cmdId,
                    ["cmd"] = cmd,
                    ["args"] = new JsonObject(),
                    ["deadline_ms"] = 4000,
                    ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds(),
                },
            });

            var ack = await fakeWs.ReadClientMessageAsync();
            Assert.Equal("cmd.ack", ack["topic"]!.GetValue<string>());
            var res = await fakeWs.ReadClientMessageAsync();
            Assert.Equal("cmd.result", res["topic"]!.GetValue<string>());
            return res["payload"]!.AsObject();
        }

        var syncErr = await SendAsync("cmd_sync_io", "seatgrid.open");
        var syncJson = syncErr.ToJsonString();
        Assert.False(syncErr["ok"]!.GetValue<bool>());
        Assert.Equal("failed", syncErr["code"]!.GetValue<string>());
        Assert.Equal("客户端执行失败", syncErr["message"]!.GetValue<string>());
        Assert.DoesNotContain("alice", syncJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.xlsx", syncJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", syncJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, l => l.Contains("secret.xlsx", StringComparison.Ordinal));

        var syncControlErr = await SendAsync("cmd_sync_ctrl", "classblock.read");
        Assert.False(syncControlErr["ok"]!.GetValue<bool>());
        Assert.Equal("bad_args", syncControlErr["code"]!.GetValue<string>());
        Assert.Equal("考场列数须在 4 至 8 之间", syncControlErr["message"]!.GetValue<string>());
    }

    private sealed class ThrowingDeferredHost : ITeachCraftControlHost
    {
        private readonly Func<Exception> _factory;
        public ThrowingDeferredHost(Func<Exception> factory) => _factory = factory;

        public Task<string> OpenSeatGridAsync(string? view, CancellationToken ct) => Task.FromException<string>(_factory());
        public Task<string> SetSeatGridViewAsync(string view, CancellationToken ct) => Task.FromException<string>(_factory());
        public Task<bool> SetSeatGridProjectionAsync(bool on, CancellationToken ct) => Task.FromException<bool>(_factory());
        public Task<SeatGridReadSummary> SetSeatGridExamAsync(bool on, int columns, CancellationToken ct) => Task.FromException<SeatGridReadSummary>(_factory());
        public Task<string?> ShowClassBlockAsync(string? className, CancellationToken ct) => Task.FromException<string?>(_factory());
        public Task<SeatGridReadSummary> ReadSeatGridAsync(CancellationToken ct) => Task.FromException<SeatGridReadSummary>(_factory());
        public Task<ClassBlockReadSummary> ReadClassBlockAsync(CancellationToken ct) => Task.FromException<ClassBlockReadSummary>(_factory());
        public Task<GradeWeaveReadSummary> ReadGradeWeaveAsync(CancellationToken ct) => Task.FromException<GradeWeaveReadSummary>(_factory());
    }

    [Fact]
    public async Task DeferredDispatcherErrorReplies_RedactUnexpectedIOException_AndPreserveControlExceptionDetail()
    {
        var fakeWs = new MemoryControlWebSocket();
        var credStore = new NoopControlCredentialStore();
        credStore.Save(new ControlCredential
        {
            DeviceId = "dev_ws_def",
            DeviceSecret = "sec_ws_def",
            Host = "https://sectl.cn",
            SitePublicId = "SC-6M34RQ",
            SiteName = "实验中学",
        });

        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Enabled = true,
            CredentialStore = credStore,
            StateStore = new NoopControlStateStore(),
            WebSocketFactory = () => fakeWs,
        });

        Exception nextEx = new IOException(@"C:\Users\alice\secret.xlsx 被占用");
        new TeachCraftControlDispatcher(service, new ThrowingDeferredHost(() => nextEx)).RegisterAll();
        Assert.True(await service.StartAsync());

        var helloReq = await fakeWs.ReadClientMessageAsync();
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "res",
            ["id"] = helloReq["id"]!.GetValue<string>(),
            ["ok"] = true,
            ["payload"] = new JsonObject { ["heartbeat_seconds"] = 25, ["offline_after_seconds"] = 75 },
        });

        async Task<JsonObject> SendAsync(string cmdId, string cmd)
        {
            fakeWs.ServerSend(new JsonObject
            {
                ["type"] = "evt",
                ["topic"] = "cmd",
                ["payload"] = new JsonObject
                {
                    ["command_id"] = cmdId,
                    ["cmd"] = cmd,
                    ["args"] = new JsonObject(),
                    ["deadline_ms"] = 4000,
                    ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds(),
                },
            });

            _ = await fakeWs.ReadClientMessageAsync();
            var res = await fakeWs.ReadClientMessageAsync();
            return res["payload"]!.AsObject();
        }

        var ioPayload = await SendAsync("cmd_def_io", "seatgrid.read");
        var ioJson = ioPayload.ToJsonString();
        Assert.False(ioPayload["ok"]!.GetValue<bool>());
        Assert.Equal("failed", ioPayload["code"]!.GetValue<string>());
        Assert.Equal("客户端执行失败", ioPayload["message"]!.GetValue<string>());
        Assert.DoesNotContain("alice", ioJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.xlsx", ioJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", ioJson, StringComparison.OrdinalIgnoreCase);

        nextEx = new ControlException("bad_args", "考场列数须在 4 至 8 之间");
        var ctrlPayload = await SendAsync("cmd_def_ctrl", "classblock.read");
        Assert.False(ctrlPayload["ok"]!.GetValue<bool>());
        Assert.Equal("bad_args", ctrlPayload["code"]!.GetValue<string>());
        Assert.Equal("考场列数须在 4 至 8 之间", ctrlPayload["message"]!.GetValue<string>());
    }

    [Fact]
    public void CommandCache_UsesLruEvictionAt512_AndPersistsOrderAcrossReload()
    {
        var statePath = Path.Combine(_dir, "lru-state.json");
        var fileStore = new FileControlStateStore(statePath);
        var noopStore = new NoopControlStateStore();

        foreach (IControlStateStore store in new IControlStateStore[] { noopStore, fileStore })
        {
            // 1. 插入 513 个不同 command_id -> 第 1 个被驱逐，第 2..513 个命中
            for (var i = 1; i <= SectlControlProtocol.CommandCacheSize + 1; i++)
                store.RememberCommand($"cmd_{i:000}", true, new JsonObject { ["seq"] = i });

            Assert.False(store.TryGetCommand("cmd_001", out _, out _, out _));
            Assert.True(store.TryGetCommand("cmd_002", out _, out _, out _));
            Assert.True(store.TryGetCommand("cmd_513", out _, out _, out _));

            // 2. 重置并塞满 512 条，先命中第 1 条提升到队尾，再插第 513 条 -> 被驱逐的是第 2 条，不是第 1 条
            store.Reset();
            for (var i = 1; i <= SectlControlProtocol.CommandCacheSize; i++)
                store.RememberCommand($"lru_{i:000}", true, new JsonObject { ["seq"] = i });

            Assert.True(store.TryGetCommand("lru_001", out _, out var hitPayload, out _));
            Assert.Equal(1, hitPayload["seq"]!.GetValue<int>());

            store.RememberCommand("lru_513", true, new JsonObject { ["seq"] = 513 });

            Assert.True(store.TryGetCommand("lru_001", out _, out _, out _));
            Assert.False(store.TryGetCommand("lru_002", out _, out _, out _));
            Assert.True(store.TryGetCommand("lru_513", out _, out _, out _));
        }

        // 3. state.json 重载后 LRU 顺序与 512 条仍可回放
        var reloaded = new FileControlStateStore(statePath);
        Assert.True(reloaded.TryGetCommand("lru_001", out _, out _, out _));
        Assert.False(reloaded.TryGetCommand("lru_002", out _, out _, out _));
        Assert.True(reloaded.TryGetCommand("lru_513", out _, out _, out _));
        for (var i = 3; i <= SectlControlProtocol.CommandCacheSize; i++)
            Assert.True(reloaded.TryGetCommand($"lru_{i:000}", out _, out _, out _));
    }

    [Fact]
    public async Task HttpWireErrorCodes_MapToSpecificChineseMessages()
    {
        Assert.Contains("30 秒后再试", SectlEnroll.DescribeError("too_many_requests", new JsonObject { ["retryAfter"] = 30 }), StringComparison.Ordinal);
        Assert.Contains("稍后再试", SectlEnroll.DescribeError("too_many_requests", null), StringComparison.Ordinal);
        Assert.Contains("登录会话已失效", SectlEnroll.DescribeError("not_authed", null), StringComparison.Ordinal);
        Assert.Contains("权限", SectlEnroll.DescribeError("forbidden", null), StringComparison.Ordinal);
        Assert.Contains("权限", SectlEnroll.DescribeError("permission_denied", null), StringComparison.Ordinal);

        var nextStatus = HttpStatusCode.TooManyRequests;
        var nextBody = """{"ok":false,"code":"too_many_requests","detail":{"retryAfter":12}}""";
        var http = new StubHttpHandler((_, _) => (nextStatus, nextBody));
        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Enabled = false,
            CredentialStore = new NoopControlCredentialStore(),
            StateStore = new NoopControlStateStore(),
            HttpHandler = http,
        });

        var r429 = await service.EnrollAsync("SC-6M34RQ", "ABCD-7KQ2");
        Assert.False(r429.Ok);
        Assert.Contains("12 秒后再试", r429.Message, StringComparison.Ordinal);

        nextStatus = HttpStatusCode.Unauthorized;
        nextBody = """{"ok":false,"code":"not_authed"}""";
        var r401 = await service.EnrollWithAccountAsync("tok_bad", "site_1");
        Assert.False(r401.Ok);
        Assert.Contains("登录会话已失效", r401.Message, StringComparison.Ordinal);

        nextStatus = HttpStatusCode.Forbidden;
        nextBody = """{"ok":false,"code":"forbidden"}""";
        var r403 = await service.EnrollWithAccountAsync("tok_no_perm", "site_1");
        Assert.False(r403.Ok);
        Assert.Contains("权限", r403.Message, StringComparison.Ordinal);
    }

    private sealed class SensitiveAndHangingHost : ITeachCraftControlHost
    {
        public string SeatTitle { get; set; } = string.Empty;
        public string GradeWeaveTitle { get; set; } = string.Empty;
        public bool IsExam { get; set; }
        public bool HangOnSeatGridRead { get; set; }

        public Task<string> OpenSeatGridAsync(string? view, CancellationToken ct) => Task.FromResult("teacher");
        public Task<string> SetSeatGridViewAsync(string view, CancellationToken ct) => Task.FromResult(view);
        public Task<bool> SetSeatGridProjectionAsync(bool on, CancellationToken ct) => Task.FromResult(on);
        public Task<SeatGridReadSummary> SetSeatGridExamAsync(bool on, int columns, CancellationToken ct)
            => Task.FromResult(new SeatGridReadSummary(true, on, "teacher", 40, 40, 40, 4, 5, columns, false, SeatTitle));
        public Task<string?> ShowClassBlockAsync(string? className, CancellationToken ct) => Task.FromResult<string?>("七年级1班");
        public Task<SeatGridReadSummary> ReadSeatGridAsync(CancellationToken ct)
        {
            if (HangOnSeatGridRead)
                return new TaskCompletionSource<SeatGridReadSummary>().Task;
            return Task.FromResult(new SeatGridReadSummary(true, IsExam, "teacher", 40, 40, 40, 4, 5, 2, false, SeatTitle));
        }
        public Task<ClassBlockReadSummary> ReadClassBlockAsync(CancellationToken ct)
            => Task.FromResult(new ClassBlockReadSummary(true, true, "七年级", "2026年春季学期", 2, "七年级1班", new[] { "七年级1班", "七年级2班" }));
        public Task<GradeWeaveReadSummary> ReadGradeWeaveAsync(CancellationToken ct)
            => Task.FromResult(new GradeWeaveReadSummary(true, true, GradeWeaveTitle, 2, new[] { "七年级1班", "七年级2班" }, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task ReadSummaries_SanitizeFreeTextTitles_ExcludePrivacySentinels_AndWatchdogTimesOutDeferredHost()
    {
        var fakeWs = new MemoryControlWebSocket();
        var credStore = new NoopControlCredentialStore();
        credStore.Save(new ControlCredential
        {
            DeviceId = "dev_sec",
            DeviceSecret = "sec_tok",
            Platform = "teachcraft",
            Host = "https://example.invalid",
            SiteId = "s_1",
            SitePublicId = "SC-6M34RQ",
            SiteName = "示例中学",
        });

        await using var service = new ControlCenterService(new ControlCenterOptions
        {
            Enabled = true,
            CredentialStore = credStore,
            StateStore = new NoopControlStateStore(),
            WebSocketFactory = () => fakeWs,
        });

        var host = new SensitiveAndHangingHost
        {
            SeatTitle = "示例中学 陈屿 20260001 分数599 内部座位表",
            GradeWeaveTitle = "示例中学 沈青禾 绝密全年级排课",
            IsExam = false,
        };
        new TeachCraftControlDispatcher(service, host).RegisterAll();
        Assert.True(await service.StartAsync());

        var helloReq = await fakeWs.ReadClientMessageAsync();
        fakeWs.ServerSend(new JsonObject
        {
            ["type"] = "res",
            ["id"] = helloReq["id"]!.GetValue<string>(),
            ["ok"] = true,
            ["payload"] = new JsonObject { ["heartbeat_seconds"] = 25, ["offline_after_seconds"] = 75 },
        });

        async Task<JsonObject> SendCmdAsync(string id, string cmd, int deadlineMs = 4000)
        {
            fakeWs.ServerSend(new JsonObject
            {
                ["type"] = "evt",
                ["topic"] = "cmd",
                ["payload"] = new JsonObject
                {
                    ["command_id"] = id,
                    ["cmd"] = cmd,
                    ["args"] = new JsonObject(),
                    ["deadline_ms"] = deadlineMs,
                    ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds(),
                },
            });
            _ = await fakeWs.ReadClientMessageAsync(); // cmd.ack
            return (await fakeWs.ReadClientMessageAsync())["payload"]!.AsObject();
        }

        // 1. 自由文本 title / project_title 必须脱敏为固定文案，且全 JSON 键扫描不含隐私哨兵或隐私键
        var seatJson = await SendCmdAsync("sec_seat", "seatgrid.read");
        var cbJson = await SendCmdAsync("sec_cb", "classblock.read");
        var gwJson = await SendCmdAsync("sec_gw", "gradeweave.read");

        Assert.Equal("班级座位表", seatJson["data"]!["title"]!.GetValue<string>());
        Assert.Equal("织序课表编排", gwJson["data"]!["project_title"]!.GetValue<string>());

        var forbiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "students", "student", "student_names", "student_no", "scores", "score", "teachers", "teacher_name", "phone" };
        void AssertCleanJson(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    Assert.DoesNotContain(kv.Key, forbiddenKeys);
                    AssertCleanJson(kv.Value);
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr) AssertCleanJson(item);
            }
        }

        foreach (var payload in new[] { seatJson, cbJson, gwJson })
        {
            AssertCleanJson(payload);
            var raw = payload.ToJsonString();
            foreach (var sentinel in new[] { "陈屿", "20260001", "599", "沈青禾", "示例中学" })
                Assert.DoesNotContain(sentinel, raw, StringComparison.Ordinal);
        }

        // 2. 合规纯班级名与考场标题允许保留，纯年级总表标题允许保留；考场自由文本回退「考场座位表」
        host.SeatTitle = "七年级1班座位表";
        host.GradeWeaveTitle = "高一总课表";
        var safeSeat = await SendCmdAsync("sec_safe_seat", "seatgrid.read");
        var safeGw = await SendCmdAsync("sec_safe_gw", "gradeweave.read");
        Assert.Equal("七年级1班座位表", safeSeat["data"]!["title"]!.GetValue<string>());
        Assert.Equal("高一总课表", safeGw["data"]!["project_title"]!.GetValue<string>());

        host.IsExam = true;
        host.SeatTitle = "任意自由备注 张三考场";
        var examRedacted = await SendCmdAsync("sec_exam_free", "seatgrid.read");
        Assert.Equal("考场座位表", examRedacted["data"]!["title"]!.GetValue<string>());

        // 3. Deferred host 挂起超过 deadline_ms 时看门狗按 timeout 回执
        host.HangOnSeatGridRead = true;
        var timedOut = await SendCmdAsync("sec_timeout", "seatgrid.read", deadlineMs: 450);
        Assert.False(timedOut["ok"]!.GetValue<bool>());
        Assert.Equal("timeout", timedOut["code"]!.GetValue<string>());
    }
}
