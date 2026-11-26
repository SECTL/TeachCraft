using System.Text.Json;
using System.Text.Json.Nodes;
using TeachCraft.Core.Cloud;

namespace TeachCraft.Core.Control;

/// <summary>
/// 集控设备长期凭证。敏感字段（DeviceSecret）落盘时经过 ITokenProtector（Windows DPAPI）保护；
/// ToString() 仅输出脱敏后的设备与地点摘要，绝不泄露 secret。
/// </summary>
public sealed class ControlCredential
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceSecret { get; init; } = string.Empty;
    public string Platform { get; init; } = SectlControlProtocol.PlatformName;
    public string Host { get; init; } = SectlPlatformDefaults.WebBaseUrl;
    public string SiteId { get; init; } = string.Empty;
    public string SitePublicId { get; init; } = string.Empty;
    public string SiteName { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public double SavedAt { get; init; }

    public string SiteLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(SiteName) && !string.IsNullOrEmpty(SitePublicId))
                return $"{SiteName}（{SitePublicId}）";
            if (!string.IsNullOrEmpty(SitePublicId)) return SitePublicId;
            return !string.IsNullOrEmpty(SiteName) ? SiteName : SiteId;
        }
    }

    public JsonObject ToJson() => new()
    {
        ["device_id"] = DeviceId,
        ["device_secret"] = DeviceSecret,
        ["platform"] = Platform,
        ["host"] = Host,
        ["site_id"] = SiteId,
        ["site_public_id"] = SitePublicId,
        ["site_name"] = SiteName,
        ["device_name"] = DeviceName,
        ["saved_at"] = SavedAt > 0 ? SavedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
    };

    public static ControlCredential FromJson(JsonObject obj) => new()
    {
        DeviceId = JsonHelp.Str(obj, "device_id"),
        DeviceSecret = JsonHelp.Str(obj, "device_secret"),
        Platform = JsonHelp.Str(obj, "platform") is { Length: > 0 } p ? p : SectlControlProtocol.PlatformName,
        Host = ControlSettings.NormalizeHost(JsonHelp.Str(obj, "host")),
        SiteId = JsonHelp.Str(obj, "site_id"),
        SitePublicId = JsonHelp.Str(obj, "site_public_id"),
        SiteName = JsonHelp.Str(obj, "site_name"),
        DeviceName = JsonHelp.Str(obj, "device_name"),
        SavedAt = JsonHelp.Num(obj, "saved_at"),
    };

    public override string ToString()
        => $"ControlCredential(DeviceId={DeviceId}, Platform={Platform}, Site={SiteLabel}, Secret=[REDACTED])";
}

public interface IControlCredentialStore
{
    string Path { get; }
    ControlCredential? Load();
    void Save(ControlCredential credential);
    void Clear();
}

/// <summary>走查与无状态测试用的空凭证存储：仅驻留内存，不碰本机磁盘。</summary>
public sealed class NoopControlCredentialStore : IControlCredentialStore
{
    private ControlCredential? _memory;
    public string Path => string.Empty;
    public ControlCredential? Load() => _memory;
    public void Save(ControlCredential credential) => _memory = credential;
    public void Clear() => _memory = null;
}

/// <summary>
/// 凭证存储，默认写至 %LOCALAPPDATA%\TeachCraft\Control\credential.json。
/// 必须与 SECTL 参考脚本目录隔离，避免误删或互相覆盖。
/// </summary>
public sealed class FileControlCredentialStore : IControlCredentialStore
{
    private const int EnvelopeVersion = 1;
    private readonly ITokenProtector _protector;

    public FileControlCredentialStore(string? path = null, ITokenProtector? protector = null)
    {
        Path = !string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.Combine(FileControlSettingsStore.GetDefaultDirectory(), "credential.json");
        _protector = protector ?? TokenProtectorFactory.Create();
    }

    public string Path { get; }

    public ControlCredential? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var text = File.ReadAllText(Path);
            if (JsonNode.Parse(text) is not JsonObject obj) return null;

            // 保护信封格式：{ v: 1, protector: "dpapi"|"plain"|"none", payload: "<protected json>" }
            if (obj["protector"] is JsonValue && obj["payload"] is JsonValue)
            {
                var version = JsonHelp.Int(obj, "v");
                var protectorName = JsonHelp.Str(obj, "protector");
                var payload = JsonHelp.Str(obj, "payload");
                if (version != EnvelopeVersion || protectorName != _protector.Name || string.IsNullOrEmpty(payload))
                    return null;

                var plaintext = _protector.Unprotect(payload);
                if (JsonNode.Parse(plaintext) is not JsonObject inner) return null;
                var protectedCred = ControlCredential.FromJson(inner);
                return ValidOrNull(protectedCred);
            }

            // 兼容直写凭证格式（仅在测试显式指定或读取未加密旧文件时出现）
            return ValidOrNull(ControlCredential.FromJson(obj));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(ControlCredential credential)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var plaintext = credential.ToJson().ToJsonString();
        var envelope = new JsonObject
        {
            ["v"] = EnvelopeVersion,
            ["protector"] = _protector.Name,
            ["payload"] = _protector.Protect(plaintext),
        };

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (Exception)
        {
            // 删除失败不影响状态机降级为 Unconfigured。
        }
    }

    private static ControlCredential? ValidOrNull(ControlCredential credential)
        => string.IsNullOrEmpty(credential.DeviceId) || string.IsNullOrEmpty(credential.DeviceSecret)
            ? null
            : credential;
}

/// <summary>期望状态版本号 + 最近 512 条指令幂等缓存（v4 §3.3 / §3.4）。</summary>
public interface IControlStateStore
{
    long AppliedRevision { get; }
    void SetAppliedRevision(long revision);
    bool TryGetCommand(string commandId, out bool ok, out JsonObject payload, out string code);
    void RememberCommand(string commandId, bool ok, JsonObject payload, string code = "");
    void Reset();
}

/// <summary>走查用的内存状态缓存：不读写 state.json。</summary>
public sealed class NoopControlStateStore : IControlStateStore
{
    private readonly object _lock = new();
    private readonly List<CachedCommand> _commands = new();

    public long AppliedRevision { get; private set; }

    public void SetAppliedRevision(long revision)
    {
        lock (_lock)
        {
            if (revision > AppliedRevision) AppliedRevision = revision;
        }
    }

    public bool TryGetCommand(string commandId, out bool ok, out JsonObject payload, out string code)
    {
        lock (_lock)
        {
            var found = _commands.FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.Ordinal));
            if (found is null)
            {
                ok = false;
                payload = new JsonObject();
                code = string.Empty;
                return false;
            }

            ok = found.Ok;
            payload = JsonHelp.CloneObject(found.Payload);
            code = found.Code;
            return true;
        }
    }

    public void RememberCommand(string commandId, bool ok, JsonObject payload, string code = "")
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        lock (_lock)
        {
            _commands.RemoveAll(c => string.Equals(c.Id, commandId, StringComparison.Ordinal));
            _commands.Add(new CachedCommand(commandId, ok, JsonHelp.CloneObject(payload), code));
            while (_commands.Count > SectlControlProtocol.CommandCacheSize)
                _commands.RemoveAt(0);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            AppliedRevision = 0;
            _commands.Clear();
        }
    }

    private sealed record CachedCommand(string Id, bool Ok, JsonObject Payload, string Code);
}

/// <summary>落在 %LOCALAPPDATA%\TeachCraft\Control\state.json 的幂等与期望状态存储。</summary>
public sealed class FileControlStateStore : IControlStateStore
{
    private readonly object _lock = new();
    private readonly List<CachedCommand> _commands = new();
    private long _appliedRevision;

    public FileControlStateStore(string? path = null)
    {
        Path = !string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.Combine(FileControlSettingsStore.GetDefaultDirectory(), "state.json");
        Load();
    }

    public string Path { get; }

    public long AppliedRevision
    {
        get { lock (_lock) return _appliedRevision; }
    }

    public void SetAppliedRevision(long revision)
    {
        lock (_lock)
        {
            if (revision <= _appliedRevision) return;
            _appliedRevision = revision;
            Save();
        }
    }

    public bool TryGetCommand(string commandId, out bool ok, out JsonObject payload, out string code)
    {
        lock (_lock)
        {
            var entry = _commands.FirstOrDefault(e => string.Equals(e.Id, commandId, StringComparison.Ordinal));
            if (entry is null)
            {
                ok = false;
                payload = new JsonObject();
                code = string.Empty;
                return false;
            }

            ok = entry.Ok;
            payload = JsonHelp.CloneObject(entry.Payload);
            code = entry.Code;
            return true;
        }
    }

    public void RememberCommand(string commandId, bool ok, JsonObject payload, string code = "")
    {
        if (string.IsNullOrEmpty(commandId)) return;
        lock (_lock)
        {
            _commands.RemoveAll(e => string.Equals(e.Id, commandId, StringComparison.Ordinal));
            _commands.Add(new CachedCommand(commandId, ok, JsonHelp.CloneObject(payload), code));
            while (_commands.Count > SectlControlProtocol.CommandCacheSize)
                _commands.RemoveAt(0);
            Save();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _appliedRevision = 0;
            _commands.Clear();
            try
            {
                if (File.Exists(Path)) File.Delete(Path);
            }
            catch (Exception)
            {
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(Path)) return;
            var text = File.ReadAllText(Path);
            if (JsonNode.Parse(text) is not JsonObject obj) return;
            _appliedRevision = JsonHelp.Long(obj, "applied_state_revision");
            if (obj["commands"] is not JsonArray arr) return;
            foreach (var item in arr.OfType<JsonObject>())
            {
                var id = JsonHelp.Str(item, "id");
                if (id.Length == 0) continue;
                _commands.Add(new CachedCommand(
                    id,
                    JsonHelp.Bool(item, "ok"),
                    JsonHelp.AsObject(item["payload"]),
                    JsonHelp.Str(item, "code")));
            }
        }
        catch (Exception)
        {
            _appliedRevision = 0;
            _commands.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var arr = new JsonArray();
            foreach (var entry in _commands)
            {
                arr.Add(new JsonObject
                {
                    ["id"] = entry.Id,
                    ["ok"] = entry.Ok,
                    ["code"] = entry.Code,
                    ["payload"] = JsonHelp.CloneObject(entry.Payload),
                });
            }

            var root = new JsonObject
            {
                ["applied_state_revision"] = _appliedRevision,
                ["commands"] = arr,
            };

            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    private sealed record CachedCommand(string Id, bool Ok, JsonObject Payload, string Code);
}
