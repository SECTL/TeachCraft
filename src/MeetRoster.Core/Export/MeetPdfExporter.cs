using System.Globalization;
using MeetRoster.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace MeetRoster.Core.Export;

/// <summary>
/// 打印用 PDF：把整理好的参赛手册画成高分辨率 PNG 再铺进 A4。
/// 与归位 / 时序 / 织序一致，不改成矢量 PDF——那样会把整套中文字体内嵌进去，体积失控。
/// </summary>
public static class MeetPdfExporter
{
    public const float PageWidth = 1000f;
    /// <summary>固定页高：每页画布尺寸一致，铺进 A4 时缩放比才一致（否则各页字号会忽大忽小）。</summary>
    public const float PageHeight = 1380f;
    private const float Pad = 54f;
    private const float ContentWidth = PageWidth - Pad * 2;
    private const float FooterHeight = 46f;
    private const float ContentHeight = PageHeight - Pad - FooterHeight - 40f;

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
    private static readonly SKColor Highlight = Hex("#FBE4D7");
    private static readonly SKColor Paper = Hex("#FFFFFF");

    public static string Export(MeetProject project, MeetExportOptions options, string path)
    {
        if (project.Classes.Count == 0)
            throw new InvalidOperationException("还没有导入任何班级，无法导出。");

        var summaries = MeetExportBuilder.Build(project, options);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        QuestPDF.Settings.License = LicenseType.Community;

        // 卡片版式：每个比赛日装箱到 A4，卡片不会从中间被劈开；表格版式走原来的分页器。
        var images = options.Layout == MeetPdfLayout.Card
            ? BuildCardPages(project, summaries, options)
            : RenderTablePages(project, summaries, options);

        Document.Create(container =>
        {
            foreach (var image in images)
            {
                var png = image;
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(9, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.Content().Image(png).FitArea();
                });
            }
        }).GeneratePdf(path);

        return Path.GetFullPath(path);
    }

    /// <summary>只渲染前若干页（预览用），不落盘、不生成 PDF。</summary>
    public static List<byte[]> RenderPreviewPages(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries,
        MeetExportOptions options, int pageCount)
    {
        var pages = BuildPages(project, summaries, options).Take(pageCount).ToList();
        return pages
            .Select(page => RenderPage(page, project, summaries, options, pages.Count))
            .ToList();
    }

    // ----------------------------- 卡片版式分页 -----------------------------

    private static List<byte[]> RenderTablePages(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var pages = BuildPages(project, summaries, options);
        return pages
            .Select(page => RenderPage(page, project, summaries, options, pages.Count))
            .ToList();
    }

    /// <summary>卡片版式的 A4 画布高：与表格版式同一常量，铺进 A4 的缩放比才一致。</summary>
    private const float CardPageHeight = PageHeight;

    private static List<byte[]> BuildCardPages(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var document = MeetCardBuilder.Build(project, summaries, options);
        var footerLeft = $"{project.Title} · {document.Scope} · 本机生成，文件未上传";
        var usable = CardPageHeight - Pad - FooterHeight - 40f;

        // 按「时段」装箱而不是整天：一天塞不下时把下午挪到下一页，卡片本身仍不被切断。
        // 日期标题只在当天第一次落位时占一次高度，不能按时段重复计算（否则每页会空掉大半）。
        const float dayHeaderHeight = 34f;

        var pages = new List<List<MeetCardSection>>();
        var current = new List<MeetCardSection>();
        var carried = new List<MeetCardPeriod>();
        var used = MeetCardRenderer.HeaderHeight(document);
        MeetCardSection? currentDay = null;

        void FlushSection()
        {
            if (carried.Count == 0) return;
            var day = currentDay!;
            current.Add(new MeetCardSection
            {
                DayLabel = day.DayLabel,
                DateText = day.DateText,
                Periods = carried.ToList(),
            });
            carried.Clear();
        }

        void FlushPage()
        {
            FlushSection();
            if (current.Count == 0) return;
            pages.Add(current);
            current = [];
            used = 0f;
        }

        foreach (var section in document.Sections)
        {
            if (currentDay is not null && !ReferenceEquals(currentDay, section))
            {
                // 换天：先把上一天没落位完的时段收口。
                FlushSection();
                used += 4f;
            }

            currentDay = section;
            foreach (var period in section.Periods)
            {
                var periodHeight = MeetCardRenderer.PeriodHeight(period);
                var need = periodHeight + (carried.Count == 0 ? dayHeaderHeight : 0f);

                if (used + need > usable && current.Count + carried.Count > 0)
                {
                    FlushPage();
                    need = periodHeight + dayHeaderHeight;
                }

                carried.Add(period);
                used += need;
            }
        }

        FlushPage();
        if (pages.Count == 0) pages.Add([]);

        return pages
            .Select((sections, index) => MeetCardRenderer.Render(
                document,
                sections,
                includeHeader: index == 0,
                footerLeft,
                $"共 {pages.Count} 页 · 第 {index + 1} 页",
                CardPageHeight))
            .ToList();
    }

    // ----------------------------- 分页 -----------------------------

    private abstract class PageItem
    {
        public abstract float Height(SKFont font);
        public abstract void Draw(SKCanvas canvas, float y, SKFont font);

        /// <summary>按可用高度把条目切开；不能切开的条目原样返回（整体挪到下一页）。</summary>
        public virtual IReadOnlyList<PageItem> Split(float available, SKFont font)
        {
            return Height(font) <= available ? [this] : [];
        }
    }

    private sealed class SpacerItem(float height) : PageItem
    {
        public override float Height(SKFont font) => height;
        public override void Draw(SKCanvas canvas, float y, SKFont font) { }
    }

    private sealed class SectionItem(string text, string english) : PageItem
    {
        public override float Height(SKFont font) => 46f;

        public override void Draw(SKCanvas canvas, float y, SKFont font)
        {
            using var titleFont = new SKFont(BoldTypeface.Value, 17f);
            using var titlePaint = new SKPaint { Color = Ink, IsAntialias = true };
            canvas.DrawText(text, Pad, y + 22f, SKTextAlign.Left, titleFont, titlePaint);

            using var enFont = new SKFont(RegularTypeface.Value, 9f);
            using var enPaint = new SKPaint { Color = Muted, IsAntialias = true };
            canvas.DrawText(english, PageWidth - Pad, y + 22f, SKTextAlign.Right, enFont, enPaint);

            using var linePaint = new SKPaint
            {
                Color = LineStrong, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke,
            };
            canvas.DrawLine(Pad, y + 34f, PageWidth - Pad, y + 34f, linePaint);
        }
    }

    private sealed class ParagraphItem(string text, float size, SKColor color, bool bold = false) : PageItem
    {
        private List<string>? _lines;

        private List<string> Lines(SKFont font)
        {
            if (_lines is not null) return _lines;
            using var measureFont = new SKFont(bold ? BoldTypeface.Value : RegularTypeface.Value, size);
            _lines = Wrap(text, measureFont, ContentWidth);
            return _lines;
        }

        public override float Height(SKFont font) => Lines(font).Count * (size + 6f) + 4f;

        public override void Draw(SKCanvas canvas, float y, SKFont font)
        {
            using var textFont = new SKFont(bold ? BoldTypeface.Value : RegularTypeface.Value, size);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            var lines = Lines(font);
            for (var i = 0; i < lines.Count; i++)
                canvas.DrawText(lines[i], Pad, y + (size + 6f) * (i + 1) - 4f, SKTextAlign.Left, textFont, paint);
        }
    }

    private sealed class StatRowItem(IReadOnlyList<(string Label, string Value)> stats) : PageItem
    {
        public override float Height(SKFont font) => 62f;

        public override void Draw(SKCanvas canvas, float y, SKFont font)
        {
            if (stats.Count == 0) return;
            var gap = 10f;
            var width = (ContentWidth - gap * (stats.Count - 1)) / stats.Count;
            using var fillPaint = new SKPaint { Color = Wash, IsAntialias = true };
            using var borderPaint = new SKPaint
            {
                Color = Line, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke,
            };

            for (var i = 0; i < stats.Count; i++)
            {
                var x = Pad + i * (width + gap);
                var rect = new SKRect(x, y + 4f, x + width, y + 58f);
                canvas.DrawRoundRect(rect, 6f, 6f, fillPaint);
                canvas.DrawRoundRect(rect, 6f, 6f, borderPaint);

                using var valueFont = new SKFont(BoldTypeface.Value, 20f);
                using var valuePaint = new SKPaint { Color = Ink, IsAntialias = true };
                canvas.DrawText(stats[i].Value, x + 12f, y + 34f, SKTextAlign.Left, valueFont, valuePaint);

                using var labelFont = new SKFont(RegularTypeface.Value, 9.5f);
                using var labelPaint = new SKPaint { Color = Muted, IsAntialias = true };
                canvas.DrawText(stats[i].Label, x + 12f, y + 48f, SKTextAlign.Left, labelFont, labelPaint);
            }
        }
    }

    private sealed class TableItem : PageItem
    {
        public TableItem(IReadOnlyList<float> widths, IReadOnlyList<string> headers,
            IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<bool>? highlights = null,
            string? title = null)
        {
            Widths = widths;
            Headers = headers;
            Rows = rows;
            Highlights = highlights;
            Title = title;
        }

        public IReadOnlyList<float> Widths { get; }
        public IReadOnlyList<string> Headers { get; }
        public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
        public IReadOnlyList<bool>? Highlights { get; }
        /// <summary>表格上方的小标题（如「七年级1班 20 人…」）；分页时随表头一起重复。</summary>
        public string? Title { get; }
        public const float TitleHeight = 30f;
        public const float HeaderHeight = 26f;
        public const float RowHeight = 23f;

        private float Top => Title is null ? HeaderHeight : TitleHeight + HeaderHeight;

        public override float Height(SKFont font) => Top + Rows.Count * RowHeight + 6f;

        /// <summary>
        /// 长表格按行切开：每一段都重画小标题与表头，续页才看得懂这是哪个班的名单。
        /// 旧实现整表不可切，超出一页的行被直接画到页外（真实秩序册 60 个班时丢了一半名单）。
        /// 切段时尽量把「某个班的报名块」留在同一页，避免表头与首行分家。
        /// </summary>
        public override IReadOnlyList<PageItem> Split(float available, SKFont font)
        {
            if (Height(font) <= available) return [this];

            var capacity = (int)Math.Floor((available - Top - 6f) / RowHeight);
            if (capacity <= 0) return [];

            // 切点留 2 行余量：页脚与最后一行之间要留白，否则最后一行会被页脚压住。
            var take = Math.Max(1, capacity - 2);
            // 从切点往回找最近的一个「块边界」（空行），找不到才硬切。
            for (var offset = 0; offset < Math.Min(capacity, Rows.Count); offset++)
            {
                var index = capacity - offset;
                if (index > 0 && index < Rows.Count && Rows[index].All(string.IsNullOrWhiteSpace))
                {
                    take = index + 1;
                    break;
                }
            }

            var head = new TableItem(Widths, Headers,
                Rows.Take(take).ToList(),
                Highlights?.Take(take).ToList(), Title);

            var tailRows = Rows.Skip(take).ToList();
            if (tailRows.Count == 0) return [head];

            // 续页标题标「续」：否则同一个班的人数会出现两次，读者会以为名单重复。
            var tailTitle = Title is null ? null : Title + "（续）";
            var tail = new TableItem(Widths, Headers, tailRows,
                Highlights?.Skip(take).ToList(), tailTitle);
            return [head, tail];
        }

        public override void Draw(SKCanvas canvas, float y, SKFont font)
        {
            var total = Widths.Sum();
            var scale = ContentWidth / total;
            using var headerFill = new SKPaint { Color = Wash, IsAntialias = true };
            using var highlightFill = new SKPaint { Color = Highlight, IsAntialias = true };
            using var headerFont = new SKFont(BoldTypeface.Value, 9.5f);
            using var headerPaint = new SKPaint { Color = InkSoft, IsAntialias = true };
            using var cellFont = new SKFont(RegularTypeface.Value, 9.5f);
            using var cellPaint = new SKPaint { Color = Ink, IsAntialias = true };
            using var linePaint = new SKPaint
            {
                Color = Line, IsAntialias = true, StrokeWidth = 0.7f, Style = SKPaintStyle.Stroke,
            };

            if (Title is not null)
            {
                using var titleFont = new SKFont(BoldTypeface.Value, 11f);
                using var titlePaint = new SKPaint { Color = InkSoft, IsAntialias = true };
                canvas.DrawText(Trim(Title, titleFont, ContentWidth), Pad, y + 15f,
                    SKTextAlign.Left, titleFont, titlePaint);
                y += TitleHeight;
            }

            canvas.DrawRect(new SKRect(Pad, y, Pad + ContentWidth, y + HeaderHeight), headerFill);

            var x = Pad;
            for (var i = 0; i < Headers.Count && i < Widths.Count; i++)
            {
                var width = Widths[i] * scale;
                canvas.DrawText(Trim(Headers[i], headerFont, width - 8f), x + 4f, y + 17f,
                    SKTextAlign.Left, headerFont, headerPaint);
                x += width;
            }

            canvas.DrawLine(Pad, y + HeaderHeight, Pad + ContentWidth, y + HeaderHeight, linePaint);

            for (var row = 0; row < Rows.Count; row++)
            {
                var rowTop = y + HeaderHeight + row * RowHeight;
                if (Highlights is not null && row < Highlights.Count && Highlights[row])
                    canvas.DrawRect(new SKRect(Pad, rowTop, Pad + ContentWidth, rowTop + RowHeight), highlightFill);

                x = Pad;
                for (var i = 0; i < Rows[row].Count && i < Widths.Count; i++)
                {
                    var width = Widths[i] * scale;
                    canvas.DrawText(Trim(Rows[row][i], cellFont, width - 8f), x + 4f, rowTop + 15.5f,
                        SKTextAlign.Left, cellFont, cellPaint);
                    x += width;
                }

                canvas.DrawLine(Pad, rowTop + RowHeight, Pad + ContentWidth, rowTop + RowHeight, linePaint);
            }
        }
    }

    private sealed class BannerItem(string title, string subtitle, string meta) : PageItem
    {
        public override float Height(SKFont font) => 118f;

        public override void Draw(SKCanvas canvas, float y, SKFont font)
        {
            using var accent = new SKPaint { Color = Accent, IsAntialias = true };
            canvas.DrawRect(new SKRect(Pad, y + 8f, Pad + 4f, y + 84f), accent);

            using var titleFont = new SKFont(BoldTypeface.Value, 26f);
            using var titlePaint = new SKPaint { Color = Ink, IsAntialias = true };
            canvas.DrawText(Trim(title, titleFont, ContentWidth - 20f), Pad + 16f, y + 42f,
                SKTextAlign.Left, titleFont, titlePaint);

            using var subFont = new SKFont(RegularTypeface.Value, 12f);
            using var subPaint = new SKPaint { Color = InkSoft, IsAntialias = true };
            canvas.DrawText(Trim(subtitle, subFont, ContentWidth - 20f), Pad + 16f, y + 66f,
                SKTextAlign.Left, subFont, subPaint);

            using var metaFont = new SKFont(RegularTypeface.Value, 9f);
            using var metaPaint = new SKPaint { Color = Muted, IsAntialias = true };
            canvas.DrawText(meta, Pad + 16f, y + 88f, SKTextAlign.Left, metaFont, metaPaint);

            using var linePaint = new SKPaint
            {
                Color = LineStrong, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke,
            };
            canvas.DrawLine(Pad, y + 102f, PageWidth - Pad, y + 102f, linePaint);
        }
    }

    private sealed class Page
    {
        public List<PageItem> Items { get; } = [];
        public float Height { get; set; }
    }

    private static List<Page> BuildPages(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var items = new List<PageItem>
        {
            new BannerItem(
                options.IsAllClasses ? project.Title : $"{summaries[0].Title} · 参赛手册",
                options.IsAllClasses ? "全部班级 · 秩序册整理" : "运动会秩序册整理",
                $"{summaries.Count} 个班 · {summaries.Sum(s => s.AthleteCount)} 名运动员 · "
                + $"{project.Days.Count} 个比赛日 · 本机生成"),
            new SpacerItem(6f),
        };

        if (options.IncludeClassSummary)
        {
            items.Add(new SectionItem("参赛概览", "OVERVIEW"));
            items.Add(new StatRowItem(
            [
                ("参赛班级", summaries.Count.ToString("00", CultureInfo.InvariantCulture)),
                ("运动员", summaries.Sum(s => s.AthleteCount).ToString(CultureInfo.InvariantCulture)),
                ("男子 / 女子",
                    $"{summaries.Sum(s => s.MaleCount)} / {summaries.Sum(s => s.FemaleCount)}"),
                ("报名项次", summaries.Sum(s => s.EntryCount).ToString(CultureInfo.InvariantCulture)),
            ]));
            items.Add(new SpacerItem(10f));
        }

        if (options.IncludeRoster)
        {
            items.Add(new SectionItem("报名名单", "ROSTER"));
            foreach (var summary in summaries)
            {
                var rows = summary.Athletes
                    .Select(a => (IReadOnlyList<string>)
                    [
                        a.Bib,
                        a.Name,
                        a.Gender,
                        Join(a.IndividualEvent1, a.IndividualEvent2),
                    ])
                    .ToList();
                // 末尾补一个空行：既是班级之间的留白，也是分页时的切块点。
                rows.Add(["", "", "", ""]);

                // 团体项目记在班级上（秩序册不点名队员），所以不再占一列，放进班级标题里。
                items.Add(new TableItem([58f, 80f, 46f, 190f],
                    ["号码", "姓名", "性别", "比赛项目"], rows,
                    title: $"{summary.Title}　{summary.AthleteCount} 人（男 {summary.MaleCount} · 女 {summary.FemaleCount}）"
                           + $"　单项 {summary.IndividualCount} 项次"
                           + (summary.Class.TeamEvents.Count > 0
                               ? $"　团体：{string.Join("、", summary.Class.TeamEvents)}"
                               : string.Empty)));
                items.Add(new SpacerItem(12f));
            }
        }

        if (options.IncludeEvents)
        {
            items.Add(new SectionItem("单项报名", "EVENTS"));
            var rows = new List<IReadOnlyList<string>>();
            var highlights = new List<bool>();
            foreach (var summary in summaries)
            {
                foreach (var (eventName, gender, names) in summary.EventRoster())
                {
                    rows.Add([eventName, gender, string.Join("、", names), summary.Title]);
                    highlights.Add(!options.IsAllClasses);
                }
            }

            if (rows.Count == 0) rows.Add(["（没有单项报名）", "", "", ""]);
            items.Add(new TableItem([120f, 50f, 260f, 120f],
                ["项目", "组别", "参赛人", "班级"], rows, highlights));
            items.Add(new SpacerItem(12f));
        }

        if (options.IncludeSchedule && project.Days.Count > 0)
        {
            items.Add(new SectionItem("参赛日程", "SCHEDULE"));
            var lines = summaries
                .SelectMany(s => s.Entries)
                .OrderBy(l => DayIndex(project, l.DayName))
                .ThenBy(l => l.Period == "上午" ? 0 : 1)
                .ThenBy(l => l.Time, StringComparer.Ordinal)
                .ToList();

            if (lines.Count == 0)
            {
                items.Add(new ParagraphItem(
                    options.IsAllClasses ? "还没有赛程数据。" : "这个班在赛程里没有参赛记录。",
                    10.5f, Muted));
            }
            else
            {
                string? lastKey = null;
                var rows = new List<IReadOnlyList<string>>();
                var highlights = new List<bool>();
                foreach (var line in lines)
                {
                    var key = line.DayName + "|" + line.DateText + "|" + line.Period;
                    if (!string.Equals(lastKey, key, StringComparison.Ordinal))
                    {
                        rows.Add([
                            $"{line.DayName} · {line.DateText} · {line.Period}",
                            string.Empty, string.Empty, string.Empty, string.Empty,
                        ]);
                        highlights.Add(false);
                        lastKey = key;
                    }

                    rows.Add([
                        line.Time, line.EventName, line.Stage, line.AthleteLabel, line.Lane,
                    ]);
                    highlights.Add(options.HighlightClass && !options.IsAllClasses);
                }

                items.Add(new TableItem([52f, 110f, 54f, 86f, 150f],
                    ["时间", "项目", "赛次", "参赛人", "组次 / 道次"], rows, highlights));
            }
        }

        return Paginate(items);
    }

    private static List<Page> Paginate(List<PageItem> items)
    {
        using var measureFont = new SKFont(RegularTypeface.Value, 10f);
        var pages = new List<Page>();
        var current = new Page();

        foreach (var item in items)
        {
            var pending = new Queue<PageItem>();
            pending.Enqueue(item);

            while (pending.Count > 0)
            {
                var next = pending.Dequeue();
                var height = next.Height(measureFont);

                if (current.Height + height <= ContentHeight)
                {
                    current.Items.Add(next);
                    current.Height += height;
                    continue;
                }

                // 放不下：先按剩余高度尝试切开，切出来的后半段回到队首继续排。
                var available = ContentHeight - current.Height;
                var parts = next.Split(available, measureFont);
                if (parts.Count >= 2)
                {
                    current.Items.Add(parts[0]);
                    current.Height += parts[0].Height(measureFont);
                    for (var i = parts.Count - 1; i >= 1; i--) pending.Enqueue(parts[i]);
                    continue;
                }

                // 整条挪到下一页；当前页已有内容时才翻页，避免空页死循环。
                if (current.Items.Count > 0)
                {
                    pages.Add(current);
                    current = new Page();
                    pending.Enqueue(next);
                    continue;
                }

                // 新页也放不下且切不开：直接放，宁可变高也不丢内容。
                current.Items.Add(next);
                current.Height += height;
            }
        }

        if (current.Items.Count > 0) pages.Add(current);
        if (pages.Count == 0) pages.Add(new Page());
        return pages;
    }

    private static int DayIndex(MeetProject project, string dayName)
    {
        for (var i = 0; i < project.Days.Count; i++)
        {
            if (string.Equals(project.Days[i].Name, dayName, StringComparison.Ordinal)) return i;
        }

        return int.MaxValue;
    }

    // ----------------------------- 绘制 -----------------------------

    private static byte[] RenderPage(
        Page page, MeetProject project, IReadOnlyList<ClassExportSummary> summaries,
        MeetExportOptions options, int totalPages)
    {
        // 1.8 倍：A4 宽 1000pt 画布 → 约 1800px（≈ 230 DPI），打印足够清晰；
        // 再往上（2.6 倍）60 个班的秩序册会到 29MB，老师发微信都发不出去。
        const float scale = 1.8f;
        var pixelWidth = (int)Math.Ceiling(PageWidth * scale);
        var pixelHeight = (int)Math.Ceiling(PageHeight * scale);

        using var surface = SKSurface.Create(
            new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            throw new InvalidOperationException("导出画布创建失败，请重试。");

        var canvas = surface.Canvas;
        canvas.Clear(Paper);
        canvas.Scale(scale);

        using var measureFont = new SKFont(RegularTypeface.Value, 10f);
        var y = Pad;
        foreach (var item in page.Items)
        {
            item.Draw(canvas, y, measureFont);
            y += item.Height(measureFont);
        }

        DrawFooter(canvas, PageHeight, project, options, totalPages);

        canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawFooter(
        SKCanvas canvas, float height, MeetProject project, MeetExportOptions options, int totalPages)
    {
        var footerTop = height - FooterHeight - Pad;
        using var linePaint = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke,
        };
        canvas.DrawLine(Pad, footerTop, PageWidth - Pad, footerTop, linePaint);

        using var font = new SKFont(RegularTypeface.Value, 8.5f);
        using var paint = new SKPaint { Color = Muted, IsAntialias = true };
        var scope = options.IsAllClasses ? "全部班级" : options.ClassName ?? string.Empty;
        canvas.DrawText($"{project.Title} · {scope} · 本机生成，文件未上传",
            Pad, footerTop + 20f, SKTextAlign.Left, font, paint);
        canvas.DrawText($"共 {totalPages} 页", PageWidth - Pad, footerTop + 20f,
            SKTextAlign.Right, font, paint);
    }

    // ----------------------------- 文本 -----------------------------

    private static string Join(params string[] values) =>
        string.Join("、", values.Where(v => !string.IsNullOrWhiteSpace(v)));

    private static string Trim(string text, SKFont font, float maxWidth)
    {
        text ??= string.Empty;
        if (maxWidth <= 0 || font.MeasureText(text) <= maxWidth) return text;

        var ellipsis = "…";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + ellipsis;
            if (font.MeasureText(candidate) <= maxWidth) return candidate;
        }

        return ellipsis;
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return [string.Empty];

        var current = string.Empty;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                lines.Add(current);
                current = string.Empty;
                continue;
            }

            var candidate = current + ch;
            if (font.MeasureText(candidate) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines.Count == 0 ? [string.Empty] : lines;
    }

    private static SKColor Hex(string hex)
    {
        var value = uint.Parse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
