using System.Globalization;
using MeetRoster.Core.Models;
using SkiaSharp;

namespace MeetRoster.Core.Export;

/// <summary>
/// 卡片版式绘制：把 <see cref="MeetCardDocument"/> 画成高分辨率位图。
/// 版式照搬软件内赛程页「仅显示该班选手」——左侧大号时间、彩色分类徽标、
/// 选手 chip 排成流、右侧人次；每张卡是一张浅色圆角卡，不是表格行。
/// </summary>
public static class MeetCardRenderer
{
    public const float PageWidth = 1000f;
    public const float Pad = 54f;
    public const float ContentWidth = PageWidth - Pad * 2;

    private const float CardPadding = 11f;
    private const float ChipHeight = 21f;
    private const float ChipGap = 5f;
    private const float HeadRow = 30f;
    private const float DetailRow = 16f;

    private static readonly Lazy<SKTypeface> RegularTypeface =
        new(() => SKFontManager.Default.MatchCharacter('李') ?? SKTypeface.Default);

    private static readonly Lazy<SKTypeface> BoldTypeface =
        new(() => SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, '李')
                   ?? RegularTypeface.Value);

    private static readonly SKColor Ink = Hex("#141413");
    private static readonly SKColor InkSoft = Hex("#3D3B36");
    private static readonly SKColor Muted = Hex("#6F6A5E");
    private static readonly SKColor Line = Hex("#E2DDD0");
    private static readonly SKColor LineStrong = Hex("#CFC8B6");
    private static readonly SKColor Accent = Hex("#D97757");
    private static readonly SKColor AccentDeep = Hex("#A34A36");
    private static readonly SKColor Wash = Hex("#FCFAF4");
    private static readonly SKColor CardFill = Hex("#FCFAF4");
    private static readonly SKColor ChipFill = Hex("#FFFFFF");
    private static readonly SKColor Paper = Hex("#FFFFFF");

    // ----------------------------- 分类配色（与界面 MeetRosterPalette 同源） ---------------

    private static SKColor CategoryColor(MeetEventCategory category) => category switch
    {
        MeetEventCategory.Track => Hex("#A34A36"),
        MeetEventCategory.Field => Hex("#5A7A4B"),
        MeetEventCategory.Team => Hex("#8A6A3C"),
        MeetEventCategory.Fun => Hex("#4F6D7A"),
        _ => Hex("#7C7668"),
    };

    private static SKColor CategoryTint(MeetEventCategory category) => category switch
    {
        MeetEventCategory.Track => Hex("#F7EBE7"),
        MeetEventCategory.Field => Hex("#EEF2EA"),
        MeetEventCategory.Team => Hex("#F5F0E8"),
        MeetEventCategory.Fun => Hex("#ECF1F3"),
        _ => Hex("#F1EFEA"),
    };

    // ----------------------------- 量算 -----------------------------

    /// <summary>一张卡的高度：标题行 + 副信息行 + chip 流。</summary>
    public static float CardHeight(MeetCardItem item)
    {
        using var measureFont = new SKFont(RegularTypeface.Value, 10f);
        var chipWidths = item.Entries
            .Select(e => ChipWidth(e, measureFont))
            .ToList();
        var chipRows = LayoutChips(chipWidths, ContentWidth - CardPadding * 2).Count;
        var chipBlock = chipRows * (ChipHeight + ChipGap);

        var height = CardPadding * 2
                     + HeadRow                                     // 时间 + 项目 + 徽标那一行
                     + (item.ShowGroupSummary ? DetailRow : 0f)     // 场地 / 组次
                     + chipBlock;
        return Math.Max(height, CardPadding * 2 + HeadRow + ChipHeight);
    }

    private static float ChipWidth(MeetCardEntry entry, SKFont font)
    {
        var text = ChipText(entry);
        using var chipFont = new SKFont(RegularTypeface.Value, 9.5f);
        return chipFont.MeasureText(text) + 20f;
    }

    private static string ChipText(MeetCardEntry entry)
    {
        var parts = new List<string>();
        // 单班导出时班级名人人相同，chip 里就不重复写了，把宽度留给道次。
        if (entry.ShowClass && entry.ClassName.Length > 0) parts.Add(entry.ClassName);
        parts.Add(entry.AthleteName);
        if (entry.Lane.Length > 0) parts.Add(entry.Lane);
        return string.Join(" · ", parts);
    }

    /// <summary>把 chip 宽度按可用宽度折行，返回每行 chip 的索引。</summary>
    private static List<List<int>> LayoutChips(IReadOnlyList<float> widths, float available)
    {
        var rows = new List<List<int>>();
        var current = new List<int>();
        var used = 0f;

        for (var i = 0; i < widths.Count; i++)
        {
            var width = Math.Min(widths[i], available);
            if (current.Count > 0 && used + ChipGap + width > available)
            {
                rows.Add(current);
                current = [];
                used = 0f;
            }

            if (current.Count > 0) used += ChipGap;
            used += width;
            current.Add(i);
        }

        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    public static float SectionHeight(MeetCardSection section) =>
        34f + section.Periods.Sum(PeriodHeight) + 4f;

    /// <summary>一个上午 / 下午占的高度：小标题 + 各卡片 + 间距。</summary>
    public static float PeriodHeight(MeetCardPeriod period) =>
        26f + period.Items.Sum(item => CardHeight(item) + 8f) + 4f;

    public static float HeaderHeight(MeetCardDocument document) => 118f;

    public static float TotalHeight(MeetCardDocument document) =>
        HeaderHeight(document)
        + document.Sections.Sum(SectionHeight)
        + (document.Footnotes.Count > 0 ? 24f + document.Footnotes.Count * 16f : 0f)
        + 12f;

    // ----------------------------- 绘制 -----------------------------

    /// <summary>
    /// 把整份卡片文档画成一张位图。调用方决定分页：一页放不下时按「比赛日」切开，
    /// 每个 PDF 页各自渲染一段，避免卡片被从中间劈开。
    /// </summary>
    public static byte[] Render(
        MeetCardDocument document,
        IReadOnlyList<MeetCardSection> sections,
        bool includeHeader,
        string footerLeft,
        string footerRight,
        float pageHeight,
        float scale = 1.8f)
    {
        var pixelWidth = (int)Math.Ceiling(PageWidth * scale);
        var pixelHeight = (int)Math.Ceiling(pageHeight * scale);

        using var surface = SKSurface.Create(
            new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            throw new InvalidOperationException("导出画布创建失败，请重试。");

        var canvas = surface.Canvas;
        canvas.Clear(Paper);
        canvas.Scale(scale);

        var y = Pad;
        if (includeHeader)
        {
            y = DrawHeader(canvas, document, y);
        }
        else
        {
            // 续页只画一行小标题，把版面留给卡片。
            using var titleFont = new SKFont(BoldTypeface.Value, 14f);
            using var titlePaint = new SKPaint { Color = InkSoft, IsAntialias = true };
            canvas.DrawText($"{document.Title}（续）", Pad, y + 16f, SKTextAlign.Left, titleFont, titlePaint);
            y += 28f;
        }

        foreach (var section in sections)
            y = DrawSection(canvas, section, y);

        DrawFooter(canvas, pageHeight, footerLeft, footerRight);

        canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static float DrawHeader(SKCanvas canvas, MeetCardDocument document, float y)
    {
        using var accent = new SKPaint { Color = Accent, IsAntialias = true };
        canvas.DrawRect(new SKRect(Pad, y + 8f, Pad + 4f, y + 84f), accent);

        using var titleFont = new SKFont(BoldTypeface.Value, 26f);
        using var titlePaint = new SKPaint { Color = Ink, IsAntialias = true };
        canvas.DrawText(Trim(document.Title, titleFont, ContentWidth - 20f), Pad + 16f, y + 42f,
            SKTextAlign.Left, titleFont, titlePaint);

        using var subFont = new SKFont(RegularTypeface.Value, 12f);
        using var subPaint = new SKPaint { Color = InkSoft, IsAntialias = true };
        canvas.DrawText(Trim(document.Subtitle, subFont, ContentWidth - 20f), Pad + 16f, y + 66f,
            SKTextAlign.Left, subFont, subPaint);

        using var metaFont = new SKFont(RegularTypeface.Value, 9f);
        using var metaPaint = new SKPaint { Color = Muted, IsAntialias = true };
        canvas.DrawText(document.Meta, Pad + 16f, y + 88f, SKTextAlign.Left, metaFont, metaPaint);

        using var linePaint = new SKPaint
        {
            Color = LineStrong, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(Pad, y + 102f, PageWidth - Pad, y + 102f, linePaint);
        return y + HeaderHeight(document);
    }

    private static float DrawSection(SKCanvas canvas, MeetCardSection section, float y)
    {
        // 日期标题：浅底通栏 + 左侧强调点，与界面「第一天 · 11 月 11 日」同款。
        using var bandPaint = new SKPaint { Color = Wash, IsAntialias = true };
        canvas.DrawRect(new SKRect(Pad, y + 4f, Pad + ContentWidth, y + 30f), bandPaint);

        using var dotPaint = new SKPaint { Color = Accent, IsAntialias = true };
        canvas.DrawCircle(Pad + 12f, y + 17f, 3.2f, dotPaint);

        using var dayFont = new SKFont(BoldTypeface.Value, 13f);
        using var dayPaint = new SKPaint { Color = AccentDeep, IsAntialias = true };
        var dayText = $"{section.DayLabel} · {section.DateText}";
        canvas.DrawText(dayText, Pad + 22f, y + 21f, SKTextAlign.Left, dayFont, dayPaint);

        y += 34f;

        foreach (var period in section.Periods)
        {
            y = DrawPeriod(canvas, period, y);
        }

        return y + 4f;
    }

    private static float DrawPeriod(SKCanvas canvas, MeetCardPeriod period, float y)
    {
        using var periodFont = new SKFont(BoldTypeface.Value, 12f);
        using var periodPaint = new SKPaint { Color = Ink, IsAntialias = true };
        canvas.DrawText(period.Period, Pad, y + 14f, SKTextAlign.Left, periodFont, periodPaint);

        using var enFont = new SKFont(RegularTypeface.Value, 8.5f);
        using var enPaint = new SKPaint { Color = Muted, IsAntialias = true };
        var enX = Pad + periodFont.MeasureText(period.Period) + 8f;
        canvas.DrawText(period.English, enX, y + 14f, SKTextAlign.Left, enFont, enPaint);

        var rangeX = enX + enFont.MeasureText(period.English) + 10f;
        canvas.DrawText(period.TimeRange, rangeX, y + 14f, SKTextAlign.Left, enFont, enPaint);

        using var countFont = new SKFont(RegularTypeface.Value, 9f);
        using var countPaint = new SKPaint { Color = Muted, IsAntialias = true };
        canvas.DrawText($"{period.Items.Count} 项", PageWidth - Pad, y + 14f,
            SKTextAlign.Right, countFont, countPaint);

        using var linePaint = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(rangeX + enFont.MeasureText(period.TimeRange) + 10f, y + 10f,
            PageWidth - Pad - countFont.MeasureText($"{period.Items.Count} 项") - 10f, y + 10f, linePaint);

        y += 26f;
        foreach (var item in period.Items)
        {
            y = DrawCard(canvas, item, y);
            y += 8f;
        }

        return y + 4f;
    }

    private static float DrawCard(SKCanvas canvas, MeetCardItem item, float y)
    {
        var height = CardHeight(item);
        var left = Pad;
        var right = Pad + ContentWidth;

        using var cardFill = new SKPaint { Color = CardFill, IsAntialias = true };
        using var cardBorder = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke,
        };
        var rect = new SKRect(left, y, right, y + height);
        canvas.DrawRoundRect(rect, 8f, 8f, cardFill);
        canvas.DrawRoundRect(rect, 8f, 8f, cardBorder);

        var innerLeft = left + CardPadding;
        var innerRight = right - CardPadding;
        var rowTop = y + CardPadding;

        // 左侧大号时间
        using var timeFont = new SKFont(BoldTypeface.Value, 15f);
        using var timePaint = new SKPaint { Color = Ink, IsAntialias = true };
        canvas.DrawText(item.Time, innerLeft, rowTop + 15f, SKTextAlign.Left, timeFont, timePaint);

        var x = innerLeft + 60f;

        // 分类徽标
        var categoryColor = CategoryColor(item.Category);
        using var badgeFill = new SKPaint { Color = CategoryTint(item.Category), IsAntialias = true };
        using var badgeBorder = new SKPaint
        {
            Color = categoryColor, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke,
        };
        using var badgeFont = new SKFont(RegularTypeface.Value, 8.5f);
        using var badgePaint = new SKPaint { Color = categoryColor, IsAntialias = true };
        var badgeWidth = badgeFont.MeasureText(item.CategoryLabel) + 14f;
        var badgeRect = new SKRect(x, rowTop + 1f, x + badgeWidth, rowTop + 19f);
        canvas.DrawRoundRect(badgeRect, 4f, 4f, badgeFill);
        canvas.DrawRoundRect(badgeRect, 4f, 4f, badgeBorder);
        canvas.DrawText(item.CategoryLabel, x + 7f, rowTop + 14f, SKTextAlign.Left, badgeFont, badgePaint);
        x += badgeWidth + 8f;

        // 项目名
        using var eventFont = new SKFont(BoldTypeface.Value, 13.5f);
        using var eventPaint = new SKPaint { Color = Ink, IsAntialias = true };
        canvas.DrawText(item.EventName, x, rowTop + 15f, SKTextAlign.Left, eventFont, eventPaint);
        x += eventFont.MeasureText(item.EventName) + 8f;

        // 年级 / 性别 / 赛次 小 chip
        foreach (var chip in new[]
                 {
                     item.ShowGrade ? item.Grade : null,
                     item.ShowGender ? item.Gender : null,
                     item.ShowStage ? item.Stage : null,
                 })
        {
            if (chip is null) continue;
            using var chipFont = new SKFont(RegularTypeface.Value, 8.5f);
            var chipWidth = chipFont.MeasureText(chip) + 14f;
            if (x + chipWidth > innerRight - 70f) break;

            using var chipFill = new SKPaint { Color = ChipFill, IsAntialias = true };
            using var chipBorder = new SKPaint
            {
                Color = Line, IsAntialias = true, StrokeWidth = 0.9f, Style = SKPaintStyle.Stroke,
            };
            var chipRect = new SKRect(x, rowTop + 1f, x + chipWidth, rowTop + 19f);
            canvas.DrawRoundRect(chipRect, 4f, 4f, chipFill);
            canvas.DrawRoundRect(chipRect, 4f, 4f, chipBorder);

            using var chipPaint = new SKPaint { Color = InkSoft, IsAntialias = true };
            canvas.DrawText(chip, x + 7f, rowTop + 14f, SKTextAlign.Left, chipFont, chipPaint);
            x += chipWidth + 6f;
        }

        // 右侧人次
        using var countFont = new SKFont(RegularTypeface.Value, 9.5f);
        using var countPaint = new SKPaint { Color = InkSoft, IsAntialias = true };
        canvas.DrawText(item.EntryCountLabel, innerRight, rowTop + 14f,
            SKTextAlign.Right, countFont, countPaint);

        var cursorY = rowTop + HeadRow;

        // 副信息：场地 · 组次 / 取几名
        if (item.ShowGroupSummary)
        {
            var detail = $"{item.Venue}　{item.GroupSummary}";
            using var detailFont = new SKFont(RegularTypeface.Value, 9f);
            using var detailPaint = new SKPaint { Color = Muted, IsAntialias = true };
            canvas.DrawText(Trim(detail, detailFont, ContentWidth - CardPadding * 2),
                innerLeft + 60f, cursorY + 2f, SKTextAlign.Left, detailFont, detailPaint);
            cursorY += DetailRow;
        }

        // 选手 chip 流
        using var chipTextFont = new SKFont(RegularTypeface.Value, 9.5f);
        var widths = item.Entries.Select(e => ChipWidth(e, chipTextFont)).ToList();
        var rows = LayoutChips(widths, innerRight - innerLeft);
        var index = 0;
        foreach (var row in rows)
        {
            var chipX = innerLeft;
            foreach (var i in row)
            {
                var entry = item.Entries[i];
                var width = Math.Min(widths[i], innerRight - innerLeft);
                var chipRect = new SKRect(chipX, cursorY, chipX + width, cursorY + ChipHeight);

                using var fill = new SKPaint { Color = ChipFill, IsAntialias = true };
                using var border = new SKPaint
                {
                    Color = Accent, IsAntialias = true, StrokeWidth = 0.9f, Style = SKPaintStyle.Stroke,
                };
                canvas.DrawRoundRect(chipRect, 4f, 4f, fill);
                canvas.DrawRoundRect(chipRect, 4f, 4f, border);

                using var textPaint = new SKPaint { Color = AccentDeep, IsAntialias = true };
                canvas.DrawText(Trim(ChipText(entry), chipTextFont, width - 12f),
                    chipX + 6f, cursorY + 15f, SKTextAlign.Left, chipTextFont, textPaint);

                chipX += width + ChipGap;
                index++;
            }

            cursorY += ChipHeight + ChipGap;
        }

        return y + height;
    }

    private static void DrawFooter(SKCanvas canvas, float pageHeight, string left, string right)
    {
        var footerTop = pageHeight - 46f - Pad;
        using var linePaint = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(Pad, footerTop, PageWidth - Pad, footerTop, linePaint);

        using var font = new SKFont(RegularTypeface.Value, 8.5f);
        using var paint = new SKPaint { Color = Muted, IsAntialias = true };
        canvas.DrawText(left, Pad, footerTop + 20f, SKTextAlign.Left, font, paint);
        canvas.DrawText(right, PageWidth - Pad, footerTop + 20f, SKTextAlign.Right, font, paint);
    }

    // ----------------------------- 文本 -----------------------------

    private static string Trim(string text, SKFont font, float maxWidth)
    {
        text ??= string.Empty;
        if (maxWidth <= 0 || font.MeasureText(text) <= maxWidth) return text;

        const string ellipsis = "…";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + ellipsis;
            if (font.MeasureText(candidate) <= maxWidth) return candidate;
        }

        return ellipsis;
    }

    private static SKColor Hex(string hex)
    {
        var value = uint.Parse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
