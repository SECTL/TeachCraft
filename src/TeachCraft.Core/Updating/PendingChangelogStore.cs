using System.Text.Json;

namespace TeachCraft.Core.Updating;

/// <summary>
/// 应用内触发更新时写入的一条「待展示更新日志」标记。
///
/// 只有升级成功并重启进入目标版本时，新进程才会消费并弹窗展示一次；
/// 安装取消、替换失败或外部手动覆盖安装都不会命中。
/// </summary>
public sealed record PendingChangelogEntry(
    string FromVersion,
    string TargetVersion,
    string? TagName = null,
    string? FeedNotes = null,
    DateTimeOffset? CreatedUtc = null)
{
    public static PendingChangelogEntry FromProposal(SemanticVersion fromVersion, UpdateProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new PendingChangelogEntry(
            FromVersion: fromVersion.ToString(),
            TargetVersion: proposal.VersionText,
            TagName: proposal.Release.TagName,
            FeedNotes: proposal.Release.ReleaseNotes,
            CreatedUtc: DateTimeOffset.UtcNow);
    }
}

public interface IPendingChangelogStore
{
    PendingChangelogEntry? Load();
    void Save(PendingChangelogEntry entry);
    void Clear();
}

/// <summary>走查与单元测试用的空存储：绝不读写本机真实目录。</summary>
public sealed class NoopPendingChangelogStore : IPendingChangelogStore
{
    public PendingChangelogEntry? Load() => null;
    public void Save(PendingChangelogEntry entry) { }
    public void Clear() { }
}

/// <summary>
/// 落在 %LOCALAPPDATA%\TeachCraft\Update\pending-changelog.json。
/// 刻意不放进 state.json，以免用户删除 state.json 重置「已跳过版本」时误伤待展示日志。
/// </summary>
public sealed class PendingChangelogStore : IPendingChangelogStore
{
    private const string FileName = "pending-changelog.json";
    private readonly string _directory;

    public PendingChangelogStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Update");
    }

    public string Directory => _directory;

    public PendingChangelogEntry? Load()
    {
        try
        {
            var path = Path.Combine(_directory, FileName);
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var from = root.TryGetProperty("FromVersion", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()
                : null;
            var target = root.TryGetProperty("TargetVersion", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(target))
                return null;

            var tag = root.TryGetProperty("TagName", out var tg) && tg.ValueKind == JsonValueKind.String
                ? tg.GetString()
                : null;
            var notes = root.TryGetProperty("FeedNotes", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString()
                : null;
            DateTimeOffset? created = null;
            if (root.TryGetProperty("CreatedUtc", out var cu) && cu.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(cu.GetString(), out var parsedCreated))
            {
                created = parsedCreated;
            }

            return new PendingChangelogEntry(from, target, tag, notes, created);
        }
        catch (Exception)
        {
            // 文件损坏不应阻塞主窗口启动；外层会顺带清理。
            return null;
        }
    }

    public void Save(PendingChangelogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var path = Path.Combine(_directory, FileName);
            var temp = path + ".tmp";

            var payload = new
            {
                entry.FromVersion,
                entry.TargetVersion,
                entry.TagName,
                entry.FeedNotes,
                CreatedUtc = (entry.CreatedUtc ?? DateTimeOffset.UtcNow).ToString("O"),
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // 记录写不下来不能拦住安装流程。
        }
    }

    public void Clear()
    {
        try
        {
            var path = Path.Combine(_directory, FileName);
            if (File.Exists(path)) File.Delete(path);
            var temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception)
        {
            // 清理失败不影响启动。
        }
    }
}
