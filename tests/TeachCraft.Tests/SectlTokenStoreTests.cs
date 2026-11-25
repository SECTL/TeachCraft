using TeachCraft.Core.Cloud;

namespace TeachCraft.Tests;

/// <summary>
/// 本地凭据与设备身份的落盘测试。全程用临时目录，绝不碰开发者真实的 %LOCALAPPDATA%。
/// </summary>
public sealed class SectlTokenStoreTests : IDisposable
{
    private readonly string _directory;

    public SectlTokenStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "teachcraft-cloud-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (Exception) { /* 临时目录清不掉不影响测试结论。 */ }
    }

    private static CloudTokens SampleTokens() => new()
    {
        AccessToken = "synthetic-access-token-示例中学",
        RefreshToken = "synthetic-refresh-token-苏晚晴",
        TokenType = "Bearer",
        Protector = "dpapi",
        ObtainedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
    };

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new FileCloudTokenStore(_directory);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNullWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_directory, "tokens.json"), "{ this is not json");
        var store = new FileCloudTokenStore(_directory);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_AtomicWrite_LeavesNoTempFile()
    {
        var store = new FileCloudTokenStore(_directory, new NoopTokenProtector());
        store.Save(SampleTokens());

        Assert.True(File.Exists(Path.Combine(_directory, "tokens.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "tokens.json.tmp")));
    }

    [Fact]
    public void Load_LeftoverTempFile_IsIgnored()
    {
        var store = new FileCloudTokenStore(_directory, new NoopTokenProtector());
        var tokens = SampleTokens();
        store.Save(tokens);
        File.WriteAllText(Path.Combine(_directory, "tokens.json.tmp"), "{ broken temp");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(tokens.RefreshToken, loaded!.RefreshToken);
    }

    [Fact]
    public void Dpapi_RoundTrip_WhenWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // 非 Windows 没有 DPAPI，跳过。

        var protector = new DpapiTokenProtector();
        Assert.True(protector.IsAvailable);
        var store = new FileCloudTokenStore(_directory, protector);
        var tokens = SampleTokens();

        store.Save(tokens);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(tokens.AccessToken, loaded!.AccessToken);
        Assert.Equal(tokens.RefreshToken, loaded.RefreshToken);
    }

    [Fact]
    public void Save_TokensFile_DoesNotContainPlaintextRefreshToken()
    {
        var tokens = SampleTokens();
        var store = new FileCloudTokenStore(_directory); // Windows 走 dpapi，其它平台走 base64 兜底。
        store.Save(tokens);

        var text = File.ReadAllText(Path.Combine(_directory, "tokens.json"));
        Assert.DoesNotContain(tokens.RefreshToken, text);
        Assert.DoesNotContain(tokens.AccessToken, text);
    }

    [Fact]
    public void CloudTokens_ToString_NeverLeaksSecrets()
    {
        var tokens = SampleTokens();
        var text = tokens.ToString();

        Assert.DoesNotContain(tokens.AccessToken, text);
        Assert.DoesNotContain(tokens.RefreshToken, text);
    }

    [Fact]
    public void DeviceUuid_StableAcrossLoads_And36Chars()
    {
        var store = new FileInstallIdentityStore(_directory);
        var first = store.GetOrCreateDeviceUuid();
        var second = store.GetOrCreateDeviceUuid();

        Assert.Equal(36, first.Length);
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, second);
    }

    [Fact]
    public void DeviceUuid_MalformedFile_Regenerates()
    {
        File.WriteAllText(Path.Combine(_directory, "device.json"), "{ not json");
        var store = new FileInstallIdentityStore(_directory);

        var uuid = store.GetOrCreateDeviceUuid();

        Assert.Equal(36, uuid.Length);
        Assert.True(Guid.TryParse(uuid, out _));
    }

    [Fact]
    public void DeviceIdentity_PersistFailure_ThrowsChineseError()
    {
        var blocker = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocker, "synthetic blocker");
        var store = new FileInstallIdentityStore(Path.Combine(blocker, "Account"));

        var ex = Assert.Throws<CloudAuthException>(() => store.GetOrCreateDeviceUuid());

        Assert.Equal(CloudErrorText.DeviceIdentityPersistFailed, ex.Message);
        Assert.Contains("设备标识", ex.Message);
    }

    [Fact]
    public void Load_CrossProtectorBlob_ReturnsNull()
    {
        // 用 dpapi 写、用 plain 读：信封里的保护方式不匹配必须 fail closed。
        if (!OperatingSystem.IsWindows()) return; // 没有 dpapi 就造不出 dpapi 信封。

        var writer = new FileCloudTokenStore(_directory, new DpapiTokenProtector());
        writer.Save(SampleTokens());
        var reader = new FileCloudTokenStore(_directory, new PlainTokenProtector());

        Assert.Null(reader.Load());
    }
}
