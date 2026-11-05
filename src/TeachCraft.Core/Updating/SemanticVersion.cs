namespace TeachCraft.Core.Updating;

/// <summary>
/// 语义版本：主.次.修 + 可选 -preview.N。
/// 预览版必须以 prerelease 段表达（1.17.0-preview.142），不能用 build metadata（1.17.0+preview.142），
/// 否则 SemVer 会把它当成与 1.17.0 相等，更新检查会永远认为「已是最新」。
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private const string PreviewMarker = "-preview.";

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>预览序号；正式版为 null。</summary>
    public int? Preview { get; }

    public bool IsPreview => Preview is not null;

    private SemanticVersion(int major, int minor, int patch, int? preview)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Preview = preview;
    }

    /// <summary>构造一个版本；preview 为 null 表示正式版。</summary>
    internal static SemanticVersion Create(int major, int minor, int patch, int? preview)
        => new(major, minor, patch, preview);

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();

        int? preview = null;
        var markerIndex = span.IndexOf(PreviewMarker.AsSpan(), StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var previewSpan = span[(markerIndex + PreviewMarker.Length)..];
            if (previewSpan.IsEmpty) return false;
            foreach (var c in previewSpan)
            {
                if (!char.IsAsciiDigit(c)) return false;
            }
            if (!int.TryParse(previewSpan, out var previewValue)) return false;
            preview = previewValue;
            span = span[..markerIndex];
        }
        else if (span.IndexOf('-') >= 0)
        {
            // 除了 -preview.N 以外的 prerelease 段暂不支持，宁可判为不识别也不要猜。
            return false;
        }

        var parts = span.ToString().Split('.');
        if (parts.Length != 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0) return false;
            foreach (var c in parts[i])
            {
                if (!char.IsAsciiDigit(c)) return false;
            }
            if (!int.TryParse(parts[i], out numbers[i])) return false;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], preview);
        return true;
    }

    public static SemanticVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"无法解析的版本号：{text}");

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        // 同号时：有预览段的小于无预览段的（1.17.0-preview.9 < 1.17.0）。
        return (Preview, other.Preview) switch
        {
            (null, null) => 0,
            (not null, null) => -1,
            (null, not null) => 1,
            (var a, var b) => a!.Value.CompareTo(b!.Value),
        };
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Preview);

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        return Preview is null ? core : $"{core}{PreviewMarker}{Preview.Value}";
    }

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
}
