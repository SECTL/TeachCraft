using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using SeatGrid.Core.Models;
using SeatGrid.Core.Rendering;
using A = DocumentFormat.OpenXml.Drawing;

namespace SeatGrid.Core.Export;

/// <summary>
/// 座位表导出成单页 16:9 PPTX，供老师把座位表塞进既有的教研或家长会幻灯片。
///
/// 与 PNG 不追求像素级一致——PPTX 的字体渲染与软件内本来就不同，
/// 座位顺序必须复用 <see cref="Rendering.SeatChartComposer.GroupOrder / ColumnOrder / RowOrder"/>，
/// 否则导出的顺序会与界面不一致。
/// </summary>
public static class SeatChartPptxExporter
{
    // 16:9 幻灯片：13.333 × 7.5 英寸，1 英寸 = 914400 EMU。
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;
    private const long Margin = 457200;      // 0.5 英寸

    public static void Export(SeatChartDocument document, string path)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var presentation = PresentationDocument.Create(target, PresentationDocumentType.Presentation);

        var presentationPart = presentation.AddPresentationPart();
        presentationPart.Presentation = new Presentation(
            new SlideMasterIdList(),
            new SlideIdList(),
            new SlideSize
            {
                Cx = new Int32Value((int)SlideWidth),
                Cy = new Int32Value((int)SlideHeight),
            },
            new NotesSize
            {
                Cx = new Int64Value(SlideWidth),
                Cy = new Int64Value(SlideHeight),
            });

        // PPTX 的最小可用结构：母版 + 版式 + 主题。缺任何一个，PowerPoint 都会
        // 报「需要修复」，而 OpenXml SDK 自己不会替我们补。
        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        masterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties())),
            new ColorMapOverride(new A.MasterColorMapping()))
        {
            ColorMap = new ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
        };
        masterPart.SlideMaster.Save();

        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = BuildTheme();
        themePart.Theme.Save();

        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        layoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties())),
            new ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = SlideLayoutValues.Blank,
        };
        layoutPart.SlideLayout.Save();

        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(layoutPart);
        slidePart.Slide = new Slide(new CommonSlideData(BuildSlide(document))) { Show = true };
        slidePart.Slide.Save();

        var masterIdList = presentationPart.Presentation.SlideMasterIdList!;
        masterIdList.Append(new SlideMasterId
        {
            Id = 2147483648U,
            RelationshipId = presentationPart.GetIdOfPart(masterPart),
        });

        var slideIdList = presentationPart.Presentation.SlideIdList!;
        slideIdList.Append(new SlideId
        {
            Id = 256U,
            RelationshipId = presentationPart.GetIdOfPart(slidePart),
        });

        presentationPart.Presentation.Save();
    }

    private static ShapeTree BuildSlide(SeatChartDocument document)
    {
        var shapes = new List<OpenXmlElement>();

        var layout = document.Layout;
        var titleHeight = 620000L;

        // 标题
        shapes.Add(MakeTextShape(
            2U, "Title", Margin, 200000, SlideWidth - Margin * 2, titleHeight,
            document.Title, 2800, bold: true, "#141413", center: true));

        // 信息行
        shapes.Add(MakeTextShape(
            3U, "Info", Margin, 820000, SlideWidth - Margin * 2, 300000,
            document.InfoLine, 1200, bold: false, "#6F6A5E", center: true));

        var gridTop = 1250000L;
        var gridHeight = SlideHeight - gridTop - Margin;
        var gridWidth = SlideWidth - Margin * 2;

        var groups = document.Groups;
        var rowCount = groups.Count == 0 ? 0 : groups[0].Rows.Count;
        var columns = layout.Columns;

        if (groups.Count > 0 && rowCount > 0)
        {
            var cellWidth = gridWidth / (groups.Count * columns);
            var cellHeight = gridHeight / rowCount;
            var gap = Math.Min(cellWidth, cellHeight) / 12;

            uint id = 10;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                for (var r = 0; r < group.Rows.Count; r++)
                {
                    var row = group.Rows[r];
                    for (var c = 0; c < row.Seats.Count; c++)
                    {
                        var seat = row.Seats[c];
                        if (seat.Kind == SeatCellKind.Aisle) continue;

                        var x = Margin + (groupIndex * columns + c) * cellWidth + gap / 2;
                        var y = gridTop + r * cellHeight + gap / 2;

                        shapes.Add(MakeTextShape(
                            id++, $"Seat{id}",
                            x, y,
                            Math.Max(1, cellWidth - gap), Math.Max(1, cellHeight - gap),
                            seat.Name,
                            (int)Math.Min(2000, Math.Max(800, cellHeight / 6)),
                            bold: document.Settings.BoldNames,
                            color: seat.IsFemale && document.Settings.MarkFemales
                                ? document.Settings.FemaleMark
                                : document.Settings.SeatText,
                            center: true,
                            fill: seat.IsEmpty ? document.Settings.EmptySeatBackground : document.Settings.SeatBackground,
                            border: document.Settings.SeatBorder));
                    }
                }
            }
        }

        // ShapeTree 的构造参数是一次性的：后续 Append 不会写进 XML。
        // 所以所有 shape 必须在这里一次性传入。
        var tree = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties());
        foreach (var shape in shapes) tree.Append(shape);
        return tree;
    }

    private static Shape MakeTextShape(
        uint id, string name, long x, long y, long cx, long cy,
        string text, int fontSizeHundredths, bool bold, string color, bool center,
        string? fill = null, string? border = null)
    {
        var runProperties = new A.RunProperties
        {
            FontSize = fontSizeHundredths,
            Bold = bold,
            Language = "zh-CN",
        };
        runProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = Hex(color) }));
        runProperties.Append(new A.LatinFont { Typeface = "Microsoft YaHei" });
        runProperties.Append(new A.EastAsianFont { Typeface = "Microsoft YaHei" });

        var run = new A.Run(runProperties, new A.Text(text));
        var paragraph = new A.Paragraph(run);
        paragraph.ParagraphProperties = new A.ParagraphProperties
        {
            Alignment = center ? A.TextAlignmentTypeValues.Center : A.TextAlignmentTypeValues.Left,
        };

        var shape = new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(),
            new TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                paragraph));

        var shapeProperties = shape.ShapeProperties!;
        shapeProperties.Transform2D = new A.Transform2D(
            new A.Offset { X = x, Y = y },
            new A.Extents { Cx = cx, Cy = cy });

        if (fill is not null)
            shapeProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = Hex(fill) }));
        else
            shapeProperties.Append(new A.NoFill());

        if (border is not null)
        {
            shapeProperties.Append(new A.Outline(
                new A.SolidFill(new A.RgbColorModelHex { Val = Hex(border) }))
            {
                Width = 9525,   // 0.75 pt
            });
        }

        return shape;
    }

    /// <summary>最小主题：一组标准配色 + 字体。没有它 PowerPoint 打不开。</summary>
    private static A.Theme BuildTheme()
    {
        static A.RgbColorModelHex Color(string hex) => new() { Val = hex };

        var colorScheme = new A.ColorScheme
        {
            Name = "SeatGrid",
            Dark1Color = new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
            Light1Color = new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
            Dark2Color = new A.Dark2Color(Color("141413")),
            Light2Color = new A.Light2Color(Color("FAF9F5")),
            Accent1Color = new A.Accent1Color(Color("D97757")),
            Accent2Color = new A.Accent2Color(Color("5A7A4B")),
            Accent3Color = new A.Accent3Color(Color("8A6A3C")),
            Accent4Color = new A.Accent4Color(Color("4F6D7A")),
            Accent5Color = new A.Accent5Color(Color("A34A36")),
            Accent6Color = new A.Accent6Color(Color("C9C1AE")),
            Hyperlink = new A.Hyperlink(Color("A34A36")),
            FollowedHyperlinkColor = new A.FollowedHyperlinkColor(Color("6F6A5E")),
        };

        var fontScheme = new A.FontScheme(
            new A.MajorFont(
                new A.LatinFont { Typeface = "Microsoft YaHei" },
                new A.EastAsianFont { Typeface = "Microsoft YaHei" },
                new A.ComplexScriptFont { Typeface = string.Empty }),
            new A.MinorFont(
                new A.LatinFont { Typeface = "Microsoft YaHei" },
                new A.EastAsianFont { Typeface = "Microsoft YaHei" },
                new A.ComplexScriptFont { Typeface = string.Empty }))
        {
            Name = "SeatGrid",
        };

        var formatScheme = new A.FormatScheme(
            new A.FillStyleList(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
            new A.LineStyleList(
                new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }))),
            new A.EffectStyleList(new A.EffectStyle()),
            new A.BackgroundFillStyleList(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
        {
            Name = "SeatGrid",
        };

        return new A.Theme(
            new A.ThemeElements(colorScheme, fontScheme, formatScheme))
        {
            Name = "SeatGrid",
        };
    }

    private static string Hex(string value)
    {
        var normalized = SeatChartSettings.NormalizeColor(value, "#000000");
        return normalized[1..].ToUpperInvariant();
    }
}
