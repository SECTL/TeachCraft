using System.Text.Json;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Core.Control;

/// <summary>
/// SECTL 集控中心偏好设置。默认关闭（Enabled = false）：
/// 未由用户明确开启时，不连接集控 WebSocket，也不发送任何设备状态。
/// </summary>
public sealed record ControlSettings(
    bool Enabled = false,
    string Host = SectlPlatformDefaults.WebBaseUrl,
    string? SiteId = null,
    string? DeviceName = null,
    bool LocalRemoteAllowed = true,
    string? SelectedClassId = null,
    string? SelectedClassName = null)
{
    public static ControlSettings Default { get; } = new();

    public static string NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SectlPlatformDefaults.WebBaseUrl;
        var trimmed = raw.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return SectlPlatformDefaults.WebBaseUrl;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }
}

public interface IControlSettingsStore
{
    ControlSettings Load();
    void Save(ControlSettings settings);
    bool TrySave(ControlSettings settings) { Save(settings); return true; }
    string Path { get; }
    string? LastError => null;
}

/// <summary>走查与测试默认用的空存储：始终返回默认关闭设置，不读写磁盘。</summary>
public sealed class NoopControlSettingsStore : IControlSettingsStore
{
    public ControlSettings Load() => ControlSettings.Default;
    public void Save(ControlSettings settings) { }
    public string Path => string.Empty;
}

/// <summary>落在 %LOCALAPPDATA%\TeachCraft\Control\settings.json。</summary>
public sealed class FileControlSettingsStore : IControlSettingsStore
{
    public const string SettingsLoadFailedReason = "集控配置文件损坏，已恢复为默认关闭。";
    private const string FileName = "settings.json";
    private readonly string _path;
    private string? _lastError;

    public FileControlSettingsStore(string? directory = null)
    {
        var dir = directory ?? GetDefaultDirectory();
        _path = System.IO.Path.Combine(dir, FileName);
    }

    public static string GetDefaultDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return System.IO.Path.Combine(baseDir, "TeachCraft", "Control");
    }

    public string Path => _path;
    public string? LastError => _lastError;

    public ControlSettings Load()
    {
        var text = ReadAllOrNull(_path);
        if (text is null)
        {
            _lastError = File.Exists(_path) ? SettingsLoadFailedReason : null;
            return ControlSettings.Default;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            _lastError = null;
            return new ControlSettings(
                Enabled: ReadBool(root, "Enabled", false),
                Host: ControlSettings.NormalizeHost(ReadString(root, "Host")),
                SiteId: ReadString(root, "SiteId"),
                DeviceName: ReadString(root, "DeviceName"),
                LocalRemoteAllowed: ReadBool(root, "LocalRemoteAllowed", true),
                SelectedClassId: ReadString(root, "SelectedClassId"),
                SelectedClassName: ReadString(root, "SelectedClassName"));
        }
        catch (Exception)
        {
            _lastError = SettingsLoadFailedReason;
            return ControlSettings.Default;
        }
    }

    public void Save(ControlSettings settings) => TrySave(settings);

    public bool TrySave(ControlSettings settings)
    {
        var normalized = settings with
        {
            Host = ControlSettings.NormalizeHost(settings.Host),
            SiteId = string.IsNullOrWhiteSpace(settings.SiteId) ? null : settings.SiteId.Trim(),
            DeviceName = string.IsNullOrWhiteSpace(settings.DeviceName) ? null : settings.DeviceName.Trim(),
            SelectedClassId = string.IsNullOrWhiteSpace(settings.SelectedClassId) ? null : settings.SelectedClassId.Trim(),
            SelectedClassName = string.IsNullOrWhiteSpace(settings.SelectedClassName) ? null : settings.SelectedClassName.Trim(),
        };

        var json = JsonSerializer.Serialize(new
        {
            normalized.Enabled,
            normalized.Host,
            normalized.SiteId,
            normalized.DeviceName,
            normalized.LocalRemoteAllowed,
            normalized.SelectedClassId,
            normalized.SelectedClassName,
        }, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
            _lastError = null;
            return true;
        }
        catch (Exception)
        {
            _lastError = "保存集控设置失败，请检查本机写入权限。";
            return false;
        }
    }

    private static string? ReadAllOrNull(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out var prop)
           && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : fallback;

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
