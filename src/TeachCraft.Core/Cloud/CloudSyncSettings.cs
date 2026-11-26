using System.Text.Json;
using TeachCraft.Shared;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 云端同步偏好。默认全关：不开启同步、不记住文档、所有可选字段都不勾选。
/// </summary>
public sealed record CloudSyncSettings(
    bool Enabled = false,
    int ConsentVersion = 0,
    string? LastDocumentId = null,
    int LastDocumentRevision = 0,
    bool LastRosterHeight = false,
    bool LastRosterVision = false,
    bool LastRosterScore = false,
    bool LastRosterNotes = false,
    bool LastRosterOriginalGroup = false,
    // 归位座位表 / 约束的自动保存。默认关闭：只有用户主动打开开关、且已登录并关联共享名单时，
    // 一次数据改动才触发一次延迟上传。没有改动就没有任务、没有请求，不建立任何常驻连接。
    bool AutoSyncSeatDocument = false)
{
    public static CloudSyncSettings Default { get; } = new();
}

/// <summary>
/// 自动保存座位表的纯判定。所有阻断条件都在这里，测试与宿主侧共用同一份口径；
/// 归位 App 不引用本程序集，按同一组条件就地判定。
/// 注意：这里只判定「该不该发一次请求」，不启动任何定时器或后台轮询。
/// </summary>
public static class CloudAutoSyncPolicy
{
    public static bool ShouldAutoSync(
        CloudSyncSettings settings, bool isSignedIn, bool isExamMode, bool hasLinkedRoster)
        => settings.Enabled
           && settings.AutoSyncSeatDocument
           && isSignedIn
           && !isExamMode
           && hasLinkedRoster;
}

public interface ICloudSyncSettingsStore
{
    CloudSyncSettings Load();
    void Save(CloudSyncSettings settings);
    string Path { get; }

    /// <summary>
    /// 写入偏好并如实报告结果。默认实现走 Save，既有实现与测试假件无需改动即可编译。
    /// 返回 false 时调用方必须给出用户可见的提示，不能让修改静默丢失。
    /// </summary>
    bool TrySave(CloudSyncSettings settings) { Save(settings); return true; }

    /// <summary>最近一次读写失败的本机原因；成功时为 null。只含简短原因，不含用户目录。</summary>
    string? LastError => null;
}

/// <summary>走查/自动化用的空实现。</summary>
public sealed class NoopCloudSyncSettingsStore : ICloudSyncSettingsStore
{
    public CloudSyncSettings Load() => CloudSyncSettings.Default;
    public void Save(CloudSyncSettings settings) { }
    public string Path => "";
}

/// <summary>落在 %LOCALAPPDATA%\TeachCraft\Account\settings.json。</summary>
public sealed class FileCloudSyncSettingsStore : ICloudSyncSettingsStore
{
    private const string FileName = "settings.json";
    private readonly string _path;
    private string? _lastError;

    public FileCloudSyncSettingsStore(string? directory = null)
    {
        var dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachCraft", "Account");
        _path = System.IO.Path.Combine(dir, FileName);
    }

    public string Path => _path;

    public string? LastError => _lastError;

    public CloudSyncSettings Load()
    {
        var text = CloudJsonFile.ReadAllOrNull(_path);
        if (text is null)
        {
            // 文件不存在是正常初始态；存在却读不出来必须如实报告，不能静默回退。
            _lastError = File.Exists(_path) ? SettingsLoadFailedReason : null;
            return CloudSyncSettings.Default;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            _lastError = null;
            return new CloudSyncSettings(
                Enabled: GetBool(root, "Enabled"),
                ConsentVersion: GetInt(root, "ConsentVersion"),
                LastDocumentId: GetString(root, "LastDocumentId"),
                LastDocumentRevision: GetInt(root, "LastDocumentRevision"),
                LastRosterHeight: GetBool(root, "LastRosterHeight"),
                LastRosterVision: GetBool(root, "LastRosterVision"),
                LastRosterScore: GetBool(root, "LastRosterScore"),
                LastRosterNotes: GetBool(root, "LastRosterNotes"),
                LastRosterOriginalGroup: GetBool(root, "LastRosterOriginalGroup"),
                AutoSyncSeatDocument: GetBool(root, "AutoSyncSeatDocument"));
        }
        catch (Exception)
        {
            _lastError = SettingsLoadFailedReason;
            return CloudSyncSettings.Default;
        }
    }

    public void Save(CloudSyncSettings settings)
    {
        try
        {
            CloudJsonFile.WriteAtomic(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 兼容旧调用方：保留原有「不抛出」语义，结果由 TrySave 报告。
        }
    }

    public bool TrySave(CloudSyncSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomicDetectable(_path, json);
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            // 只给中文原因，绝不把用户目录或异常类型名拼进提示。
            _lastError = ErrorText.Describe(ex, "保存本机设置");
            return false;
        }
    }

    public const string SettingsLoadFailedReason = "本机云端设置无法读取，已恢复默认值。";

    // CloudJsonFile.WriteAtomic 内部吞掉所有 IO 异常，无法据此判断成败；这里单独走一条可失败的原子写。
    private static void WriteAtomicDetectable(string path, string json)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static bool GetBool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e)
           && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)
           && e.GetBoolean();

    private static int GetInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
           && e.TryGetInt32(out var v) ? v : 0;

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
