using System.Text.Json;

namespace TeachCraft.Core.Cloud;

public sealed record CloudAccount(
    string UserId,
    string DisplayName,
    string[] Scopes,
    string ClientId,
    DateTimeOffset SignedInAtUtc);

public interface ICloudAccountStore
{
    CloudAccount? Load();
    void Save(CloudAccount account);
    void Clear();
    string Path { get; }
}

/// <summary>走查/自动化用的空实现。</summary>
public sealed class NoopCloudAccountStore : ICloudAccountStore
{
    public CloudAccount? Load() => null;
    public void Save(CloudAccount account) { }
    public void Clear() { }
    public string Path => "";
}

/// <summary>落在 %LOCALAPPDATA%\TeachCraft\Account\account.json。</summary>
public sealed class FileCloudAccountStore : ICloudAccountStore
{
    private const string FileName = "account.json";
    private readonly string _path;

    public FileCloudAccountStore(string? directory = null)
    {
        var dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Account");
        _path = System.IO.Path.Combine(dir, FileName);
    }

    public string Path => _path;

    public CloudAccount? Load()
    {
        var text = CloudJsonFile.ReadAllOrNull(_path);
        if (text is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var userId = GetString(root, "UserId");
            if (string.IsNullOrWhiteSpace(userId)) return null;

            var scopes = new List<string>();
            if (root.TryGetProperty("Scopes", out var s) && s.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in s.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) scopes.Add(value);
                }
            }

            return new CloudAccount(
                userId,
                GetString(root, "DisplayName") ?? "已登录",
                scopes.ToArray(),
                GetString(root, "ClientId") ?? "",
                GetDate(root, "SignedInAtUtc") ?? DateTimeOffset.MinValue);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(CloudAccount account)
    {
        try
        {
            var payload = new
            {
                UserId = account.UserId,
                DisplayName = account.DisplayName,
                Scopes = account.Scopes,
                ClientId = account.ClientId,
                SignedInAtUtc = account.SignedInAtUtc.ToString("O"),
            };
            CloudJsonFile.WriteAtomic(_path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 状态存不下来不影响本次操作。
        }
    }

    public void Clear() => CloudJsonFile.Delete(_path);

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static DateTimeOffset? GetDate(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(e.GetString(), out var d) ? d : null;
}
