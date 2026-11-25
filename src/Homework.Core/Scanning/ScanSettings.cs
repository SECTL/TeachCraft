namespace Homework.Core.Scanning;

/// <summary>扫描来源。平板只出单页；输稿器可以连续进纸，双面会一次扫两面。</summary>
public enum ScanSource
{
    Flatbed,
    FeederSingle,
    FeederDuplex,
}

/// <summary>原稿尺寸。8K / 16K 是国内试卷与作业常用的开本，WIA 没有标准枚举，按毫米换算成像素范围。</summary>
public enum ScanPageSize
{
    A4,
    A3,
    B5,
    EightK,
    SixteenK,
    Auto,
}

/// <summary>色彩模式。填涂识别只需要黑白，照片类作业可以选灰度或彩色。</summary>
public enum ScanColorMode
{
    BlackWhite,
    Grayscale,
    Color,
}

/// <summary>扫描页数。输稿器可选全部或只扫第一页；平板固定单页。</summary>
public enum ScanPageLimit
{
    All,
    FirstPage,
}

/// <summary>
/// 一次扫描任务的设置。只描述「要扫成什么」，不包含任何 WIA / 平台细节，
/// 这样 UI、WIA 映射和测试可以共用同一份数据。
/// </summary>
public sealed record ScanSettings
{
    public ScanSource Source { get; init; } = ScanSource.FeederSingle;

    public ScanPageSize PageSize { get; init; } = ScanPageSize.A4;

    public int ResolutionDpi { get; init; } = 300;

    public ScanColorMode ColorMode { get; init; } = ScanColorMode.BlackWhite;

    public ScanPageLimit PageLimit { get; init; } = ScanPageLimit.All;

    public static ScanSettings Default { get; } = new();

    public bool IsFeeder => Source != ScanSource.Flatbed;

    public bool IsDuplex => Source == ScanSource.FeederDuplex;

    /// <summary>只有输稿器才可能连续扫多页；平板永远只出第一页。</summary>
    public bool ScanAllPages => IsFeeder && PageLimit == ScanPageLimit.All;

    /// <summary>原稿的物理尺寸（毫米）。自动尺寸按 A4 处理，驱动会按实际纸张裁切。</summary>
    public (double WidthMm, double HeightMm) PaperSizeMm => PageSize switch
    {
        ScanPageSize.A4 => (210, 297),
        ScanPageSize.A3 => (297, 420),
        ScanPageSize.B5 => (176, 250),
        ScanPageSize.EightK => (270, 390),
        ScanPageSize.SixteenK => (195, 270),
        _ => (210, 297),
    };

    /// <summary>按当前分辨率换算出的像素范围。WIA 的 extent 以像素为单位。</summary>
    public (int Width, int Height) PixelExtent
    {
        get
        {
            var (widthMm, heightMm) = PaperSizeMm;
            var dpi = NormalizeResolution(ResolutionDpi);
            return (
                (int)Math.Round(widthMm / 25.4 * dpi),
                (int)Math.Round(heightMm / 25.4 * dpi));
        }
    }

    /// <summary>只允许 200 / 300 / 600 三档，避免 UI 传进驱动不支持的奇怪数值。</summary>
    public static int NormalizeResolution(int dpi) => dpi switch
    {
        <= 200 => 200,
        <= 300 => 300,
        _ => 600,
    };

    public ScanSettings Normalized() => this with
    {
        ResolutionDpi = NormalizeResolution(ResolutionDpi),
        PageLimit = IsFeeder ? PageLimit : ScanPageLimit.FirstPage,
    };

    /// <summary>驱动已经报告过能力时使用：保留驱动支持的 dpi，只做合理范围保护。</summary>
    public ScanSettings NormalizedForDriver() => this with
    {
        ResolutionDpi = Math.Clamp(ResolutionDpi, 75, 1200),
        PageLimit = IsFeeder ? PageLimit : ScanPageLimit.FirstPage,
    };
}

/// <summary>界面与状态栏共用的中文短标签，避免 UI 与 Core 各写一份。</summary>
public static class ScanSettingsText
{
    public static string SourceLabel(ScanSource source) => source switch
    {
        ScanSource.Flatbed => "稿台玻璃",
        ScanSource.FeederSingle => "输稿器（单面）",
        ScanSource.FeederDuplex => "输稿器（双面）",
        _ => "输稿器（单面）",
    };

    public static string PageSizeLabel(ScanPageSize size) => size switch
    {
        ScanPageSize.A4 => "A4",
        ScanPageSize.A3 => "A3",
        ScanPageSize.B5 => "B5",
        ScanPageSize.EightK => "8K",
        ScanPageSize.SixteenK => "16K",
        ScanPageSize.Auto => "自动",
        _ => "A4",
    };

    public static string ColorModeLabel(ScanColorMode mode) => mode switch
    {
        ScanColorMode.BlackWhite => "黑白",
        ScanColorMode.Grayscale => "灰度",
        ScanColorMode.Color => "彩色",
        _ => "黑白",
    };

    public static string PageLimitLabel(ScanPageLimit limit) => limit switch
    {
        ScanPageLimit.All => "全部页面",
        ScanPageLimit.FirstPage => "仅第 1 页",
        _ => "全部页面",
    };

    public static string Summary(ScanSettings settings)
    {
        var normalized = settings.Normalized();
        return $"{SourceLabel(normalized.Source)} · {PageSizeLabel(normalized.PageSize)} · "
               + $"{normalized.ResolutionDpi} dpi · {ColorModeLabel(normalized.ColorMode)} · "
               + PageLimitLabel(normalized.PageLimit);
    }
}
