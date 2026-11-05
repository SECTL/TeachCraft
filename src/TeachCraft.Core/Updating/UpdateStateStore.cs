namespace TeachCraft.Core.Updating;

/// <summary>本机更新状态。只存与本机有关的东西，不上传。</summary>
public sealed record UpdateState(DateTimeOffset? LastCheckUtc, IReadOnlyList<string> SkippedVersions)
{
    public static UpdateState Empty { get; } = new(null, Array.Empty<string>());
}

/// <summary>状态存储的抽象，便于走查路径注入一个什么都不做的实现。</summary>
public interface IUpdateStateStore
{
    UpdateState Load();
    void Save(UpdateState state);
}

/// <summary>走查/自动化用的空实现：读永远为空，写永远丢弃。</summary>
public sealed class NoopUpdateStateStore : IUpdateStateStore
{
    public UpdateState Load() => UpdateState.Empty;
    public void Save(UpdateState state) { }
}

/// <summary>
/// 落在 %LOCALAPPDATA%\TeachCraft\Update\state.json。
///
/// 必须原子写（先写 .tmp 再替换）：进程中途被杀不能留下半截 JSON，
/// 否则下次启动读到损坏文件会以为状态丢了。读取失败一律当空状态，不抛。
/// </summary>
public sealed class UpdateStateStore : IUpdateStateStore
{
    private const string FileName = "state.json";
    private readonly string _directory;

    public UpdateStateStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Update");
    }

    public string Directory => _directory;

    public UpdateState Load()
    {
        try
        {
            var path = Path.Combine(_directory, FileName);
            if (!File.Exists(path)) return UpdateState.Empty;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return UpdateState.Empty;

            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            DateTimeOffset? lastCheck = null;
            if (root.TryGetProperty("LastCheckUtc", out var last) && last.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(last.GetString(), out var parsed))
            {
                lastCheck = parsed;
            }

            var skipped = new List<string>();
            if (root.TryGetProperty("SkippedVersions", out var skippedElement)
                && skippedElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in skippedElement.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) skipped.Add(value);
                }
            }

            return new UpdateState(lastCheck, skipped);
        }
        catch (Exception)
        {
            // 文件损坏、权限不足都不该让更新功能崩掉。
            return UpdateState.Empty;
        }
    }

    public void Save(UpdateState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var path = Path.Combine(_directory, FileName);
            var temp = path + ".tmp";

            var payload = new
            {
                LastCheckUtc = state.LastCheckUtc?.ToString("O"),
                SkippedVersions = state.SkippedVersions,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            });

            File.WriteAllText(temp, json);

            // File.Move 的 overwrite 语义保证要么是旧的完整文件，要么是新的完整文件。
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // 状态存不下来不影响本次检查结果，静默即可。
        }
    }
}
