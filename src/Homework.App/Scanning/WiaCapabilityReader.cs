using Homework.Core.Scanning;

namespace Homework.App.Scanning;

/// <summary>
/// 把 WIA 属性快照翻译成界面可用的能力集。纯映射，不碰 COM，便于在没有扫描仪的机器上测试。
/// </summary>
public static class WiaCapabilityReader
{
    private static readonly int[] KnownResolutions = [75, 100, 150, 200, 300, 400, 600, 1200];
    private static readonly int[] KnownIntents = [1, 2, 4];
    private static readonly int[] KnownPageSizes = [3, 5, 9, 100];

    public static ScannerCapabilities Read(WiaPropertySnapshot snapshot)
    {
        var sourceInfo = snapshot.Get(WiaScanRequest.DocumentHandlingCapabilitiesProperty)
                         ?? snapshot.Get(WiaScanRequest.DocumentHandlingProperty);
        var intentInfo = snapshot.Get(WiaScanRequest.CurIntentProperty);
        var resolutionInfo = snapshot.Get(WiaScanRequest.XResolutionProperty)
                             ?? snapshot.Get(WiaScanRequest.YResolutionProperty);
        var pageSizeInfo = snapshot.Get(WiaScanRequest.PageSizeProperty);
        var pagesInfo = snapshot.Get(WiaScanRequest.PagesProperty);

        var sources = ReadSources(sourceInfo);
        var colors = ReadColors(intentInfo);
        var resolutions = ReadResolutions(resolutionInfo);
        var pageSizes = ReadPageSizes(pageSizeInfo);
        var maxPages = ReadMaxPages(pagesInfo);
        var reported = sources.Count > 0 || colors.Count > 0
                       || resolutions.Count > 0 || pageSizes.Count > 0;

        return new ScannerCapabilities(
            sources.Count > 0 ? sources : [ScanSource.FeederSingle],
            pageSizes.Count > 0 ? pageSizes : [ScanPageSize.A4],
            resolutions.Count > 0 ? resolutions : [300],
            colors.Count > 0 ? colors : [ScanColorMode.BlackWhite],
            maxPages,
            reported);
    }

    private static List<ScanSource> ReadSources(WiaPropertyInfo? info)
    {
        var result = new List<ScanSource>();
        if (info is null) return result;

        foreach (var value in EffectiveValues(info, [1, 2, 4]))
        {
            if ((value & 1) != 0) Add(result, ScanSource.FeederSingle);
            if ((value & 2) != 0) Add(result, ScanSource.Flatbed);
            if ((value & 4) != 0) Add(result, ScanSource.FeederDuplex);
        }

        return result;
    }

    private static List<ScanColorMode> ReadColors(WiaPropertyInfo? info)
    {
        var result = new List<ScanColorMode>();
        if (info is null) return result;

        foreach (var value in EffectiveValues(info, KnownIntents))
        {
            var mode = value switch
            {
                1 => ScanColorMode.Color,
                2 => ScanColorMode.Grayscale,
                4 => ScanColorMode.BlackWhite,
                _ => (ScanColorMode?)null,
            };
            if (mode is { } modeValue) Add(result, modeValue);
        }

        return result;
    }

    private static List<int> ReadResolutions(WiaPropertyInfo? info)
    {
        if (info is null) return [];

        if (info.Kind is WiaPropertyKind.List or WiaPropertyKind.Flag)
            return EffectiveValues(info, KnownResolutions)
                .Where(value => value is >= 75 and <= 1200)
                .Distinct()
                .OrderBy(value => value)
                .ToList();

        if (info.Kind == WiaPropertyKind.Range)
        {
            var values = KnownResolutions
                .Where(value => value >= info.Min && value <= info.Max)
                .Where(value => info.Step <= 0 || (value - info.Min) % info.Step == 0)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            if (values.Count > 0) return values;

            var fallback = new List<int> { info.Min, info.Max };
            if (info.Min <= 300 && info.Max >= 300) fallback.Add(300);
            return fallback
                .Where(value => value is >= 75 and <= 1200)
                .Distinct()
                .OrderBy(value => value)
                .ToList();
        }

        if (info.SingleValue is { } single && single is >= 75 and <= 1200)
            return [single];

        return [];
    }

    private static List<ScanPageSize> ReadPageSizes(WiaPropertyInfo? info)
    {
        var result = new List<ScanPageSize>();
        if (info is null) return result;

        foreach (var value in EffectiveValues(info, KnownPageSizes))
        {
            var size = value switch
            {
                3 => ScanPageSize.A4,
                5 => ScanPageSize.A3,
                9 => ScanPageSize.B5,
                100 => ScanPageSize.Auto,
                _ => (ScanPageSize?)null,
            };
            if (size is { } pageSize) Add(result, pageSize);
        }

        return result;
    }

    private static int ReadMaxPages(WiaPropertyInfo? info)
    {
        if (info is null) return 100;
        var max = info.Kind switch
        {
            WiaPropertyKind.Range => info.Max,
            WiaPropertyKind.List or WiaPropertyKind.Flag => info.Values.DefaultIfEmpty(info.SingleValue ?? 0).Max(),
            _ => info.SingleValue ?? 0,
        };
        return max > 0 ? max : 100;
    }

    private static IEnumerable<int> EffectiveValues(WiaPropertyInfo info, IReadOnlyList<int> known)
    {
        if (info.Values.Count > 0) return info.Values;
        if (info.SingleValue is { } single) return [single];
        if (info.Kind == WiaPropertyKind.Range)
            return known.Where(value => value >= info.Min && value <= info.Max);
        return [];
    }

    private static void Add<T>(List<T> list, T value)
    {
        if (!list.Contains(value)) list.Add(value);
    }
}
