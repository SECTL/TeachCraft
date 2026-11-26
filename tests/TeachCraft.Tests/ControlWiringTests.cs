using System.Net.Http;
using TeachCraft.App.ControlCenter;
using TeachCraft.App.ViewModels;
using TeachCraft.Core.Cloud;
using TeachCraft.Core.Control;

namespace TeachCraft.Tests;

/// <summary>
/// 集控装配与抽屉隔离回归：
/// 1. 走查模式下哪怕本地 settings.json 写着 Enabled=true 也必须返回 Disabled；
/// 2. 默认关闭、打开抽屉不发任何请求；
/// 3. 集控面板与更新/账号/工具/项目面板严格互斥；
/// 4. 设备凭证默认经保护信封落盘且 ToString 脱敏。
/// </summary>
public sealed class ControlWiringTests : IDisposable
{
    private readonly string _dir;

    public ControlWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "teachcraft-control-wiring", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (Exception) { }
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("装配或打开面板时绝不能发网络请求");
        }
    }

    [Fact]
    public void Walkthrough_ReturnsDisabled_EvenWithEnabledSettings()
    {
        new FileControlSettingsStore(_dir).Save(new ControlSettings(Enabled: true, Host: "https://example.invalid"));
        var handler = new ExplodingHandler();
        var appVersion = typeof(ControlCenterService).Assembly.GetName().Version!.ToString(3);

        var runtime = ControlWiring.Create(isWalkthrough: true, appVersion: appVersion, appDataRoot: _dir, handler: handler);

        Assert.False(runtime.IsConfigured);
        Assert.Null(runtime.Service);
        Assert.False(runtime.Snapshot.Enabled);
        Assert.Equal(0, handler.Requests);
        Assert.False(File.Exists(Path.Combine(_dir, "credential.json")));
    }

    [Fact]
    public void OpenControlPanel_DoesNotGoOnline_AndIsMutuallyExclusive()
    {
        var handler = new ExplodingHandler();
        var appVersion = typeof(ControlCenterService).Assembly.GetName().Version!.ToString(3);
        var runtime = ControlWiring.Create(isWalkthrough: false, appVersion: appVersion, appDataRoot: _dir, handler: handler);
        var vm = new MainViewModel(controlRuntime: runtime);

        Assert.True(vm.Control.IsConfigured);
        Assert.False(vm.ControlCenter.ControlEnabled);
        Assert.Equal(0, handler.Requests);

        vm.OpenAccountPanel();
        Assert.True(vm.IsAccountPanelOpen);

        vm.OpenControlPanel();
        Assert.True(vm.IsControlPanelOpen);
        Assert.False(vm.IsAccountPanelOpen);
        Assert.False(vm.IsUpdatePanelOpen);
        Assert.False(vm.IsProjectPanelOpen);
        Assert.False(vm.IsToolPanelOpen);
        Assert.True(vm.IsPanelOpen);
        Assert.Equal(0, handler.Requests);

        vm.OpenUpdatePanel();
        Assert.False(vm.IsControlPanelOpen);
        Assert.True(vm.IsUpdatePanelOpen);

        vm.OpenControlPanel();
        vm.ClosePanels();
        Assert.False(vm.IsControlPanelOpen);
        Assert.False(vm.IsPanelOpen);
    }

    [Fact]
    public void SettingsStore_DefaultsOff_AndRecoversFromCorruption()
    {
        var store = new FileControlSettingsStore(_dir);
        var initial = store.Load();
        Assert.False(initial.Enabled);
        Assert.Equal(SectlPlatformDefaults.WebBaseUrl, initial.Host);
        Assert.True(initial.LocalRemoteAllowed);
        Assert.Null(store.LastError);

        File.WriteAllText(store.Path, "{broken-json");
        var corrupted = store.Load();
        Assert.False(corrupted.Enabled);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void CredentialStore_ProtectsSecret_AndRedactsToString()
    {
        var credPath = Path.Combine(_dir, "credential.json");
        var protector = new PlainTokenProtector();
        var store = new FileControlCredentialStore(credPath, protector);
        var cred = new ControlCredential
        {
            DeviceId = "dev_123",
            DeviceSecret = "sec_very_sensitive_value",
            Host = "https://sectl.cn",
            SiteId = "site_1",
            SitePublicId = "SC-6M34RQ",
            SiteName = "示例学校",
            DeviceName = "高一1班大屏",
        };

        store.Save(cred);
        var rawFile = File.ReadAllText(credPath);
        Assert.DoesNotContain("sec_very_sensitive_value", rawFile, StringComparison.Ordinal);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("dev_123", loaded!.DeviceId);
        Assert.Equal("sec_very_sensitive_value", loaded.DeviceSecret);
        Assert.DoesNotContain("sec_very_sensitive_value", loaded.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", loaded.ToString(), StringComparison.Ordinal);
    }
}
