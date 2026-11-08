namespace TeachCraft.Core.Updating;

/// <summary>本机更新状态。只存与本机有关的东西，不上传。</summary>
/// <param name="LastCheckUtc">上次检查时间。</param>
/// <param name="SkippedVersions">用户点过「跳过」的版本。</param>
/// <param name="PreferredSource">用户手动选定的更新源；null 表示用默认（GitHub + 自动回退）。</param>
public sealed record UpdateState(
    DateTimeOffset? LastCheckUtc,
    IReadOnlyList<string> SkippedVersions,
    UpdateSource? PreferredSource = null)
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

            UpdateSource? preferred = null;
            if (root.TryGetProperty("PreferredSource", out var sourceElement)
                && sourceElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var raw = sourceElement.GetString();
                // 只有明确写过才认；空串或未知值当作「没选过」，回落到默认行为。
                if (!string.IsNullOrWhiteSpace(raw) && raw is "github" or "sectl")
                    preferred = UpdateSourceText.Parse(raw);
            }

            return new UpdateState(lastCheck, skipped, preferred);
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
                // null 表示「没手动选过」，JSON 里写成 null，读取时按默认行为处理。
                PreferredSource = state.PreferredSource is { } source
                    ? UpdateSourceText.ToStorageValue(source)
                    : null,
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
