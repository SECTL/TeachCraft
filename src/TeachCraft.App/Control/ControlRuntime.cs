using TeachCraft.Core.Control;

namespace TeachCraft.App.ControlCenter;

/// <summary>
/// 集控中心在进程内的装配结果；未开启或走查路径固定拿到 Disabled，
/// 保证默认配置下不建立任何长连接、不读写本机 Control 目录。
/// </summary>
public sealed class ControlRuntime
{
    public static ControlRuntime Disabled { get; } = new(
        isConfigured: false,
        service: null,
        settings: new NoopControlSettingsStore(),
        credentialStore: new NoopControlCredentialStore(),
        stateStore: new NoopControlStateStore(),
        initialSettings: ControlSettings.Default);

    public ControlRuntime(
        bool isConfigured,
        ControlCenterService? service,
        IControlSettingsStore settings,
        IControlCredentialStore credentialStore,
        IControlStateStore stateStore,
        ControlSettings initialSettings)
    {
        IsConfigured = isConfigured;
        Service = service;
        Settings = settings;
        CredentialStore = credentialStore;
        StateStore = stateStore;
        Snapshot = initialSettings;
    }

    public bool IsConfigured { get; }
    public ControlCenterService? Service { get; }
    public IControlSettingsStore Settings { get; }
    public IControlCredentialStore CredentialStore { get; }
    public IControlStateStore StateStore { get; }
    public ControlSettings Snapshot { get; }
}
