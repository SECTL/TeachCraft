using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homework.Core.Scanning;

/// <summary>
/// 扫描任务的本机偏好。只存在本机，不随工程走；换电脑不会带走收件夹路径。
/// </summary>
public sealed class ScanTaskPreferences
{
    public string InboxFolder { get; set; } = string.Empty;

    public ScanSource Source { get; set; } = ScanSource.FeederSingle;

    public ScanPageSize PageSize { get; set; } = ScanPageSize.A4;

    public int ResolutionDpi { get; set; } = 300;

    public ScanColorMode ColorMode { get; set; } = ScanColorMode.BlackWhite;

    public ScanPageLimit PageLimit { get; set; } = ScanPageLimit.All;

    public bool ImportToCurrent { get; set; } = true;

    public static string DefaultInboxFolder
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(documents, "TeachCraft", "扫描收件");
        }
    }

    public static ScanTaskPreferences CreateDefault() => new()
    {
        InboxFolder = DefaultInboxFolder,
    };

    public ScanTaskPreferences Normalized()
    {
        var clone = (ScanTaskPreferences)MemberwiseClone();
        if (string.IsNullOrWhiteSpace(clone.InboxFolder))
            clone.InboxFolder = DefaultInboxFolder;
        clone.ResolutionDpi = Math.Clamp(clone.ResolutionDpi, 75, 1200);
        if (clone.Source == ScanSource.Flatbed)
            clone.PageLimit = ScanPageLimit.FirstPage;
        return clone;
    }

    public ScanSettings ToSettings() => new()
    {
        Source = Source,
        PageSize = PageSize,
        ResolutionDpi = ResolutionDpi,
        ColorMode = ColorMode,
        PageLimit = PageLimit,
    };
}

/// <summary>
/// 读写 %LOCALAPPDATA%\TeachCraft\Homework\scan-task.json。文件损坏或缺失时
/// 一律回落到默认值，绝不因为偏好文件坏了让扫描功能打不开。
/// </summary>
public sealed class ScanTaskPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ScanTaskPreferencesStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TeachCraft", "Homework", "scan-task.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public ScanTaskPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return ScanTaskPreferences.CreateDefault();
            var json = File.ReadAllText(FilePath);
            var preferences = JsonSerializer.Deserialize<ScanTaskPreferences>(json, JsonOptions);
            return (preferences ?? ScanTaskPreferences.CreateDefault()).Normalized();
        }
        catch
        {
            return ScanTaskPreferences.CreateDefault();
        }
    }

    public bool TrySave(ScanTaskPreferences preferences)
    {
        try
        {
            var normalized = preferences.Normalized();
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, FilePath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
