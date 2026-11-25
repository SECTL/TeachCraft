using System.Net.Http;
using TeachCraft.App.Cloud;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 装配闸门：走查与配置不全时一律不造服务，哪怕本机偏好写着 Enabled=true。
/// 用一个「收到请求就抛」的 handler 证明构造期没有任何网络动作。
/// </summary>
public sealed class CloudWiringTests : IDisposable
{
    private readonly string _directory;

    public CloudWiringTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "teachcraft-wiring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (Exception) { /* 临时目录清不掉不影响测试结论。 */ }
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("装配期不该发请求");
        }
    }

    private void WriteEnabledSettings()
        => new FileCloudSyncSettingsStore(_directory).Save(new CloudSyncSettings(Enabled: true, ConsentVersion: 1));

    [Fact]
    public void Walkthrough_ReturnsDisabled_EvenWithEnabledSettings()
    {
        WriteEnabledSettings();
        var handler = new ExplodingHandler();

        var runtime = CloudWiring.Create(
            isWalkthrough: true,
            clientId: SectlPlatformDefaults.ClientId,
            redirectUri: SectlPlatformDefaults.RedirectUri,
            appDataRoot: _directory,
            handler: handler);

        Assert.False(runtime.IsConfigured);
        Assert.Null(runtime.Account);
        Assert.Null(runtime.SeatDocuments);
        Assert.Null(runtime.Rosters);
        Assert.False(runtime.Snapshot.Enabled);
        Assert.Equal(0, handler.Requests);
        Assert.False(File.Exists(Path.Combine(_directory, "device.json")), "走查连设备身份都不该落盘");
    }

    [Fact]
    public void BlankClientId_ReturnsDisabled()
    {
        WriteEnabledSettings();

        var runtime = CloudWiring.Create(false, "   ", SectlPlatformDefaults.RedirectUri, _directory);

        Assert.False(runtime.IsConfigured);
        Assert.Null(runtime.Account);
        Assert.Null(runtime.SeatDocuments);
        Assert.Null(runtime.Rosters);
    }

    [Fact]
    public void BlankRedirectUri_ReturnsDisabled()
    {
        WriteEnabledSettings();

        var runtime = CloudWiring.Create(false, SectlPlatformDefaults.ClientId, "", _directory);

        Assert.False(runtime.IsConfigured);
        Assert.Null(runtime.Account);
        Assert.Null(runtime.Rosters);
    }

    [Fact]
    public void Configured_CreatesAllServices_WithoutNetwork()
    {
        WriteEnabledSettings();
        var handler = new ExplodingHandler();

        var runtime = CloudWiring.Create(
            isWalkthrough: false,
            clientId: SectlPlatformDefaults.ClientId,
            redirectUri: SectlPlatformDefaults.RedirectUri,
            appDataRoot: _directory,
            handler: handler);

        Assert.True(runtime.IsConfigured);
        Assert.NotNull(runtime.Account);
        Assert.NotNull(runtime.SeatDocuments);
        Assert.NotNull(runtime.Rosters);
        Assert.True(runtime.Snapshot.Enabled);
        Assert.Equal(0, handler.Requests);
        Assert.True(File.Exists(Path.Combine(_directory, "device.json")));
    }

    [Fact]
    public void Disabled_UsesNoopSettingsStore()
    {
        var store = CloudRuntime.Disabled.Settings;

        Assert.Equal("", store.Path);
        store.Save(new CloudSyncSettings(Enabled: true));
        Assert.False(store.Load().Enabled);
    }

    [Fact]
    public void DeviceIdentityPersistFailure_SurfacesChineseError()
    {
        WriteEnabledSettings();
        var blocker = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocker, "synthetic blocker");
        var unwritableRoot = Path.Combine(blocker, "Account");

        var ex = Assert.Throws<CloudAuthException>(() => CloudWiring.Create(
            isWalkthrough: false,
            clientId: SectlPlatformDefaults.ClientId,
            redirectUri: SectlPlatformDefaults.RedirectUri,
            appDataRoot: unwritableRoot));

        Assert.Equal(CloudErrorText.DeviceIdentityPersistFailed, ex.Message);
    }
}
