using System.Text.Json;

namespace TeachCraft.Core.Cloud;

public interface IInstallIdentityStore
{
    string GetOrCreateDeviceUuid();
}

/// <summary>走查/自动化用的空实现：返回一个固定的合成 UUID（36 字符）。</summary>
public sealed class NoopInstallIdentityStore : IInstallIdentityStore
{
    public string GetOrCreateDeviceUuid() => "00000000-0000-0000-0000-000000000001";
}

/// <summary>
/// 落在 %LOCALAPPDATA%\TeachCraft\Account\device.json，形如 {V:1, DeviceUuid:"..."}。
/// 文件损坏或 UUID 不合法就重新生成，绝不把坏值交给云端。
/// </summary>
public sealed class FileInstallIdentityStore : IInstallIdentityStore
{
    private const string FileName = "device.json";
    private readonly string _path;

    public FileInstallIdentityStore(string? directory = null)
    {
        var dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Account");
        _path = System.IO.Path.Combine(dir, FileName);
    }

    public string Path => _path;

    public string GetOrCreateDeviceUuid()
    {
        var existing = TryReadUuid(_path);
        if (existing is not null) return existing;

        var uuid = Guid.NewGuid().ToString("D");
        CloudJsonFile.WriteAtomic(_path, JsonSerializer.Serialize(
            new { V = 1, DeviceUuid = uuid }, new JsonSerializerOptions { WriteIndented = true }));

        // WriteAtomic 吞掉所有 IO 异常，必须读回确认真的落盘；
        // 否则每次启动都生成新 UUID，静默破坏 SECTL 设备绑定。
        if (!string.Equals(TryReadUuid(_path), uuid, StringComparison.Ordinal))
            throw new CloudAuthException(CloudErrorText.DeviceIdentityPersistFailed, "device_identity_persist_failed");

        return uuid;
    }

    private static string? TryReadUuid(string path)
    {
        var text = CloudJsonFile.ReadAllOrNull(path);
        if (text is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("V", out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var version) && version == 1
                && root.TryGetProperty("DeviceUuid", out var d) && d.ValueKind == JsonValueKind.String
                && Guid.TryParse(d.GetString(), out var parsed))
                return parsed.ToString("D");
        }
        catch (Exception)
        {
            // 损坏就重新生成。
        }
        return null;
    }
}
