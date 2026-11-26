using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Control;

namespace TeachCraft.App.ControlCenter;

/// <summary>
/// 集控服务的唯一装配点。走查模式下即便 settings.json 写着 Enabled=true 也必须返回 Disabled，
/// 确保走查截图与自动回归绝不建立真实 WebSocket 连接或写入本机 Control 目录。
/// </summary>
public static class ControlWiring
{
    public static ControlRuntime Create(
        bool isWalkthrough,
        string appVersion,
        string? appDataRoot = null,
        HttpMessageHandler? handler = null,
        Func<IControlWebSocket>? webSocketFactory = null)
    {
        if (isWalkthrough)
            return ControlRuntime.Disabled;

        var controlDir = appDataRoot is null
            ? FileControlSettingsStore.GetDefaultDirectory()
            : Path.GetFullPath(appDataRoot);

        var settingsStore = new FileControlSettingsStore(controlDir);
        var settings = settingsStore.Load();
        var credentialStore = new FileControlCredentialStore(Path.Combine(controlDir, "credential.json"));
        var stateStore = new FileControlStateStore(Path.Combine(controlDir, "state.json"));

        // 仅当用户曾开启过或显式使用集控时才会读取/创建设备标识，默认未开启不触碰磁盘。
        var deviceUuid = DeriveStableDeviceGuid(controlDir);
        var options = new ControlCenterOptions
        {
            Enabled = settings.Enabled,
            Host = settings.Host,
            Platform = SectlControlProtocol.PlatformName,
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "1.46.0" : appVersion,
            DeviceUuid = deviceUuid,
            DeviceName = string.IsNullOrWhiteSpace(settings.DeviceName)
                ? $"{Environment.MachineName}-TeachCraft"
                : settings.DeviceName,
            Capabilities = SectlControlProtocol.DefaultCapabilities,
            CredentialStore = credentialStore,
            StateStore = stateStore,
            HttpHandler = handler,
            WebSocketFactory = webSocketFactory,
            AutoReselect = false,
            LocalRemoteAllowed = settings.LocalRemoteAllowed,
        };

        var service = new ControlCenterService(options);
        return new ControlRuntime(true, service, settingsStore, credentialStore, stateStore, settings);
    }

    private static string DeriveStableDeviceGuid(string controlDir)
    {
        var raw = $"{Environment.MachineName}|{Environment.UserName}|{controlDir}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes).ToString("D");
    }
}
