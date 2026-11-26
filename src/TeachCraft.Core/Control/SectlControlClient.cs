using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Updating;

namespace TeachCraft.Core.Control;

/// <summary>WebSocket 传输抽象，方便测试用内存连接驱动完整握手与指令回路。</summary>
public interface IControlWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
    Task<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
    void Abort();
}

internal sealed class ManagedControlWebSocket : IControlWebSocket
{
    private readonly ClientWebSocket _ws = new();

    public WebSocketState State => _ws.State;
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _ws.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => _ws.SendAsync(buffer, messageType, endOfMessage, cancellationToken).AsTask();

    public Task<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _ws.ReceiveAsync(buffer, cancellationToken).AsTask();

    public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => _ws.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public void Abort() => _ws.Abort();

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SectlControlClientOptions
{
    public string Host { get; init; } = SectlPlatformDefaults.WebBaseUrl;
    public string Platform { get; init; } = SectlControlProtocol.PlatformName;
    public string? DeviceName { get; init; }
    public string DeviceUuid { get; init; } = string.Empty;
    public string AppVersion { get; init; } = "1.46.0";
    public IReadOnlyList<string> Capabilities { get; init; } = SectlControlProtocol.DefaultCapabilities;
    public IReadOnlyList<string>? AppSupported { get; init; }
    public IControlCredentialStore? CredentialStore { get; init; }
    public ControlCredential? Credential { get; init; }
    public IControlStateStore? StateStore { get; init; }
    public HttpMessageHandler? HttpHandler { get; init; }
    public Func<IControlWebSocket>? WebSocketFactory { get; init; }
    public bool AutoReselect { get; init; } = false;
    public bool LocalRemoteAllowed { get; init; } = true;
    public double RequestTimeoutSeconds { get; init; } = 8;

    public Action<ControlState, JsonObject>? OnState { get; init; }
    public Func<CommandInvocation, object?>? OnCommand { get; init; }
    public Action<string, string>? OnCommandStart { get; init; }
    public Action<string, string, bool, double>? OnCommandDone { get; init; }
    public Action<JsonObject>? OnEvicted { get; init; }
    public Action<JsonObject>? OnTree { get; init; }
    public Action<JsonObject>? OnDesiredState { get; init; }
    public Action<string>? OnLog { get; init; }
}

/// <summary>
/// TeachCraft 用 SECTL 集控中心客户端：零第三方依赖（基于 BCL ClientWebSocket），
/// 支持双因子接入、SECTL 账号接入、设备选班与座位布局摘要读取、v4 两帧指令回执。
/// </summary>
public sealed class SectlControlClient : IAsyncDisposable
{
    private readonly SectlControlClientOptions _opt;
    private readonly HttpClient _http;
    private readonly Func<IControlWebSocket> _wsFactory;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCmds = new();
    private readonly HashSet<string> _appSupported;

    private ControlCredential? _credential;
    private IControlWebSocket? _ws;
    private Task _supervisor = Task.CompletedTask;
    private int _supervisorStarted;
    private int _disposed;
    private long _seq;
    private int _attempt;
    private string? _wantClass;
    private volatile bool _localRemoteAllowed;
    private string _stopReason = string.Empty;
    private int _heartbeatSeconds = SectlControlProtocol.DefaultHeartbeatSeconds;
    private int _offlineAfterSeconds = SectlControlProtocol.DefaultOfflineAfterSeconds;

    public SectlControlClient(SectlControlClientOptions? options = null)
    {
        _opt = options ?? new SectlControlClientOptions();
        Store = _opt.CredentialStore ?? new NoopControlCredentialStore();
        StateStore = _opt.StateStore ?? new NoopControlStateStore();
        Host = ControlSettings.NormalizeHost(_opt.Host);
        Platform = string.IsNullOrWhiteSpace(_opt.Platform) ? SectlControlProtocol.PlatformName : _opt.Platform.Trim();
        Capabilities = _opt.Capabilities.ToArray();
        _appSupported = new HashSet<string>(_opt.AppSupported ?? _opt.Capabilities, StringComparer.Ordinal);
        _localRemoteAllowed = _opt.LocalRemoteAllowed;
        _http = new HttpClient(_opt.HttpHandler ?? SharedHandlers.Default, disposeHandler: false);
        _wsFactory = _opt.WebSocketFactory ?? (() => new ManagedControlWebSocket());

        if (_opt.Credential is { } direct)
        {
            _credential = direct;
        }
        else
        {
            _credential = Store.Load();
            if (_credential is not null && !string.IsNullOrWhiteSpace(_credential.Host))
                Host = ControlSettings.NormalizeHost(_credential.Host);
        }

        if (_credential is not null)
        {
            State = ControlState.Configured;
            StateDetail = new JsonObject
            {
                ["message"] = "已有本机凭证，等待连接",
                ["site"] = _credential.SiteLabel,
            };
        }
    }

    public IControlCredentialStore Store { get; }
    public IControlStateStore StateStore { get; }
    public string Host { get; private set; }
    public string Platform { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public string DeviceName => !string.IsNullOrWhiteSpace(_opt.DeviceName)
        ? _opt.DeviceName!
        : $"{Environment.MachineName}-TeachCraft";
    public ControlState State { get; private set; } = ControlState.Unconfigured;
    public JsonObject StateDetail { get; private set; } = new() { ["message"] = "尚未接入地点" };
    public ControlCredential? Credential => _credential;
    public string? SelectedClass { get; private set; }
    public JsonObject? Hello { get; private set; }
    public JsonObject? Tree { get; private set; }
    public JsonObject? DesiredState { get; private set; }
    public long AppliedStateRevision => StateStore.AppliedRevision;
    public int CommandTimeoutMs { get; private set; } = SectlControlProtocol.DefaultCommandTimeoutMs;
    public int HeartbeatSeconds => _heartbeatSeconds;
    public int OfflineAfterSeconds => _offlineAfterSeconds;
    public bool LocalRemoteAllowed => _localRemoteAllowed;
    public string SiteLabel => _credential?.SiteLabel ?? string.Empty;
    public bool Connected => State is ControlState.Connected or ControlState.Selected or ControlState.Superseded;

    public string WsUrl
    {
        get
        {
            var baseUrl = Host;
            if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "wss://" + baseUrl[8..];
            else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "ws://" + baseUrl[7..];
            return baseUrl.TrimEnd('/') + SectlControlProtocol.WsPath;
        }
    }

    public void SetLocalRemoteAllowed(bool allowed)
    {
        _localRemoteAllowed = allowed;
        Log(allowed ? "已允许接收远程指令" : "已暂停接收远程指令");
    }

    public void UpdateDesiredHost(string host)
    {
        Host = ControlSettings.NormalizeHost(host);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _supervisorStarted, 1) != 0) return;
        _supervisor = Task.Run(() => SupervisorLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        var ws = _ws;
        if (ws is not null)
        {
            try { ws.Abort(); } catch (ObjectDisposedException) { }
        }

        try
        {
            await _supervisor.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    // --------------------------------------------------------- HTTP 接入与自助

    public async Task<ControlCredential> EnrollAsync(
        string siteId,
        string code,
        string? deviceName = null,
        double timeoutSeconds = 15)
    {
        var site = SectlEnroll.NormalizeSiteId(siteId);
        var pairCode = SectlEnroll.NormalizePairCode(code);
        if (site.Length == 0) throw new ControlException("bad_request", "请填写地点ID（形如 SC-6M34RQ）");
        if (pairCode.Length == 0) throw new ControlException("bad_request", "请填写配对码（形如 ABCD-7KQ2）");

        var name = !string.IsNullOrWhiteSpace(deviceName) ? deviceName!.Trim() : DeviceName;
        var body = new JsonObject
        {
            ["site_id"] = SectlEnroll.FormatSiteId(site),
            ["code"] = SectlEnroll.FormatCode(pairCode),
            ["platform"] = Platform,
            ["device_name"] = name,
            ["machine"] = MachineJson(),
        };

        var outObj = await PostJsonAsync($"{Host}/control/api/enroll", body, bearerToken: null, timeoutSeconds).ConfigureAwait(false);
        return ApplyEnrollResponse(outObj, name);
    }

    public async Task<ControlCredential> EnrollWithAccountAsync(
        string accessToken,
        string siteId,
        string? deviceName = null,
        string? classId = null,
        double timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ControlException("not_authed", "请先在「账号与云同步」登录 SECTL 账号");
        if (string.IsNullOrWhiteSpace(siteId))
            throw new ControlException("bad_request", "请选择要接入的地点");

        var name = !string.IsNullOrWhiteSpace(deviceName) ? deviceName!.Trim() : DeviceName;
        var body = new JsonObject
        {
            ["siteId"] = siteId.Trim(),
            ["platform"] = Platform,
            ["deviceName"] = name,
            ["machine"] = MachineJson(),
        };
        if (!string.IsNullOrWhiteSpace(classId))
            body["classId"] = classId.Trim();

        var outObj = await PostJsonAsync($"{Host}/control/api/device/enroll", body, accessToken, timeoutSeconds).ConfigureAwait(false);
        return ApplyEnrollResponse(outObj, name);
    }

    public async Task<IReadOnlyList<ControlSiteSummary>> FetchSitesWithAccountAsync(
        string accessToken,
        double timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ControlException("not_authed", "请先登录 SECTL 账号");

        var outObj = await GetJsonAsync($"{Host}/control/api/sites", accessToken, timeoutSeconds).ConfigureAwait(false);
        if (!JsonHelp.Bool(outObj, "ok"))
        {
            var code = JsonHelp.Str(outObj, "code", "sites_failed");
            throw new ControlException(code, SectlEnroll.DescribeError(code, outObj["detail"] as JsonObject), outObj);
        }

        var data = UnwrapData(outObj);
        var list = new List<ControlSiteSummary>();
        foreach (var node in JsonHelp.AsArray(data["sites"]).OfType<JsonObject>())
        {
            list.Add(new ControlSiteSummary(
                Id: JsonHelp.Str(node, "id"),
                PublicId: JsonHelp.Str(node, "publicId"),
                Name: JsonHelp.Str(node, "name"),
                Role: JsonHelp.Str(node, "role"),
                RoleText: JsonHelp.Str(node, "roleText"),
                DeviceCount: JsonHelp.Int(node, "deviceCount")));
        }

        return list;
    }

    public async Task<ControlTreeSnapshot> FetchDeviceTreeAsync(double timeoutSeconds = 15)
    {
        var cred = RequireCredential();
        var body = DeviceAuthBody(cred);
        var outObj = await PostJsonAsync($"{Host}/control/api/device/tree", body, bearerToken: null, timeoutSeconds).ConfigureAwait(false);
        if (!JsonHelp.Bool(outObj, "ok"))
        {
            var code = JsonHelp.Str(outObj, "code", "tree_failed");
            throw new ControlException(code, SectlEnroll.DescribeError(code, outObj["detail"] as JsonObject), outObj);
        }

        var data = UnwrapData(outObj);
        var siteObj = JsonHelp.AsObject(data["site"]);
        var devObj = JsonHelp.AsObject(data["device"]);
        var grades = new List<ControlTreeGrade>();
        foreach (var g in JsonHelp.AsArray(data["grades"]).OfType<JsonObject>())
        {
            var classes = JsonHelp.AsArray(g["classes"])
                .OfType<JsonObject>()
                .Select(c => new ControlTreeClass(JsonHelp.Str(c, "id"), JsonHelp.Str(c, "name")))
                .ToList();
            grades.Add(new ControlTreeGrade(JsonHelp.Str(g, "id"), JsonHelp.Str(g, "name"), classes));
        }

        return new ControlTreeSnapshot(
            SitePublicId: JsonHelp.Str(siteObj, "publicId"),
            SiteName: JsonHelp.Str(siteObj, "name"),
            DeviceId: JsonHelp.Str(devObj, "id"),
            DeviceName: JsonHelp.Str(devObj, "name"),
            AssignedClassId: JsonHelp.Str(devObj, "classId"),
            Grades: grades);
    }

    public async Task<(string ClassId, string ClassName)> AssignDeviceClassAsync(string? classId, double timeoutSeconds = 15)
    {
        var cred = RequireCredential();
        var body = DeviceAuthBody(cred);
        body["classId"] = string.IsNullOrWhiteSpace(classId) ? string.Empty : classId.Trim();
        var outObj = await PostJsonAsync($"{Host}/control/api/device/assign", body, bearerToken: null, timeoutSeconds).ConfigureAwait(false);
        if (!JsonHelp.Bool(outObj, "ok"))
        {
            var code = JsonHelp.Str(outObj, "code", "assign_failed");
            throw new ControlException(code, SectlEnroll.DescribeError(code, outObj["detail"] as JsonObject), outObj);
        }

        var dev = JsonHelp.AsObject(UnwrapData(outObj)["device"]);
        return (JsonHelp.Str(dev, "classId"), JsonHelp.Str(dev, "className"));
    }

    public async Task<ControlDeviceSeatsSnapshot> FetchDeviceSeatsAsync(string? classId = null, double timeoutSeconds = 15)
    {
        var cred = RequireCredential();
        var body = DeviceAuthBody(cred);
        if (!string.IsNullOrWhiteSpace(classId))
            body["classId"] = classId.Trim();

        var outObj = await PostJsonAsync($"{Host}/control/api/device/seats", body, bearerToken: null, timeoutSeconds).ConfigureAwait(false);
        if (!JsonHelp.Bool(outObj, "ok"))
        {
            var code = JsonHelp.Str(outObj, "code", "seats_failed");
            throw new ControlException(code, SectlEnroll.DescribeError(code, outObj["detail"] as JsonObject), outObj);
        }

        var data = UnwrapData(outObj);
        var seatsObj = JsonHelp.AsObject(data["seats"]);
        return new ControlDeviceSeatsSnapshot(
            ClassRequired: JsonHelp.Bool(data, "classRequired"),
            ClassId: JsonHelp.Str(data, "classId"),
            ClassName: JsonHelp.Str(data, "className"),
            Rows: JsonHelp.Int(data, "rows"),
            Columns: JsonHelp.Int(data, "cols"),
            Source: JsonHelp.Str(data, "source"),
            LabelCount: seatsObj.Count);
    }

    public async Task<bool> UnenrollAsync(double timeoutSeconds = 10)
    {
        var cred = _credential;
        var removedRemotely = false;
        if (cred is not null)
        {
            try
            {
                var outObj = await PostJsonAsync(
                    $"{Host}/control/api/device/unenroll",
                    DeviceAuthBody(cred),
                    bearerToken: null,
                    timeoutSeconds).ConfigureAwait(false);
                removedRemotely = JsonHelp.Bool(outObj, "ok");
            }
            catch (Exception)
            {
                removedRemotely = false;
            }
        }

        ClearCredential();
        return removedRemotely;
    }

    public void ClearCredential()
    {
        Store.Clear();
        StateStore.Reset();
        DesiredState = null;
        SelectedClass = null;
        _wantClass = null;
        _credential = null;
        _stopReason = "cleared";

        var ws = _ws;
        if (ws is not null)
        {
            try { ws.Abort(); } catch (ObjectDisposedException) { }
        }

        SetState(ControlState.Unconfigured, "本地集控凭证已清除");
    }

    // --------------------------------------------------------- WebSocket 请求

    public async Task<JsonObject> RequestAsync(string topic, JsonObject? payload = null, TimeSpan? timeout = null)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
            throw new ControlException("not_connected", "尚未连接集控中心");

        var id = $"net_{Interlocked.Increment(ref _seq)}_{Random.Shared.Next(1000, 10000)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendRawAsync(new JsonObject
            {
                ["id"] = id,
                ["type"] = "req",
                ["topic"] = topic,
                ["payload"] = payload is null ? new JsonObject() : JsonHelp.Clone(payload),
            }).ConfigureAwait(false);

            var wait = timeout ?? TimeSpan.FromSeconds(_opt.RequestTimeoutSeconds);
            using var timer = new CancellationTokenSource(wait);
            using (timer.Token.Register(() => tcs.TrySetException(new ControlException("timeout", $"{topic} 等待响应超时"))))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<JsonObject> SelectClassAsync(string classId, TimeSpan? timeout = null)
    {
        var res = await RequestAsync(
            "class.select",
            new JsonObject { ["class_id"] = classId, ["platform"] = Platform },
            timeout).ConfigureAwait(false);

        if (JsonHelp.Bool(res, "ok"))
        {
            var payload = JsonHelp.AsObject(res["payload"]);
            if (JsonHelp.Bool(payload, "granted"))
            {
                SelectedClass = classId;
                _wantClass = classId;
                _stopReason = string.Empty;
                var taken = JsonHelp.AsObject(payload["taken_from"]);
                var takenName = JsonHelp.Str(taken, "device_name");
                var detail = takenName.Length > 0
                    ? $"已占用班级 {classId}，已接管 {takenName}"
                    : $"已占用班级 {classId}";
                Log(detail);
                SetState(ControlState.Selected, detail, new JsonObject { ["class_id"] = classId });
            }

            return res;
        }

        var code = JsonHelp.Str(res, "code");
        if (string.Equals(code, "busy", StringComparison.Ordinal))
        {
            _wantClass = null;
            var payload = JsonHelp.AsObject(res["payload"]);
            var retryMs = JsonHelp.Long(payload, "retry_after_ms");
            Log($"占班受 3 秒防抖限制，请 {Math.Ceiling(retryMs / 1000.0)} 秒后再试");
        }
        else
        {
            Log($"占班失败：{code} {JsonHelp.Str(res, "message")}");
        }

        return res;
    }

    public async Task<JsonObject> ReleaseClassAsync(TimeSpan? timeout = null)
    {
        var res = await RequestAsync(
            "class.release",
            new JsonObject { ["class_id"] = SelectedClass ?? string.Empty, ["platform"] = Platform },
            timeout).ConfigureAwait(false);
        SelectedClass = null;
        _wantClass = null;
        if (State == ControlState.Selected)
            SetState(ControlState.Connected, "已释放当前班级");
        return res;
    }

    public async Task<JsonObject> GetTreeAsync(TimeSpan? timeout = null)
    {
        var res = await RequestAsync("tree.get", new JsonObject(), timeout).ConfigureAwait(false);
        if (JsonHelp.Bool(res, "ok"))
        {
            Tree = JsonHelp.AsObject(res["payload"]);
            try { _opt.OnTree?.Invoke(Tree); } catch (Exception ex) { Log($"结构树回调异常：{ex.Message}"); }
        }

        return res;
    }

    public async Task ReportAsync(string cmdId, bool ok, JsonObject? payload = null, string code = "", string message = "")
    {
        _pendingCmds.TryRemove(cmdId, out _);
        var body = payload ?? new JsonObject();
        StateStore.RememberCommand(cmdId, ok, body, code);
        await SendResultAsync(cmdId, ok, body, code, message).ConfigureAwait(false);
    }

    // --------------------------------------------------------- 内部状态机

    private ControlCredential RequireCredential()
        => _credential ?? throw new ControlException("unconfigured", "本机尚未接入集控中心");

    private static JsonObject UnwrapData(JsonObject outObj)
        => outObj["data"] as JsonObject ?? outObj;

    private static JsonObject DeviceAuthBody(ControlCredential credential) => new()
    {
        ["deviceId"] = credential.DeviceId,
        ["secret"] = credential.DeviceSecret,
    };

    private ControlCredential ApplyEnrollResponse(JsonObject outObj, string resolvedDeviceName)
    {
        if (!JsonHelp.Bool(outObj, "ok"))
        {
            var errCode = JsonHelp.Str(outObj, "code") is { Length: > 0 } c ? c : "enroll_failed";
            throw new ControlException(errCode, SectlEnroll.DescribeError(errCode, outObj["detail"] as JsonObject), outObj);
        }

        var data = UnwrapData(outObj);
        var siteObj = JsonHelp.AsObject(data["site"]);
        var credential = new ControlCredential
        {
            DeviceId = JsonHelp.Str(data, "deviceId"),
            DeviceSecret = JsonHelp.Str(data, "secret"),
            Platform = Platform,
            Host = Host,
            SiteId = JsonHelp.Str(data, "siteId"),
            SitePublicId = JsonHelp.Str(siteObj, "publicId") is { Length: > 0 } pid ? pid : JsonHelp.Str(siteObj, "id"),
            SiteName = JsonHelp.Str(siteObj, "name"),
            DeviceName = resolvedDeviceName,
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };

        if (credential.DeviceId.Length == 0 || credential.DeviceSecret.Length == 0)
            throw new ControlException("bad_response", "接入返回缺少设备标识或密钥");

        var heartbeat = JsonHelp.Num(data, "heartbeatSeconds");
        if (heartbeat > 0) _heartbeatSeconds = (int)heartbeat;
        var offlineAfter = JsonHelp.Num(data, "offlineAfterSeconds");
        if (offlineAfter > 0) _offlineAfterSeconds = (int)offlineAfter;

        Store.Save(credential);
        StateStore.Reset();
        DesiredState = null;
        _stopReason = string.Empty;
        _attempt = 0;
        _credential = credential;
        SetState(ControlState.Configured, "已保存接入凭证，准备连接");
        Log($"接入成功：已绑定 {credential.SiteLabel}");
        Start();
        return credential;
    }

    private async Task<JsonObject> PostJsonAsync(string url, JsonObject body, string? bearerToken, double timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        return await SendHttpJsonAsync(request, cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonObject> GetJsonAsync(string url, string? bearerToken, double timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        return await SendHttpJsonAsync(request, cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonObject> SendHttpJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonObject
                   ?? throw new ControlException("bad_response", $"集控接口返回格式异常（HTTP {(int)response.StatusCode}）");
        }
        catch (OperationCanceledException ex)
        {
            throw new ControlException("network", "请求集控中心超时，请检查网络连接", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ControlException("network", "无法连接到集控中心，请检查网络或服务地址", null, ex);
        }
        catch (JsonException ex)
        {
            throw new ControlException("bad_response", "集控接口返回的数据格式无法识别", null, ex);
        }
    }

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_credential is null)
                {
                    if (State != ControlState.Unconfigured)
                        SetState(ControlState.Unconfigured, "尚未接入集控中心");
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    continue;
                }

                if (_stopReason.Length > 0 && _stopReason != "cleared")
                    break;

                _stopReason = string.Empty;
                var shouldStop = false;
                try
                {
                    SetState(ControlState.Connecting, $"正在连接 {Host}");
                    await ConnectAndRunOnceAsync(_credential, ct).ConfigureAwait(false);
                }
                catch (StopReconnectException ex)
                {
                    _stopReason = ex.Reason;
                    SetState(ex.State, ex.Message, new JsonObject { ["reason"] = ex.Reason });
                    shouldStop = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"集控连接中断：{ex.Message}");
                }

                if (shouldStop || ct.IsCancellationRequested) break;
                if (_credential is null) continue;
                if (_stopReason == "revoked" || _stopReason == "replaced") break;

                _attempt++;
                var baseDelay = Math.Min(SectlControlProtocol.BackoffMaxSeconds, Math.Pow(2, _attempt - 1));
                var jitter = baseDelay * (0.8 + Random.Shared.NextDouble() * 0.4);
                SetState(ControlState.Connecting, $"连接断开，{jitter:0.0} 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(jitter), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ConnectAndRunOnceAsync(ControlCredential credential, CancellationToken ct)
    {
        var ws = _wsFactory();
        await using var _ = ws.ConfigureAwait(false);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(WsUrl), connectCts.Token).ConfigureAwait(false);

        _ws = ws;
        var receiver = ReceiveLoopAsync(ws, ct);
        try
        {
            var hello = await RequestAsync("hello", HelloPayload(credential), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!JsonHelp.Bool(hello, "ok"))
            {
                var code = JsonHelp.Str(hello, "code", "hello_failed");
                var msg = JsonHelp.Str(hello, "message", code);
                if (SectlControlProtocol.NoRetryCodes.Contains(code, StringComparer.Ordinal))
                {
                    if (string.Equals(code, "device_revoked", StringComparison.Ordinal))
                        throw new StopReconnectException(ControlState.Revoked, "设备凭证已被管理员移除，请重新接入", code);
                    throw new StopReconnectException(ControlState.Revoked, $"握手被拒绝：{msg}", code);
                }

                throw new ControlException(code, msg);
            }

            Hello = JsonHelp.AsObject(hello["payload"]);
            var limits = JsonHelp.AsObject(Hello["limits"]);
            var limitMs = JsonHelp.Num(limits, "cmdTimeoutMs");
            CommandTimeoutMs = limitMs > 0 ? (int)limitMs : SectlControlProtocol.DefaultCommandTimeoutMs;
            var hb = JsonHelp.Num(Hello, "heartbeat_seconds");
            _heartbeatSeconds = hb > 0 ? (int)hb : SectlControlProtocol.DefaultHeartbeatSeconds;
            var offline = JsonHelp.Num(Hello, "offline_after_seconds");
            _offlineAfterSeconds = offline > 0 ? (int)offline : SectlControlProtocol.DefaultOfflineAfterSeconds;
            _attempt = 0;

            var device = JsonHelp.AsObject(Hello["device"]);
            var name = JsonHelp.Str(device, "name", DeviceName);
            Log($"已连接集控：{name}（心跳 {_heartbeatSeconds}s / 离线阈值 {_offlineAfterSeconds}s）");
            SetState(ControlState.Connected, "已连接集控中心");

            if (_opt.AutoReselect && _wantClass is { Length: > 0 } want)
            {
                try { await SelectClassAsync(want).ConfigureAwait(false); }
                catch (Exception ex) { Log($"重连占班失败：{ex.Message}"); }
            }

            await receiver.ConfigureAwait(false);
        }
        finally
        {
            try { ws.Abort(); } catch (ObjectDisposedException) { }
            FailPending();
            _ws = null;
            await Task.WhenAny(receiver, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(IControlWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var assembling = new MemoryStream();
        var ended = false;
        try
        {
            while (!ended && !ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ended = true;
                        break;
                    }

                    if (result.Count > 0) assembling.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (ended) break;
                if (assembling.Length == 0) continue;
                var text = Encoding.UTF8.GetString(assembling.GetBuffer(), 0, (int)assembling.Length);
                assembling.SetLength(0);

                await HandleTextAsync(text).ConfigureAwait(false);
                if (_stopReason.Length > 0)
                {
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, _stopReason, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            Log($"连接异常：{ex.Message}");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private JsonObject HelloPayload(ControlCredential credential) => new()
    {
        ["protocol"] = SectlControlProtocol.ProtocolVersion,
        ["platform"] = Platform,
        ["device_id"] = credential.DeviceId,
        ["secret"] = credential.DeviceSecret,
        ["device_name"] = DeviceName,
        ["version"] = _opt.AppVersion,
        ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        ["applied_state_revision"] = StateStore.AppliedRevision,
    };

    private JsonObject MachineJson()
    {
        var obj = new JsonObject { ["host"] = Environment.MachineName };
        if (!string.IsNullOrWhiteSpace(_opt.DeviceUuid))
            obj["guid"] = _opt.DeviceUuid.Trim();
        return obj;
    }

    private async Task SendRawAsync(JsonObject message)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private Task SendAckAsync(string commandId, bool accepted, string code = "")
    {
        var payload = new JsonObject { ["accepted"] = accepted };
        if (!accepted && code.Length > 0) payload["code"] = code;
        return SendRawAsync(new JsonObject
        {
            ["type"] = "req",
            ["id"] = commandId,
            ["topic"] = "cmd.ack",
            ["payload"] = payload,
        });
    }

    private Task SendResultAsync(string commandId, bool ok, JsonObject payload, string code = "", string message = "")
    {
        var body = new JsonObject
        {
            ["ok"] = ok,
            ["data"] = JsonHelp.CloneObject(payload),
        };
        if (!ok)
        {
            body["code"] = code.Length > 0 ? code : "failed";
            if (message.Length > 0) body["message"] = message;
        }

        return SendRawAsync(new JsonObject
        {
            ["type"] = "req",
            ["id"] = commandId,
            ["topic"] = "cmd.result",
            ["payload"] = body,
        });
    }

    private void FailPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new ControlException("disconnected", "连接已断开"));
        }
    }

    private async Task HandleTextAsync(string text)
    {
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (data is null) return;
        var type = JsonHelp.Str(data, "type");
        if (type == "res")
        {
            if (_pending.TryRemove(JsonHelp.Str(data, "id"), out var tcs))
                tcs.TrySetResult(data);
            return;
        }

        if (type != "evt") return;
        var topic = JsonHelp.Str(data, "topic");
        var payload = JsonHelp.AsObject(data["payload"]);
        switch (topic)
        {
            case "cmd":
                await HandleCommandAsync(payload).ConfigureAwait(false);
                break;
            case "evicted":
                HandleEvicted(payload);
                break;
            case "desired_state":
                HandleDesiredState(payload);
                break;
        }
    }

    private async Task HandleCommandAsync(JsonObject payload)
    {
        var commandId = JsonHelp.Str(payload, "command_id") is { Length: > 0 } cid ? cid : JsonHelp.Str(payload, "id");
        var cmd = JsonHelp.Str(payload, "cmd");
        var args = JsonHelp.AsObject(payload["args"]);
        var deadlineMs = JsonHelp.Int(payload, "deadline_ms", CommandTimeoutMs);
        var expiresAt = JsonHelp.Long(payload, "expires_at");
        if (expiresAt > 0 && expiresAt < 100_000_000_000L) expiresAt *= 1000;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var started = Stopwatch.GetTimestamp();

        if (expiresAt > 0 && nowMs > expiresAt)
        {
            await SendAckAsync(commandId, false, "expired").ConfigureAwait(false);
            InvokeDone(commandId, cmd, false, 0);
            return;
        }

        if (!_localRemoteAllowed)
        {
            await SendAckAsync(commandId, false, "local_remote_disabled").ConfigureAwait(false);
            InvokeDone(commandId, cmd, false, 0);
            return;
        }

        if (_opt.OnCommand is null || !_appSupported.Contains(cmd))
        {
            await SendAckAsync(commandId, false, "capability_unsupported").ConfigureAwait(false);
            InvokeDone(commandId, cmd, false, 0);
            return;
        }

        if (StateStore.TryGetCommand(commandId, out var cachedOk, out var cachedPayload, out var cachedCode))
        {
            await SendAckAsync(commandId, true).ConfigureAwait(false);
            await SendResultAsync(commandId, cachedOk, cachedPayload, cachedCode).ConfigureAwait(false);
            return;
        }

        await SendAckAsync(commandId, true).ConfigureAwait(false);
        try { _opt.OnCommandStart?.Invoke(commandId, cmd); } catch { }

        async Task FinishAsync(bool ok, JsonObject body, string code = "", string message = "")
        {
            _pendingCmds.TryRemove(commandId, out _);
            StateStore.RememberCommand(commandId, ok, body, code);
            await SendResultAsync(commandId, ok, body, code, message).ConfigureAwait(false);
            var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
            InvokeDone(commandId, cmd, ok, Math.Round(elapsed, 3));
        }

        var invocation = new CommandInvocation
        {
            Id = commandId,
            CommandId = commandId,
            Cmd = cmd,
            Args = JsonHelp.CloneObject(args),
            Issuer = JsonHelp.Str(payload, "issuer"),
            DeadlineMs = deadlineMs,
            ExpiresAt = expiresAt,
        };

        object? result;
        try
        {
            result = _opt.OnCommand(invocation);
        }
        catch (UnsupportedCommandException ex)
        {
            await FinishAsync(false, new JsonObject { ["cmd"] = cmd }, "unsupported", ex.Detail).ConfigureAwait(false);
            return;
        }
        catch (ControlException ex)
        {
            await FinishAsync(false, new JsonObject { ["cmd"] = cmd }, ex.Code, ex.Detail).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await FinishAsync(false, new JsonObject { ["cmd"] = cmd }, "failed", ex.Message).ConfigureAwait(false);
            return;
        }

        if (ReferenceEquals(result, DeferredReply.Instance))
        {
            _pendingCmds[commandId] = new PendingCommand(cmd, DateTime.UtcNow.AddMilliseconds(deadlineMs));
            _ = Task.Run(() => CommandWatchdogAsync(commandId, cmd, deadlineMs));
            return;
        }

        if (result is null)
        {
            await FinishAsync(true, new JsonObject { ["ack"] = cmd }).ConfigureAwait(false);
            return;
        }

        if (result is JsonObject body)
        {
            await FinishAsync(true, body).ConfigureAwait(false);
            return;
        }

        await FinishAsync(true, new JsonObject { ["detail"] = result.ToString() }).ConfigureAwait(false);
    }

    private void InvokeDone(string commandId, string cmd, bool ok, double seconds)
    {
        try { _opt.OnCommandDone?.Invoke(commandId, cmd, ok, seconds); } catch { }
    }

    private async Task CommandWatchdogAsync(string cmdId, string cmd, int deadlineMs)
    {
        if (!_pendingCmds.TryGetValue(cmdId, out var pending)) return;
        var wait = pending.DeadlineUtc - DateTime.UtcNow - TimeSpan.FromMilliseconds(200);
        if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
        if (!_pendingCmds.TryRemove(cmdId, out _)) return;

        var body = new JsonObject { ["cmd"] = cmd };
        StateStore.RememberCommand(cmdId, false, body, "timeout");
        await SendResultAsync(cmdId, false, body, "timeout", "客户端执行超时").ConfigureAwait(false);
        InvokeDone(cmdId, cmd, false, Math.Round(deadlineMs / 1000.0, 3));
    }

    private void HandleDesiredState(JsonObject payload)
    {
        DesiredState = JsonHelp.CloneObject(payload);
        var rev = JsonHelp.Long(payload, "revision");
        if (rev > StateStore.AppliedRevision) StateStore.SetAppliedRevision(rev);
        try { _opt.OnDesiredState?.Invoke(DesiredState); } catch { }
    }

    private void HandleEvicted(JsonObject payload)
    {
        var reason = JsonHelp.Str(payload, "reason");
        try { _opt.OnEvicted?.Invoke(JsonHelp.CloneObject(payload)); } catch { }

        switch (reason)
        {
            case "superseded":
            {
                SelectedClass = null;
                _wantClass = null;
                var by = JsonHelp.Str(JsonHelp.AsObject(payload["by"]), "device_name");
                var msg = by.Length > 0 ? $"本班已被 {by} 接管" : "本班已被其他设备接管";
                Log(msg);
                SetState(ControlState.Superseded, msg, new JsonObject { ["reason"] = reason, ["by"] = by });
                return;
            }
            case "revoked":
            {
                _stopReason = "revoked";
                SelectedClass = null;
                _wantClass = null;
                const string msg = "该设备已被管理员从集控移除，请重新接入";
                Log(msg);
                SetState(ControlState.Revoked, msg, new JsonObject { ["reason"] = reason });
                return;
            }
            case "replaced":
            {
                _stopReason = "replaced";
                SelectedClass = null;
                _wantClass = null;
                const string msg = "同一设备在别处重新连接，本机连接已停止";
                Log(msg);
                SetState(ControlState.Stopped, msg, new JsonObject { ["reason"] = reason });
                return;
            }
            default:
                SetState(ControlState.Connected, "占班已释放", new JsonObject { ["reason"] = reason });
                return;
        }
    }

    private void SetState(ControlState state, string message, JsonObject? extra = null)
    {
        var detail = new JsonObject
        {
            ["message"] = message,
            ["previous"] = State.WireName(),
            ["selected_class"] = SelectedClass,
        };
        JsonHelp.Merge(detail, extra);
        var unchanged = state == State && StateDetail.ToJsonString() == detail.ToJsonString();
        State = state;
        StateDetail = detail;
        if (unchanged) return;
        try { _opt.OnState?.Invoke(state, detail); } catch { }
    }

    private void Log(string message)
    {
        try { _opt.OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}"); } catch { }
    }

    private sealed class StopReconnectException : Exception
    {
        public StopReconnectException(ControlState state, string message, string reason) : base(message)
        {
            State = state;
            Reason = reason;
        }

        public ControlState State { get; }
        public string Reason { get; }
    }

    private sealed record PendingCommand(string Cmd, DateTime DeadlineUtc);
}
