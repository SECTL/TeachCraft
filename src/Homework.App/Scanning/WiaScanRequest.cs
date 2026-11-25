using Homework.Core.Scanning;

namespace Homework.App.Scanning;

/// <summary>
/// 把 <see cref="ScanSettings"/> 翻译成 WIA 的属性值。这里不碰 COM，纯映射，
/// 方便在没有扫描仪的机器上测试。属性号来自 WIA 2.0（wiadef.h）。
/// </summary>
public sealed record WiaScanRequest(
    int Intent,
    int XResolution,
    int YResolution,
    int DocumentHandling,
    int Pages,
    int PageSize,
    int WidthPixels,
    int HeightPixels)
{
    // WIA_IPS_CUR_INTENT / XRES / YRES / XEXTENT / YEXTENT
    public const int CurIntentProperty = 6146;
    public const int XResolutionProperty = 6147;
    public const int YResolutionProperty = 6148;
    public const int XExtentProperty = 6151;
    public const int YExtentProperty = 6152;

    // WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES / WIA_IPS_DOCUMENT_HANDLING_SELECT / PAGES / PAGE_SIZE
    public const int DocumentHandlingCapabilitiesProperty = 3086;
    public const int DocumentHandlingProperty = 3088;
    public const int PagesProperty = 3096;
    public const int PageSizeProperty = 3097;

    public const int IntentColor = 1;
    public const int IntentGrayscale = 2;
    public const int IntentText = 4;

    public const int Feeder = 1;
    public const int Flatbed = 2;
    public const int Duplex = 4;

    public const int PageSizeCustom = 0;
    public const int PageSizeA4 = 3;
    public const int PageSizeA3 = 5;
    public const int PageSizeB5 = 9;
    public const int PageSizeAuto = 100;

    public static WiaScanRequest Build(ScanSettings settings)
    {
        var normalized = settings.NormalizedForDriver();
        var extent = normalized.PixelExtent;

        return new WiaScanRequest(
            Intent: normalized.ColorMode switch
            {
                ScanColorMode.Color => IntentColor,
                ScanColorMode.Grayscale => IntentGrayscale,
                _ => IntentText,
            },
            XResolution: normalized.ResolutionDpi,
            YResolution: normalized.ResolutionDpi,
            DocumentHandling: normalized.Source switch
            {
                ScanSource.Flatbed => Flatbed,
                ScanSource.FeederDuplex => Feeder | Duplex,
                _ => Feeder,
            },
            Pages: normalized.ScanAllPages ? 0 : 1,
            PageSize: normalized.PageSize switch
            {
                ScanPageSize.A4 => PageSizeA4,
                ScanPageSize.A3 => PageSizeA3,
                ScanPageSize.B5 => PageSizeB5,
                ScanPageSize.Auto => PageSizeAuto,
                _ => PageSizeCustom, // 8K / 16K 没有标准 WIA 枚举，用自定义 + 像素范围
            },
            WidthPixels: extent.Width,
            HeightPixels: extent.Height);
    }
}

/// <summary>部分驱动只接受属性名而不接受数字 ID；这里给出 WIA 规范名。</summary>
public static class WiaPropertyNames
{
    public static string? For(int id) => id switch
    {
        3086 => "WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES",
        3088 => "WIA_IPS_DOCUMENT_HANDLING_SELECT",
        3096 => "WIA_IPS_PAGES",
        3097 => "WIA_IPS_PAGE_SIZE",
        6146 => "WIA_IPS_CUR_INTENT",
        6147 => "WIA_IPS_XRES",
        6148 => "WIA_IPS_YRES",
        6151 => "WIA_IPS_XEXTENT",
        6152 => "WIA_IPS_YEXTENT",
        _ => null,
    };
}
