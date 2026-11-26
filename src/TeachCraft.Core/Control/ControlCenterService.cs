using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Core.Control;

public sealed class ControlCenterOptions
{
    public bool Enabled { get; init; } = false;
    public string Host { get; init; } = SectlPlatformDefaults.WebBaseUrl;
    public string Platform { get; init; } = SectlControlProtocol.PlatformName;
    public string AppVersion { get; init; } = "1.46.0";
    public string DeviceUuid { get; init; } = string.Empty;
    public string? DeviceName { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = SectlControlProtocol.DefaultCapabilities;
    public IControlCredentialStore? CredentialStore { get; init; }
    public IControlStateStore? StateStore { get; init; }
    public HttpMessageHandler? HttpHandler { get; init; }
    public Func<IControlWebSocket>? WebSocketFactory { get; init; }
    public bool AutoReselect { get; init; } = false;
    public bool LocalRemoteAllowed { get; init; } = true;
    public Action<string>? Log { get; init; }
}

public sealed record ControlCenterSnapshot
{
    public string State { get; init; } = "stopped";
    public string Label { get; init; } = "未开启集控";
    public string? SelectedClass { get; init; }
    public string? DeviceId { get; init; }
    public string SiteLabel { get; init; } = string.Empty;
    public long AppliedStateRevision { get; init; }
    public bool Connected { get; init; }
    public bool NeedsEnroll { get; init; }
    public bool Interrupted { get; init; }
    public string StopReason { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;
    public JsonObject Detail { get; init; } = new();
}

/// <summary>
/// TeachCraft 侧 SECTL 集控服务生命周期包装（无 DI 容器依赖）。
/// 回调均运行在后台线程，UI 层需通过 Dispatcher.UIThread 切换线程。
/// </summary>
public sealed class ControlCenterService : IAsyncDisposable
{
    private static readonly Dictionary<ControlState, string> Labels = new()
    {
        [ControlState.Unconfigured] = "未接入地点（等待填写地点ID + 配对码，或使用 SECTL 账号接入）",
        [ControlState.Configured] = "已保存凭证，等待连接",
        [ControlState.Connecting] = "正在连接集控中心…",
        [ControlState.Connected] = "已连接集控中心",
        [ControlState.Selected] = "已占用班级，可接收远程指令",
        [ControlState.Superseded] = "当前班级已被其他设备接管",
        [ControlState.Revoked] = "设备已被管理员移除，需重新接入",
        [ControlState.Stopped] = "集控未开启",
    };

    private readonly ConcurrentDictionary<string, Func<CommandInvocation, object?>> _handlers =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlCenterOptions _options;
    private SectlControlClient? _client;
    private ControlCenterSnapshot _snapshot = new();
    private ControlState _lastState = ControlState.Stopped;
    private JsonObject _lastDetail = new();
    private volatile bool _disposed;

    public ControlCenterService(ControlCenterOptions? options = null)
    {
        _options = options ?? new ControlCenterOptions();
        RefreshInitialSnapshot();
    }

    public ControlCenterSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public SectlControlClient? Client => _client;
    public bool HasCredential => (_options.CredentialStore?.Load() ?? _client?.Credential) is not null;

    public event EventHandler<ControlCenterSnapshot>? StateChanged;
    public event EventHandler<CommandInvocation>? CommandReceived;
    public event EventHandler? EnrollCodeConsumed;

    private void RefreshInitialSnapshot()
    {
        var cred = _options.CredentialStore?.Load();
        if (!_options.Enabled)
        {
            _snapshot = new ControlCenterSnapshot
            {
                State = "stopped",
                Label = cred is null ? "集控未开启" : "集控未开启（本机已保存接入凭证）",
                DeviceId = cred?.DeviceId,
                SiteLabel = cred?.SiteLabel ?? string.Empty,
                NeedsEnroll = cred is null,
                AppliedStateRevision = _options.StateStore?.AppliedRevision ?? 0,
            };
            return;
        }

        var state = cred is null ? ControlState.Unconfigured : ControlState.Configured;
        _snapshot = new ControlCenterSnapshot
        {
            State = state.WireName(),
            Label = Labels[state],
            DeviceId = cred?.DeviceId,
            SiteLabel = cred?.SiteLabel ?? string.Empty,
            NeedsEnroll = cred is null,
            AppliedStateRevision = _options.StateStore?.AppliedRevision ?? 0,
        };
    }

    public void UpdateRuntimeOptions(string host, string? deviceName, bool localRemoteAllowed)
    {
        _options = new ControlCenterOptions
        {
            Enabled = _options.Enabled,
            Host = ControlSettings.NormalizeHost(host),
            Platform = _options.Platform,
            AppVersion = _options.AppVersion,
            DeviceUuid = _options.DeviceUuid,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? _options.DeviceName : deviceName.Trim(),
            Capabilities = _options.Capabilities,
            CredentialStore = _options.CredentialStore,
            StateStore = _options.StateStore,
            HttpHandler = _options.HttpHandler,
            WebSocketFactory = _options.WebSocketFactory,
            AutoReselect = _options.AutoReselect,
            LocalRemoteAllowed = localRemoteAllowed,
            Log = _options.Log,
        };

        var client = _client;
        if (client is not null)
        {
            client.UpdateDesiredHost(_options.Host);
            client.SetLocalRemoteAllowed(localRemoteAllowed);
        }
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Enabled)
            {
                _options = new ControlCenterOptions
                {
                    Enabled = true,
                    Host = _options.Host,
                    Platform = _options.Platform,
                    AppVersion = _options.AppVersion,
                    DeviceUuid = _options.DeviceUuid,
                    DeviceName = _options.DeviceName,
                    Capabilities = _options.Capabilities,
                    CredentialStore = _options.CredentialStore,
                    StateStore = _options.StateStore,
                    HttpHandler = _options.HttpHandler,
                    WebSocketFactory = _options.WebSocketFactory,
                    AutoReselect = _options.AutoReselect,
                    LocalRemoteAllowed = _options.LocalRemoteAllowed,
                    Log = _options.Log,
                };
            }

            if (_client is not null) return true;

            var client = CreateClient();
            client.Start();
            _client = client;
            HandleState(client.State, client.StateDetail);
            return true;
        }
        catch (Exception ex)
        {
            Log($"启动集控失败：{ex.Message}");
            var broken = _client;
            _client = null;
            if (broken is not null)
            {
                try { await broken.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = _client;
            _client = null;
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            _options = new ControlCenterOptions
            {
                Enabled = false,
                Host = _options.Host,
                Platform = _options.Platform,
                AppVersion = _options.AppVersion,
                DeviceUuid = _options.DeviceUuid,
                DeviceName = _options.DeviceName,
                Capabilities = _options.Capabilities,
                CredentialStore = _options.CredentialStore,
                StateStore = _options.StateStore,
                HttpHandler = _options.HttpHandler,
                WebSocketFactory = _options.WebSocketFactory,
                AutoReselect = _options.AutoReselect,
                LocalRemoteAllowed = _options.LocalRemoteAllowed,
                Log = _options.Log,
            };

            var cred = _options.CredentialStore?.Load();
            Publish(new ControlCenterSnapshot
            {
                State = "stopped",
                Label = cred is null ? "集控未开启" : "集控已暂停",
                DeviceId = cred?.DeviceId,
                SiteLabel = cred?.SiteLabel ?? string.Empty,
                NeedsEnroll = cred is null,
                AppliedStateRevision = _options.StateStore?.AppliedRevision ?? 0,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void RegisterHandler(string cmd, Func<CommandInvocation, object?> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cmd);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[cmd] = handler;
    }

    public void UnregisterHandler(string cmd) => _handlers.TryRemove(cmd, out _);

    public void SetLocalRemoteAllowed(bool allowed)
    {
        UpdateRuntimeOptions(_options.Host, _options.DeviceName, allowed);
    }

    public async Task<(bool Ok, string Message, ControlCredential? Credential)> EnrollAsync(
        string siteId,
        string code,
        string? deviceName = null)
    {
        if (_disposed) return (false, "服务已停止", null);
        if (string.IsNullOrWhiteSpace(siteId)) return (false, "地点ID 不能为空（形如 SC-6M34RQ）", null);
        if (string.IsNullOrWhiteSpace(code)) return (false, "配对码不能为空（形如 ABCD-7KQ2）", null);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = EnsureClientStartedLocked();
            var cred = await client.EnrollAsync(siteId, code, deviceName ?? _options.DeviceName).ConfigureAwait(false);
            EnrollCodeConsumed?.Invoke(this, EventArgs.Empty);
            HandleState(client.State, client.StateDetail);
            return (true, $"已接入 {cred.SiteLabel}", cred);
        }
        catch (Exception ex)
        {
            Log($"配对码接入失败：{ex.Message}");
            return (false, ex.Message, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Ok, string Message, ControlCredential? Credential)> EnrollWithAccountAsync(
        string accessToken,
        string siteId,
        string? deviceName = null,
        string? classId = null)
    {
        if (_disposed) return (false, "服务已停止", null);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = EnsureClientStartedLocked();
            var cred = await client.EnrollWithAccountAsync(accessToken, siteId, deviceName ?? _options.DeviceName, classId)
                .ConfigureAwait(false);
            HandleState(client.State, client.StateDetail);
            return (true, $"已通过 SECTL 账号接入 {cred.SiteLabel}", cred);
        }
        catch (Exception ex)
        {
            Log($"账号接入失败：{ex.Message}");
            return (false, ex.Message, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ControlSiteSummary>> FetchSitesWithAccountAsync(string accessToken)
    {
        if (_disposed) return Array.Empty<ControlSiteSummary>();
        // 使用临时短客户端仅发一次 HTTP GET /control/api/sites，不启 WebSocket，不改配置状态。
        var transient = CreateClient(withStores: false);
        try
        {
            return await transient.FetchSitesWithAccountAsync(accessToken).ConfigureAwait(false);
        }
        finally
        {
            await transient.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<ControlTreeSnapshot> FetchDeviceTreeAsync()
    {
        var client = _client;
        if (client is not null && client.Credential is not null)
            return await client.FetchDeviceTreeAsync().ConfigureAwait(false);

        var transient = CreateClient(withStores: true);
        try
        {
            return await transient.FetchDeviceTreeAsync().ConfigureAwait(false);
        }
        finally
        {
            await transient.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<(string ClassId, string ClassName)> AssignDeviceClassAsync(string? classId)
    {
        var client = _client;
        if (client is not null && client.Credential is not null)
            return await client.AssignDeviceClassAsync(classId).ConfigureAwait(false);

        var transient = CreateClient(withStores: true);
        try
        {
            return await transient.AssignDeviceClassAsync(classId).ConfigureAwait(false);
        }
        finally
        {
            await transient.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<ControlDeviceSeatsSnapshot> FetchDeviceSeatsAsync(string? classId = null)
    {
        var client = _client;
        if (client is not null && client.Credential is not null)
            return await client.FetchDeviceSeatsAsync(classId).ConfigureAwait(false);

        var transient = CreateClient(withStores: true);
        try
        {
            return await transient.FetchDeviceSeatsAsync(classId).ConfigureAwait(false);
        }
        finally
        {
            await transient.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<JsonObject> SelectClassAsync(string classId)
    {
        var client = _client ?? throw new ControlException("not_connected", "集控尚未连接");
        return client.SelectClassAsync(classId);
    }

    public Task<JsonObject> ReleaseClassAsync()
    {
        var client = _client ?? throw new ControlException("not_connected", "集控尚未连接");
        return client.ReleaseClassAsync();
    }

    public async Task<bool> UnenrollAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                var remoteRemoved = await _client.UnenrollAsync().ConfigureAwait(false);
                HandleState(_client.State, _client.StateDetail);
                return remoteRemoved;
            }

            var transient = CreateClient(withStores: true);
            try
            {
                var removed = await transient.UnenrollAsync().ConfigureAwait(false);
                RefreshInitialSnapshot();
                Publish(_snapshot);
                return removed;
            }
            finally
            {
                await transient.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private SectlControlClient EnsureClientStartedLocked()
    {
        if (_client is not null) return _client;
        var client = CreateClient();
        client.Start();
        _client = client;
        return client;
    }

    private SectlControlClient CreateClient(bool withStores = true) => new(new SectlControlClientOptions
    {
        Host = _options.Host,
        Platform = _options.Platform,
        AppVersion = _options.AppVersion,
        DeviceUuid = _options.DeviceUuid,
        DeviceName = _options.DeviceName,
        Capabilities = _options.Capabilities,
        CredentialStore = withStores ? (_options.CredentialStore ?? new NoopControlCredentialStore()) : new NoopControlCredentialStore(),
        StateStore = withStores ? (_options.StateStore ?? new NoopControlStateStore()) : new NoopControlStateStore(),
        HttpHandler = _options.HttpHandler,
        WebSocketFactory = _options.WebSocketFactory,
        AutoReselect = _options.AutoReselect,
        LocalRemoteAllowed = _options.LocalRemoteAllowed,
        OnState = withStores ? HandleState : null,
        OnCommand = withStores ? DispatchCommand : null,
        OnEvicted = withStores ? HandleEvicted : null,
        OnDesiredState = withStores ? HandleDesiredState : null,
        OnLog = Log,
    });

    private object? DispatchCommand(CommandInvocation invocation)
    {
        CommandReceived?.Invoke(this, invocation);
        if (!_handlers.TryGetValue(invocation.Cmd, out var handler))
            throw new UnsupportedCommandException($"当前版本不支持指令 {invocation.Cmd}");
        return handler(invocation);
    }

    private void HandleState(ControlState state, JsonObject detail)
    {
        _lastState = state;
        _lastDetail = detail;
        var credential = _client?.Credential;
        var stopReason = JsonHelp.Str(detail, "reason");
        var message = JsonHelp.Str(detail, "message");
        Publish(new ControlCenterSnapshot
        {
            State = state.WireName(),
            Label = message.Length > 0 ? message : (Labels.TryGetValue(state, out var label) ? label : state.WireName()),
            SelectedClass = _client?.SelectedClass,
            DeviceId = credential?.DeviceId,
            SiteLabel = credential?.SiteLabel ?? string.Empty,
            AppliedStateRevision = _client?.AppliedStateRevision ?? 0,
            Connected = state is ControlState.Connected or ControlState.Selected or ControlState.Superseded,
            NeedsEnroll = state is ControlState.Unconfigured or ControlState.Revoked,
            Interrupted = state is ControlState.Superseded or ControlState.Revoked,
            StopReason = stopReason,
            LastError = state == ControlState.Revoked ? message : string.Empty,
            Detail = detail,
        });
    }

    private void HandleDesiredState(JsonObject payload)
    {
        var rev = JsonHelp.Long(payload, "revision");
        Log($"收到期望状态 revision={rev}（已记录本机）");
        HandleState(_lastState, _lastDetail);
    }

    private void HandleEvicted(JsonObject payload)
    {
        var reason = JsonHelp.Str(payload, "reason");
        if (string.Equals(reason, "revoked", StringComparison.Ordinal))
        {
            try { _client?.ClearCredential(); } catch { }
            Log("设备已被管理员移除，已清除本地凭证");
        }
    }

    private void Publish(ControlCenterSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        try { StateChanged?.Invoke(this, snapshot); } catch { }
    }

    private void Log(string message)
    {
        try { _options.Log?.Invoke(message); } catch { }
    }
}
