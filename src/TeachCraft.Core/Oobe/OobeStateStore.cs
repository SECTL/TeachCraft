namespace TeachCraft.Core.Oobe;

/// <summary>
/// 落在 %LOCALAPPDATA%\TeachCraft\Oobe\state.json。
///
/// 必须原子写（先写 .tmp 再替换）：进程中途被杀不能留下半截 JSON，
/// 否则下次启动读到损坏文件会以为状态丢了。读取失败一律当空状态，不抛。
///
/// 与 UpdateStateStore 同一套范式，但状态**不能**混进 Update\state.json：
/// 那份文件的「删除即重置更新提示」是已知的用户操作，混在一起会让它误伤引导。
/// </summary>
public sealed class OobeStateStore : IOobeStateStore
{
    private const string FileName = "state.json";
    private readonly string _directory;

    public OobeStateStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Oobe");
    }

    public string StateDirectory => _directory;

    public OobeState Load()
    {
        try
        {
            var path = Path.Combine(_directory, FileName);
            if (!File.Exists(path)) return OobeState.Empty;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return OobeState.Empty;

            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            // 缺字段一律按 null 处理，不得因为少一个字段就判整份文件损坏。
            var schemaVersion = OobeState.CurrentSchemaVersion;
            if (root.TryGetProperty("SchemaVersion", out var schemaElement)
                && schemaElement.ValueKind == System.Text.Json.JsonValueKind.Number
                && schemaElement.TryGetInt32(out var parsedSchema))
            {
                schemaVersion = parsedSchema;
            }

            DateTimeOffset? dismissed = null;
            if (root.TryGetProperty("DismissedUtc", out var dismissedElement)
                && dismissedElement.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(dismissedElement.GetString(), out var parsedDismissed))
            {
                dismissed = parsedDismissed;
            }

            OobeDismissal? how = null;
            if (root.TryGetProperty("DismissedHow", out var howElement)
                && howElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                // 只认明确写过的两个值；未知值当作「没记过」，不猜。
                how = howElement.GetString() switch
                {
                    "Completed" => OobeDismissal.Completed,
                    "Skipped" => OobeDismissal.Skipped,
                    _ => null,
                };
            }

            // 旧版文件里的 LastNotifiedVersion 直接忽略：那个功能已删除，
            // 多出的字段不该让读取失败。
            return new OobeState(dismissed, how, schemaVersion);
        }
        catch (Exception)
        {
            // 文件损坏、权限不足都不该让引导把主程序挡住。
            return OobeState.Empty;
        }
    }

    public void Save(OobeState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var path = Path.Combine(_directory, FileName);
            var temp = path + ".tmp";

            var payload = new
            {
                SchemaVersion = OobeState.CurrentSchemaVersion,
                DismissedUtc = state.DismissedUtc?.ToString("O"),
                // 枚举在磁盘上是字符串（不是整数），便于人工排查。
                DismissedHow = state.DismissedHow?.ToString(),
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
            // 状态存不下来不影响本次启动，静默即可。
        }
    }
}
