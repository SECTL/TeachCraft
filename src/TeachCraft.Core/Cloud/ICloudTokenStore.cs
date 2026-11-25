using System.Text.Json;

namespace TeachCraft.Core.Cloud;

public interface ICloudTokenStore
{
    CloudTokens? Load();

    /// <summary>凭据必须真正落盘才算成功；保护或写入失败时抛 <see cref="CloudAuthException"/>，调用方据此 fail closed。</summary>
    void Save(CloudTokens tokens);

    void Clear();
    string Path { get; }
}

/// <summary>走查/自动化用的空实现：读永远为空，写与清都丢弃。</summary>
public sealed class NoopCloudTokenStore : ICloudTokenStore
{
    public CloudTokens? Load() => null;
    public void Save(CloudTokens tokens) { }
    public void Clear() { }
    public string Path => "";
}

/// <summary>
/// 落在 %LOCALAPPDATA%\TeachCraft\Account\tokens.json。
/// 信封 {V:1, Protector:"...", Payload:"<保护后的文本>"}：
/// 版本不符或保护方式不匹配一律返回 null（fail closed），绝不拿旧凭据去登录。
/// </summary>
public sealed class FileCloudTokenStore : ICloudTokenStore
{
    private const string FileName = "tokens.json";
    private const int EnvelopeVersion = 1;
    private readonly ITokenProtector _protector;
    private readonly string _path;

    public FileCloudTokenStore(string? directory = null, ITokenProtector? protector = null)
    {
        var dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Account");
        _path = System.IO.Path.Combine(dir, FileName);
        _protector = protector ?? TokenProtectorFactory.Create();
    }

    public string Path => _path;

    public CloudTokens? Load()
    {
        var text = CloudJsonFile.ReadAllOrNull(_path);
        if (text is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (!root.TryGetProperty("V", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var version) || version != EnvelopeVersion)
                return null;

            if (!root.TryGetProperty("Protector", out var p) || p.ValueKind != JsonValueKind.String
                || p.GetString() != _protector.Name)
                return null;

            if (!root.TryGetProperty("Payload", out var payload) || payload.ValueKind != JsonValueKind.String)
                return null;

            var json = _protector.Unprotect(payload.GetString()!);
            using var inner = JsonDocument.Parse(json);
            var t = inner.RootElement;
            if (t.ValueKind != JsonValueKind.Object) return null;

            return new CloudTokens
            {
                AccessToken = GetString(t, "AccessToken") ?? "",
                RefreshToken = GetString(t, "RefreshToken") ?? "",
                TokenType = GetString(t, "TokenType") ?? "Bearer",
                Protector = GetString(t, "Protector") ?? _protector.Name,
                ObtainedAtUtc = GetDate(t, "ObtainedAtUtc") ?? DateTimeOffset.MinValue,
                ExpiresAtUtc = GetDate(t, "ExpiresAtUtc"),
            };
        }
        catch (Exception)
        {
            // 凭据损坏或解密失败一律当作未登录。
            return null;
        }
    }

    public void Save(CloudTokens tokens)
    {
        string envelope;
        try
        {
            var inner = JsonSerializer.Serialize(new
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                TokenType = tokens.TokenType,
                Protector = tokens.Protector,
                ObtainedAtUtc = tokens.ObtainedAtUtc.ToString("O"),
                ExpiresAtUtc = tokens.ExpiresAtUtc?.ToString("O"),
            });

            envelope = JsonSerializer.Serialize(new
            {
                V = EnvelopeVersion,
                Protector = _protector.Name,
                Payload = _protector.Protect(inner),
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            // 保护失败必须让调用方知道，否则会返回一枚没落盘的 token。
            throw new CloudAuthException(CloudErrorText.TokenPersistFailed, "token_persist_failed", ex);
        }

        CloudJsonFile.WriteAtomic(_path, envelope);

        // WriteAtomic 吞掉所有 IO 异常，必须读回确认真的落盘；
        // 否则轮换后的 refresh token 没写进去，进程重启就会用旧 token 而失败。
        var persisted = Load();
        if (persisted is null
            || !string.Equals(persisted.AccessToken, tokens.AccessToken, StringComparison.Ordinal)
            || !string.Equals(persisted.RefreshToken, tokens.RefreshToken, StringComparison.Ordinal))
            throw new CloudAuthException(CloudErrorText.TokenPersistFailed, "token_persist_failed");
    }

    public void Clear() => CloudJsonFile.Delete(_path);

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static DateTimeOffset? GetDate(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(e.GetString(), out var d) ? d : null;
}
