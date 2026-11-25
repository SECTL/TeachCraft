namespace Homework.Core.Scanning;

/// <summary>
/// 扫描仪实际报告的能力。界面只展示这里列出的选项；没报告的类别用保守默认值。
/// </summary>
public sealed record ScannerCapabilities(
    IReadOnlyList<ScanSource> Sources,
    IReadOnlyList<ScanPageSize> PageSizes,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<ScanColorMode> ColorModes,
    int MaxPages,
    bool IsReported)
{
    /// <summary>诊断用：驱动实际报告的属性原文，用于排查能力读取问题。</summary>
    public string RawDump { get; init; } = string.Empty;

    public static ScannerCapabilities Unknown { get; } = new(
        [ScanSource.FeederSingle],
        [ScanPageSize.A4],
        [300],
        [ScanColorMode.BlackWhite],
        100,
        false);

    public static ScannerCapabilities All { get; } = new(
        [ScanSource.Flatbed, ScanSource.FeederSingle, ScanSource.FeederDuplex],
        [ScanPageSize.A4, ScanPageSize.A3, ScanPageSize.B5, ScanPageSize.EightK, ScanPageSize.SixteenK, ScanPageSize.Auto],
        [200, 300, 600],
        [ScanColorMode.BlackWhite, ScanColorMode.Grayscale, ScanColorMode.Color],
        100,
        true);

    public string Summary()
    {
        var sources = string.Join(" / ", Sources.Select(ScanSettingsText.SourceLabel));
        var pages = string.Join(" / ", PageSizes.Select(ScanSettingsText.PageSizeLabel));
        var resolutions = $"{string.Join(" / ", Resolutions.OrderBy(value => value))} dpi";
        var colors = string.Join(" / ", ColorModes.Select(ScanSettingsText.ColorModeLabel));
        return $"{sources} · {pages} · {resolutions} · {colors}";
    }
}
