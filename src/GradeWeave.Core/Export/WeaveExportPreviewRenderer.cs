using GradeWeave.Core.Models;
using SkiaSharp;

namespace GradeWeave.Core.Export;

/// <summary>预览结果：一张 PNG + 一行标题与说明。</summary>
public sealed class WeaveExportPreview
{
    public required byte[] Png { get; init; }
    public required string Caption { get; init; }
    public required string Detail { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// 织序导出预览：把「将要导出的东西」画成缩略图，用户在导出设置里先看一眼再决定。
/// 只画示意，不落盘、不生成完整工作簿。
/// </summary>
public static class WeaveExportPreviewRenderer
{
    /// <summary>预览缩略图宽度；够看清版式即可。</summary>
    public const int ThumbnailWidth = 1180;

    private static readonly SKColor Ink = Hex("#141413");
    private static readonly SKColor Muted = Hex("#6F6A5E");
    private static readonly SKColor Line = Hex("#C9C5BA");
    private static readonly SKColor Paper = Hex("#FFFFFF");
    private static readonly SKColor SeewoYellow = Hex("#FFFF00");
    private static readonly SKColor SeewoHeader = Hex("#DDEBF7");
    private static readonly SKColor SeewoRed = Hex("#C00000");
    private static readonly SKColor Clay = Hex("#D97757");
    private static readonly SKColor Wash = Hex("#F4F2EC");

    private static readonly Lazy<SKTypeface> Regular = new(() =>
        SKFontManager.Default.MatchCharacter('李') ?? SKTypeface.Default);
    private static readonly Lazy<SKTypeface> Bold = new(() =>
        SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, '李') ?? SKTypeface.Default);

    public static WeaveExportPreview Render(WeaveProject project, WeaveExportStyle style)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Classes.Count == 0)
        {
            return new WeaveExportPreview
            {
                Png = Blank("还没有班级，先导入或新建工程"),
                Caption = "无法预览",
                Detail = "工程里还没有班级。",
                Width = ThumbnailWidth,
                Height = 240,
            };
        }

        return style == WeaveExportStyle.Seewo
            ? RenderSeewo(project)
            : RenderWorkbook(project, style);
    }

    /// <summary>
    /// PNG / PDF 导出的是「当前班级或教师的 A4 课表」，与工作簿不是同一份东西，
    /// 所以这两种格式要用单独的画法——预览必须和真正导出的内容一致。
    /// </summary>
    public static WeaveExportPreview RenderSingle(WeaveProject project, string? classId, string? teacherId)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (teacherId is not null && project.FindTeacher(teacherId) is { } teacher)
        {
            return RenderSingleTimetable(project, teacher.Name, "教师",
                (day, period) => project.Lessons
                    .Where(l => l.TeacherId == teacher.Id && l.DayIndex == day && l.Period == period)
                    .Select(l => WeaveExportSupport.TeacherCell(project, l))
                    .ToList());
        }

        if (classId is not null && project.FindClass(classId) is { } cls)
        {
            return RenderSingleTimetable(project, cls.Name, "班级",
                (day, period) => WeaveExportSupport
                    .LessonCell(project, project.LessonAt(cls.Id, day, period))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .ToList());
        }

        return new WeaveExportPreview
        {
            Png = Blank("先在主窗口左侧选择一个班级或教师"),
            Caption = "未选择导出对象",
            Detail = "PNG / PDF 导出当前选中的班级或教师课表。",
            Width = ThumbnailWidth,
            Height = 240,
        };
    }

    /// <summary>单班 / 单教师的 A4 课表缩略图：与 WeaveImageRenderer 的版面同构（节次 × 五天）。</summary>
    private static WeaveExportPreview RenderSingleTimetable(
        WeaveProject project, string title, string kind, Func<int, int, List<string>> cellLines)
    {
        var periods = WeaveExportSupport.PeriodsOf(project);
        var days = WeaveDefaults.Days.Length;
        var rowHeight = 30;
        var height = 60 + 40 + periods.Count * rowHeight + 34;

        using var bitmap = new SKBitmap(ThumbnailWidth, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);

        using var regular = new SKFont(Regular.Value, 11f);
        using var bold = new SKFont(Bold.Value, 12f);
        using var small = new SKFont(Regular.Value, 10f);
        using var inkPaint = new SKPaint { Color = Ink, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = Muted, IsAntialias = true };
        using var linePaint = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 0.6f, Style = SKPaintStyle.Stroke,
        };
        using var washPaint = new SKPaint { Color = Wash, IsAntialias = true };
        using var clayPaint = new SKPaint { Color = Clay, IsAntialias = true };

        const float left = 24f;
        var width = ThumbnailWidth - left * 2f;
        var labelWidth = 84f;
        var dayWidth = (width - labelWidth) / days;

        canvas.DrawText($"{title} {kind}课表 · 预览", left, 26f, SKTextAlign.Left, bold, inkPaint);
        canvas.DrawText($"{project.GradeName} · {project.TermName} · A4 打印",
            left, 42f, SKTextAlign.Left, small, mutedPaint);

        const float top = 54f;
        canvas.DrawRect(new SKRect(left, top, left + width, top + 30f), washPaint);
        DrawCentered(canvas, "节次/星期", new SKRect(left, top, left + labelWidth, top + 30f), small, mutedPaint);
        for (var day = 1; day <= days; day++)
        {
            DrawCentered(canvas, WeaveDefaults.DayName(day),
                new SKRect(left + labelWidth + (day - 1) * dayWidth, top,
                    left + labelWidth + day * dayWidth, top + 30f),
                bold, inkPaint);
        }

        DrawCellBorders(canvas, left, top, width, 30f, 1, 1, linePaint);
        canvas.DrawLine(left, top + 30f, left + width, top + 30f, clayPaint);

        var y = top + 30f;
        foreach (var slot in periods)
        {
            DrawCentered(canvas, slot.Label, new SKRect(left, y, left + labelWidth, y + rowHeight), small, inkPaint);

            for (var day = 1; day <= days; day++)
            {
                var lines = cellLines(day, slot.Index);
                if (lines.Count > 0)
                {
                    var rect = new SKRect(left + labelWidth + (day - 1) * dayWidth, y,
                        left + labelWidth + day * dayWidth, y + rowHeight);
                    if (lines.Count == 1)
                        DrawCentered(canvas, lines[0], rect, small, inkPaint);
                    else
                        DrawCentered(canvas, lines[0] + "\n" + string.Join(" · ", lines.Skip(1)), rect, small, inkPaint);
                }
            }

            DrawCellBorders(canvas, left, y, width, rowHeight, 1, 1, linePaint);
            y += rowHeight;
        }

        canvas.DrawText("与导出的 A4 打印课表同版式；预览为缩略示意。",
            left, y + 16f, SKTextAlign.Left, small, mutedPaint);

        return new WeaveExportPreview
        {
            Png = Encode(bitmap),
            Caption = $"{title} {kind}课表 · {periods.Count} 节 × {days} 天",
            Detail = "PNG / PDF 导出当前选中的班级或教师，版面与这里一致。",
            Width = ThumbnailWidth,
            Height = height,
        };
    }

    // ----------------------------- 希沃版面 -----------------------------

    /// <summary>
    /// 画希沃模板的版面：黄色标题与须知、蓝色表头、被真实数据替换的示例行。
    /// 版面元素与模板一致（标题合并、须知合并、每天两列）。
    /// 预览只画周一到周五——中学课表通常只有工作日有课，画满七天会把每列压得看不清；
    /// 周末两列在导出文件里仍然保留（空着）。
    /// </summary>
    private static WeaveExportPreview RenderSeewo(WeaveProject project)
    {
        const int previewDays = 5;
        var rows = WeaveSeewoExporter.BuildRows(project, null);
        var sample = rows.Take(4).ToList();

        var headerHeight = 30 + 26 + 44 + 20;
        var rowHeight = 30;
        var height = 60 + headerHeight + Math.Max(1, sample.Count) * rowHeight + 40;

        using var bitmap = new SKBitmap(ThumbnailWidth, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);

        using var regular = new SKFont(Regular.Value, 11f);
        using var bold = new SKFont(Bold.Value, 12f);
        using var small = new SKFont(Regular.Value, 10f);

        using var inkPaint = new SKPaint { Color = Ink, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = Muted, IsAntialias = true };
        using var linePaint = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 0.6f, Style = SKPaintStyle.Stroke,
        };
        using var yellowPaint = new SKPaint { Color = SeewoYellow, IsAntialias = true };
        using var headerPaint = new SKPaint { Color = SeewoHeader, IsAntialias = true };
        using var whitePaint = new SKPaint { Color = Paper, IsAntialias = true };

        const float left = 24f;
        var width = ThumbnailWidth - left * 2f;
        var y = 34f;

        canvas.DrawText("希沃全校课表 · 预览", left, 22f, SKTextAlign.Left, bold, inkPaint);

        // 第 1 行：黄色标题（模板 A1:Q1 合并）
        canvas.DrawRect(new SKRect(left, y, left + width, y + 30f), yellowPaint);
        DrawCentered(canvas, "希沃全校课表模板", new SKRect(left, y, left + width, y + 30f), bold, inkPaint);
        DrawCellBorders(canvas, left, y, width, 30f, 1, 1, linePaint);
        y += 30f;

        // 第 2 行：黄色须知（模板 A2:Q2 合并）
        canvas.DrawRect(new SKRect(left, y, left + width, y + 26f), yellowPaint);
        canvas.DrawText("【填写须知】：黑色格子为系统生成，请勿修改；单双周用「/」隔开……",
            left + 5f, y + 17f, SKTextAlign.Left, small, inkPaint);
        DrawCellBorders(canvas, left, y, width, 26f, 1, 1, linePaint);
        y += 26f;

        // 第 3–4 行：表头。A:B 合并「班级」，C 纵向合并「课节（必填）」，D–Q 每天两列。
        var headerTop = y;
        var infoWidth = 96f;                      // A + B + C 三列
        var gradeWidth = infoWidth * 0.30f;       // A 年级
        var classWidth = infoWidth * 0.44f;       // B 班级
        var periodWidth = infoWidth - gradeWidth - classWidth; // C 课节
        var dayWidth = (width - infoWidth) / previewDays;
        var subjectWidth = dayWidth * 0.45f;

        canvas.DrawRect(new SKRect(left, headerTop, left + width, headerTop + 40f), headerPaint);
        DrawCentered(canvas, "班级", new SKRect(left, headerTop, left + infoWidth - periodWidth, headerTop + 40f), bold, inkPaint);
        DrawCentered(canvas, "课节\n（必填）", new SKRect(left + infoWidth - periodWidth, headerTop, left + infoWidth, headerTop + 40f), small, inkPaint);

        var x = left + infoWidth;
        for (var day = 1; day <= previewDays; day++)
        {
            var dayName = WeaveDefaults.DayName(day);
            DrawCentered(canvas, dayName, new SKRect(x, headerTop, x + dayWidth, headerTop + 18f), bold, inkPaint);

            DrawCentered(canvas, "课程名称", new SKRect(x, headerTop + 18f, x + subjectWidth, headerTop + 40f), small, inkPaint);
            DrawCentered(canvas, "老师姓名或手机号", new SKRect(x + subjectWidth, headerTop + 18f, x + dayWidth, headerTop + 40f), small, inkPaint);
            x += dayWidth;
        }

        DrawCellBorders(canvas, left, headerTop, width, 40f, 1, 1, linePaint);
        // A/B 与 C 之间的分隔线，以及每天的分隔线
        DrawVLine(canvas, left + infoWidth - periodWidth, headerTop, headerTop + 40f, linePaint);
        x = left + infoWidth;
        for (var day = 1; day <= previewDays; day++)
        {
            DrawVLine(canvas, x, headerTop, headerTop + 40f, linePaint);
            DrawVLine(canvas, x + subjectWidth, headerTop + 18f, headerTop + 40f, linePaint);
            x += dayWidth;
        }

        y += 40f;

        // 数据行：真实课表的前两行
        foreach (var item in sample)
        {
            canvas.DrawRect(new SKRect(left, y, left + width, y + rowHeight), whitePaint);
            DrawCentered(canvas, item.Grade, new SKRect(left, y, left + gradeWidth, y + rowHeight), small, inkPaint);
            DrawCentered(canvas, item.ClassName, new SKRect(left + gradeWidth, y, left + infoWidth - periodWidth, y + rowHeight), small, inkPaint);
            DrawCentered(canvas, item.Period.ToString(),
                new SKRect(left + infoWidth - periodWidth, y, left + infoWidth, y + rowHeight), small, mutedPaint);

            x = left + infoWidth;
            for (var day = 0; day < previewDays; day++)
            {
                var subject = item.Subjects[day];
                var teacher = item.Teachers[day];
                DrawCentered(canvas, subject, new SKRect(x, y, x + subjectWidth, y + rowHeight), small, inkPaint);
                DrawCentered(canvas, teacher, new SKRect(x + subjectWidth, y, x + dayWidth, y + rowHeight), small, mutedPaint);
                x += dayWidth;
            }

            DrawCellBorders(canvas, left, y, width, rowHeight, 1, 1, linePaint);
            DrawVLine(canvas, left + gradeWidth, y, y + rowHeight, linePaint);
            DrawVLine(canvas, left + infoWidth - periodWidth, y, y + rowHeight, linePaint);
            x = left + infoWidth;
            for (var day = 1; day <= previewDays; day++)
            {
                DrawVLine(canvas, x, y, y + rowHeight, linePaint);
                DrawVLine(canvas, x + subjectWidth, y, y + rowHeight, linePaint);
                x += dayWidth;
            }

            y += rowHeight;
        }

        canvas.DrawText("黄色为模板固定内容，原样保留；第 5 行起的示例行已替换为真实课表。",
            left, y + 16f, SKTextAlign.Left, small, mutedPaint);

        return new WeaveExportPreview
        {
            Png = Encode(bitmap),
            Caption = $"希沃全校课表 · {project.Classes.Count} 个班 × {rows.Count / Math.Max(1, project.Classes.Count)} 节",
            Detail = "把课表填进希沃官方模板：标题、须知、表头、批注与合并单元格原样保留，只替换示例行。",
            Width = ThumbnailWidth,
            Height = height,
        };
    }

    // ----------------------------- 软件 / 参考校版式 -----------------------------

    private static WeaveExportPreview RenderWorkbook(WeaveProject project, WeaveExportStyle style)
    {
        var periods = WeaveExportSupport.PeriodsOf(project);
        var days = WeaveDefaults.Days.Length;
        var rowHeight = 24;
        var height = 56 + 26 + periods.Count * rowHeight + 34;

        using var bitmap = new SKBitmap(ThumbnailWidth, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);

        using var regular = new SKFont(Regular.Value, 9f);
        using var bold = new SKFont(Bold.Value, 10f);
        using var small = new SKFont(Regular.Value, 8f);
        using var inkPaint = new SKPaint { Color = Ink, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = Muted, IsAntialias = true };
        using var linePaint = new SKPaint
        {
            Color = Line, IsAntialias = true, StrokeWidth = 0.6f, Style = SKPaintStyle.Stroke,
        };
        using var washPaint = new SKPaint { Color = Wash, IsAntialias = true };
        using var clayPaint = new SKPaint { Color = Clay, IsAntialias = true };

        const float left = 28f;
        var width = ThumbnailWidth - left * 2f;
        var labelWidth = 68f;
        var dayWidth = (width - labelWidth) / days;

        canvas.DrawText(style == WeaveExportStyle.Reference ? "软件自带样式2 · 预览" : "课表工作簿 · 预览",
            left, 26f, SKTextAlign.Left, bold, inkPaint);

        const float top = 44f;
        canvas.DrawRect(new SKRect(left, top, left + width, top + 26f), washPaint);
        DrawCentered(canvas, "节次 / 星期", new SKRect(left, top, left + labelWidth, top + 26f), small, mutedPaint);
        for (var day = 1; day <= days; day++)
        {
            DrawCentered(canvas, WeaveDefaults.DayName(day),
                new SKRect(left + labelWidth + (day - 1) * dayWidth, top, left + labelWidth + day * dayWidth, top + 26f),
                bold, inkPaint);
        }

        DrawCellBorders(canvas, left, top, width, 26f, 1, 1, linePaint);

        var cls = project.Classes[0];
        var y = top + 26f;
        foreach (var slot in periods)
        {
            DrawCentered(canvas, slot.Label, new SKRect(left, y, left + labelWidth, y + rowHeight), small, inkPaint);

            for (var day = 1; day <= days; day++)
            {
                var lesson = project.LessonAt(cls.Id, day, slot.Index);
                var text = WeaveExportSupport.LessonCell(project, lesson).Split('\n')[0];
                if (text.Length > 0)
                {
                    DrawCentered(canvas, text,
                        new SKRect(left + labelWidth + (day - 1) * dayWidth, y,
                            left + labelWidth + day * dayWidth, y + rowHeight),
                        small, lesson is null ? mutedPaint : inkPaint);
                }
            }

            DrawCellBorders(canvas, left, y, width, rowHeight, 1, 1, linePaint);
            y += rowHeight;
        }

        // 外框加重，与导出文件的表头分隔线呼应。
        canvas.DrawRect(new SKRect(left, top + 26f, left + width, top + 26f + 1f), clayPaint);

        canvas.DrawText($"班级课表页 · {cls.Name}（预览为第一页示意）",
            left, y + 16f, SKTextAlign.Left, small, mutedPaint);

        return new WeaveExportPreview
        {
            Png = Encode(bitmap),
            Caption = style == WeaveExportStyle.Reference
                ? $"软件自带样式2 · {project.Classes.Count} 个班 · {periods.Count} 节"
                : $"软件自带样式 · {project.Classes.Count} 个班 · {periods.Count} 节",
            Detail = "预览是工作簿首页的样式示意；完整内容（总表 / 分工表 / 班级与教师课表）以导出文件为准。",
            Width = ThumbnailWidth,
            Height = height,
        };
    }

    // ----------------------------- 绘制工具 -----------------------------

    private static void DrawCellBorders(
        SKCanvas canvas, float left, float top, float width, float height,
        int columns, int rows, SKPaint paint)
    {
        canvas.DrawRect(new SKRect(left, top, left + width, top + height), paint);
        if (rows > 1)
        {
            for (var r = 1; r < rows; r++)
                canvas.DrawLine(left, top + height * r / rows, left + width, top + height * r / rows, paint);
        }

        if (columns > 1)
        {
            for (var c = 1; c < columns; c++)
                canvas.DrawLine(left + width * c / columns, top, left + width * c / columns, top + height, paint);
        }
    }

    private static void DrawVLine(SKCanvas canvas, float x, float top, float bottom, SKPaint paint) =>
        canvas.DrawLine(x, top, x, bottom, paint);

    private static void DrawCentered(SKCanvas canvas, string text, SKRect rect, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;

        var lines = text.Split('\n');
        var lineHeight = font.Size + 2f;
        var startY = rect.MidY - (lines.Length - 1) * lineHeight / 2f + font.Size * 0.36f;
        for (var i = 0; i < lines.Length; i++)
        {
            var width = font.MeasureText(lines[i]);
            canvas.DrawText(lines[i], rect.MidX - width / 2f, startY + i * lineHeight, font, paint);
        }
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Blank(string message)
    {
        using var bitmap = new SKBitmap(ThumbnailWidth, 240);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);
        using var font = new SKFont(Regular.Value, 13f);
        using var paint = new SKPaint { Color = Muted, IsAntialias = true };
        canvas.DrawText(message, ThumbnailWidth / 2f, 124f, SKTextAlign.Center, font, paint);
        return Encode(bitmap);
    }

    private static SKColor Hex(string value) =>
        SKColor.Parse(value);
}
