using MeetRoster.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MeetRoster.Core.Export;

/// <summary>预览结果：一张 PNG（PDF 首页缩略图或 Excel 网格示意）+ 一行说明。</summary>
public sealed class MeetExportPreview
{
    public required byte[] Png { get; init; }
    public required string Caption { get; init; }
    public required string Detail { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// 导出预览：把「将要导出的东西」画成一张缩略图，用户在导出设置里先看一眼再决定。
/// 不复用完整导出管线——预览只画首页，且不落盘，避免为了预览生成整份 PDF / xlsx。
/// </summary>
public static class MeetExportPreviewRenderer
{
    /// <summary>预览缩略图的横向像素；按屏幕显示宽度取，够看清版式即可。</summary>
    public const int ThumbnailWidth = 760;

    public static MeetExportPreview Render(
        MeetProject project, MeetExportOptions options, MeetExportFormatKind format)
    {
        if (project.Classes.Count == 0)
        {
            return new MeetExportPreview
            {
                Png = BlankPng(ThumbnailWidth, 900, "还没有导入任何班级"),
                Caption = "无法预览",
                Detail = "先导入秩序册或报名表，再回来导出。",
                Width = ThumbnailWidth,
                Height = 900,
            };
        }

        var summaries = MeetExportBuilder.Build(project, options);
        if (format == MeetExportFormatKind.Pdf)
            return RenderPdfPreview(project, summaries, options);

        return options.ExcelStyle == MeetExcelStyle.OriginalHighlight
            ? RenderOriginalHighlight(project, options)
            : RenderExcelPreview(project, summaries, options);
    }

    // ----------------------------- 原表高亮预览 -----------------------------

    /// <summary>
    /// 原表高亮的预览：画一段「代表队名单」的示意网格，目标班整行浅橙底，
    /// 其余行白底——让老师一眼看出这是原表的版面、只多了底色。
    /// </summary>
    private static MeetExportPreview RenderOriginalHighlight(
        MeetProject project, MeetExportOptions options)
    {
        var className = options.ClassName;
        var rows = new List<(string[] Cells, bool Highlight)>();
        var summary = project.Classes
            .FirstOrDefault(c => string.Equals(c.Name, className, StringComparison.Ordinal))
            ?? project.Classes.FirstOrDefault();

        if (summary is not null)
        {
            foreach (var athlete in summary.Athletes.Take(6))
            {
                rows.Add(([
                    summary.Name,
                    athlete.Bib,
                    athlete.Name,
                    athlete.Gender,
                    string.Join("、", new[] { athlete.IndividualEvent1, athlete.IndividualEvent2 }
                        .Where(v => !string.IsNullOrWhiteSpace(v))),
                ], true));
            }
        }

        // 再补两行别的班，说明「只有该班被涂色」。
        var other = project.Classes
            .FirstOrDefault(c => !string.Equals(c.Name, className, StringComparison.Ordinal));
        if (other is not null)
        {
            foreach (var athlete in other.Athletes.Take(2))
            {
                rows.Add(([
                    other.Name,
                    athlete.Bib,
                    athlete.Name,
                    athlete.Gender,
                    string.Join("、", new[] { athlete.IndividualEvent1, athlete.IndividualEvent2 }
                        .Where(v => !string.IsNullOrWhiteSpace(v))),
                ], false));
            }
        }

        var table = (Headers: new[] { "班级", "号码", "姓名", "性别", "比赛项目" }, Rows: rows);
        var png = DrawGridWithHighlights(table);
        var height = 56 + 26 + rows.Count * 26 + 26;

        return new MeetExportPreview
        {
            Png = png,
            Caption = className is null
                ? "Excel · 原表高亮（未指定班级，不着色）"
                : $"Excel · 原表高亮 · {className}",
            Detail = "复制导入的原工作簿，只在「代表队名单 / 项目分组表」里给该班条目涂底色；"
                     + "版面、列宽、合并单元格与下拉校验原样保留，原文件不会被改动。",
            Width = ThumbnailWidth,
            Height = height,
        };
    }

    private static byte[] DrawGridWithHighlights(
        (string[] Headers, List<(string[] Cells, bool Highlight)> Rows) table)
    {
        var height = 56 + 26 + table.Rows.Count * 26 + 26;
        using var bitmap = new SkiaSharp.SKBitmap(ThumbnailWidth, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        using var regular = new SkiaSharp.SKFont(
            SkiaSharp.SKFontManager.Default.MatchCharacter('李') ?? SkiaSharp.SKTypeface.Default, 11f);
        using var headerFont = new SkiaSharp.SKFont(
            SkiaSharp.SKFontManager.Default.MatchCharacter(null, SkiaSharp.SKFontStyle.Bold, null, '李')
            ?? SkiaSharp.SKTypeface.Default, 11f);

        var ink = new SkiaSharp.SKColor(0x14, 0x14, 0x13);
        var muted = new SkiaSharp.SKColor(0x6F, 0x6A, 0x5E);
        var wash = new SkiaSharp.SKColor(0xF4, 0xF2, 0xEC);
        var highlight = new SkiaSharp.SKColor(0xFB, 0xE4, 0xD7);
        var line = new SkiaSharp.SKColor(0xD9, 0xD3, 0xC4);

        const float left = 28f;
        const float top = 56f;
        var width = ThumbnailWidth - left * 2;
        var columns = table.Headers.Length;
        var columnWidth = width / columns;

        using var washPaint = new SkiaSharp.SKPaint { Color = wash, IsAntialias = true };
        using var highlightPaint = new SkiaSharp.SKPaint { Color = highlight, IsAntialias = true };
        using var linePaint = new SkiaSharp.SKPaint
        {
            Color = line, IsAntialias = true, StrokeWidth = 0.8f, Style = SkiaSharp.SKPaintStyle.Stroke,
        };
        using var inkPaint = new SkiaSharp.SKPaint { Color = ink, IsAntialias = true };
        using var mutedPaint = new SkiaSharp.SKPaint { Color = muted, IsAntialias = true };

        canvas.DrawText("原表 · 预览", left, 30f, SkiaSharp.SKTextAlign.Left, headerFont, inkPaint);

        canvas.DrawRect(new SkiaSharp.SKRect(left, top, left + width, top + 26f), washPaint);
        for (var c = 0; c < columns; c++)
        {
            canvas.DrawText(table.Headers[c], left + c * columnWidth + 8f, top + 17f,
                SkiaSharp.SKTextAlign.Left, headerFont, inkPaint);
        }

        canvas.DrawLine(left, top + 26f, left + width, top + 26f, linePaint);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var rowTop = top + 26f + r * 26f;
            var (cells, isHighlight) = table.Rows[r];
            if (isHighlight)
                canvas.DrawRect(new SkiaSharp.SKRect(left, rowTop, left + width, rowTop + 26f), highlightPaint);

            for (var c = 0; c < columns && c < cells.Length; c++)
            {
                canvas.DrawText(cells[c], left + c * columnWidth + 8f, rowTop + 17f,
                    SkiaSharp.SKTextAlign.Left, regular, inkPaint);
            }

            canvas.DrawLine(left, rowTop + 26f, left + width, rowTop + 26f, linePaint);
        }

        canvas.DrawText("浅橙底 = 该班条目；其余为原表内容", left,
            top + 26f + table.Rows.Count * 26f + 20f,
            SkiaSharp.SKTextAlign.Left, regular, mutedPaint);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ----------------------------- PDF 预览 -----------------------------

    private static MeetExportPreview RenderPdfPreview(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        if (options.Layout == MeetPdfLayout.Card)
        {
            var document = MeetCardBuilder.Build(project, summaries, options);
            var scope = document.Scope;

            // 首页只放能放下的部分：按比赛日装箱，与真实导出的分页规则一致。
            var usable = MeetCardRenderer.PageWidth > 0
                ? MeetPdfExporter.PageHeight - 54f - 46f - 40f
                : 1240f;
            var page = new List<MeetCardSection>();
            var used = MeetCardRenderer.HeaderHeight(document);
            foreach (var section in document.Sections)
            {
                var height = MeetCardRenderer.SectionHeight(section);
                if (page.Count > 0 && used + height > usable) break;
                page.Add(section);
                used += height;
            }

            var png = MeetCardRenderer.Render(
                document, page, includeHeader: true,
                $"{project.Title} · {scope} · 本机生成，文件未上传",
                "预览 · 第 1 页",
                MeetPdfExporter.PageHeight,
                scale: ThumbnailWidth / MeetCardRenderer.PageWidth);

            return new MeetExportPreview
            {
                Png = png,
                Caption = $"赛程卡 · {document.Sections.Count} 天 · {summaries.Sum(s => s.EntryCount)} 条参赛记录",
                Detail = "每个时段一张卡，左侧时间、彩色分类徽标、选手标签；一天放不下就另起一页。",
                Width = ThumbnailWidth,
                Height = (int)Math.Ceiling(MeetPdfExporter.PageHeight * (ThumbnailWidth / MeetCardRenderer.PageWidth)),
            };
        }

        var pages = MeetPdfExporter.RenderPreviewPages(project, summaries, options, pageCount: 1);
        return new MeetExportPreview
        {
            Png = pages[0],
            Caption = $"参赛手册 · {summaries.Count} 个班 · {summaries.Sum(s => s.AthleteCount)} 名运动员",
            Detail = "纸墨表格：参赛概览 → 报名名单 → 单项报名 → 参赛日程，长表格跨页自动重复表头。",
            Width = ThumbnailWidth,
            Height = (int)Math.Ceiling(MeetPdfExporter.PageHeight * (ThumbnailWidth / MeetPdfExporter.PageWidth)),
        };
    }

    // ----------------------------- Excel 预览 -----------------------------

    private static MeetExportPreview RenderExcelPreview(
        MeetProject project, IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        var sheets = new List<string>();
        if (options.IncludeClassSummary) sheets.Add("班级汇总");
        if (options.IncludeRoster) sheets.Add("班级报名");
        if (options.IncludeEvents) sheets.Add("单项名单");
        if (options.IncludeSchedule) sheets.Add("赛程");

        var table = BuildPreviewRows(summaries, options);
        var png = DrawGrid(table, options.HighlightClass && !options.IsAllClasses);
        var height = 56 + 26 + table.Rows.Count * 26 + (options.HighlightClass && !options.IsAllClasses ? 26 : 10);

        return new MeetExportPreview
        {
            Png = png,
            Caption = $"Excel · {sheets.Count} 页：{string.Join(" / ", sheets)}",
            Detail = "工作表结构与导出一致；预览是前几行的样式示意，完整内容以导出文件为准。",
            Width = ThumbnailWidth,
            Height = height,
        };
    }

    /// <summary>Excel 预览的数据行：单班给名单前几行，全部班级给各班汇总前几行。</summary>
    private static (string[] Headers, List<string[]> Rows) BuildPreviewRows(
        IReadOnlyList<ClassExportSummary> summaries, MeetExportOptions options)
    {
        if (options.IsAllClasses)
        {
            var rows = summaries
                .Take(7)
                .Select(s => new[]
                {
                    s.Title,
                    s.MaleCount.ToString(),
                    s.FemaleCount.ToString(),
                    s.AthleteCount.ToString(),
                    s.IndividualCount.ToString(),
                    s.TeamCount.ToString(),
                })
                .ToList();
            return (["班级", "男子", "女子", "合计", "单项", "团体项目"], rows);
        }

        var single = summaries[0];
        var athletes = single.Athletes
            .Take(8)
            .Select((a, index) => new[]
            {
                (index + 1).ToString(),
                a.Bib,
                a.Name,
                a.Gender,
                string.Join("、", new[] { a.IndividualEvent1, a.IndividualEvent2 }
                    .Where(v => !string.IsNullOrWhiteSpace(v))),
            })
            .ToList();
        return (["序号", "号码", "姓名", "性别", "比赛项目"], athletes);
    }

    private static byte[] DrawGrid(
        (string[] Headers, List<string[]> Rows) table, bool highlight)
    {
        // 画布高按内容算：行数 × 行高 + 标题 + 表头 + 底部说明，不留大片空白。
        var height = 56 + 26 + table.Rows.Count * 26 + (highlight ? 26 : 10);
        using var bitmap = new SkiaSharp.SKBitmap(ThumbnailWidth, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        using var regular = new SkiaSharp.SKFont(
            SkiaSharp.SKFontManager.Default.MatchCharacter('李') ?? SkiaSharp.SKTypeface.Default, 11f);
        using var headerFont = new SkiaSharp.SKFont(
            SkiaSharp.SKFontManager.Default.MatchCharacter(null, SkiaSharp.SKFontStyle.Bold, null, '李')
            ?? SkiaSharp.SKTypeface.Default, 11f);

        var ink = new SkiaSharp.SKColor(0x14, 0x14, 0x13);
        var muted = new SkiaSharp.SKColor(0x6F, 0x6A, 0x5E);
        var wash = new SkiaSharp.SKColor(0xF4, 0xF2, 0xEC);
        var highlightFill = new SkiaSharp.SKColor(0xFB, 0xE4, 0xD7);
        var line = new SkiaSharp.SKColor(0xD9, 0xD3, 0xC4);

        const float left = 28f;
        const float top = 56f;
        var width = ThumbnailWidth - left * 2;
        var columns = table.Headers.Length;
        var columnWidth = width / columns;

        using var washPaint = new SkiaSharp.SKPaint { Color = wash, IsAntialias = true };
        using var highlightPaint = new SkiaSharp.SKPaint { Color = highlightFill, IsAntialias = true };
        using var linePaint = new SkiaSharp.SKPaint
        {
            Color = line, IsAntialias = true, StrokeWidth = 0.8f, Style = SkiaSharp.SKPaintStyle.Stroke,
        };
        using var inkPaint = new SkiaSharp.SKPaint { Color = ink, IsAntialias = true };
        using var mutedPaint = new SkiaSharp.SKPaint { Color = muted, IsAntialias = true };

        // 标题：像 Excel 里的第一行合并标题
        canvas.DrawText("参赛手册 · 预览", left, 30f, SkiaSharp.SKTextAlign.Left, headerFont, inkPaint);

        canvas.DrawRect(new SkiaSharp.SKRect(left, top, left + width, top + 26f), washPaint);
        for (var c = 0; c < columns; c++)
        {
            canvas.DrawText(table.Headers[c], left + c * columnWidth + 8f, top + 17f,
                SkiaSharp.SKTextAlign.Left, headerFont, inkPaint);
        }

        canvas.DrawLine(left, top + 26f, left + width, top + 26f, linePaint);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var rowTop = top + 26f + r * 26f;
            if (highlight) canvas.DrawRect(new SkiaSharp.SKRect(left, rowTop, left + width, rowTop + 26f), highlightPaint);

            var row = table.Rows[r];
            for (var c = 0; c < columns && c < row.Length; c++)
            {
                canvas.DrawText(row[c], left + c * columnWidth + 8f, rowTop + 17f,
                    SkiaSharp.SKTextAlign.Left, regular, inkPaint);
            }

            canvas.DrawLine(left, rowTop + 26f, left + width, rowTop + 26f, linePaint);
        }

        if (highlight)
        {
            canvas.DrawText("底色：该班条目高亮", left, top + 26f + table.Rows.Count * 26f + 20f,
                SkiaSharp.SKTextAlign.Left, regular, mutedPaint);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] BlankPng(int width, int height, string message)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        using var font = new SkiaSharp.SKFont(
            SkiaSharp.SKFontManager.Default.MatchCharacter('李') ?? SkiaSharp.SKTypeface.Default, 14f);
        using var paint = new SkiaSharp.SKPaint
        {
            Color = new SkiaSharp.SKColor(0x6F, 0x6A, 0x5E), IsAntialias = true,
        };
        canvas.DrawText(message, width / 2f, height / 2f, SkiaSharp.SKTextAlign.Center, font, paint);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>预览要按哪种格式画；与 App 层的导出格式枚举解耦，Core 不引用 App。</summary>
public enum MeetExportFormatKind
{
    Excel,
    Pdf,
}
