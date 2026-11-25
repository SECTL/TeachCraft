using SkiaSharp;
using TeachCraft.Core.Tools;

namespace TeachCraft.Tests;

public sealed class CertificatePageGeometryTests
{
    [Theory]
    [InlineData(900, 600, true)]
    [InlineData(600, 900, false)]
    [InlineData(600, 600, true)]
    public void Measure_CentersAnUncroppedProportionalTemplateOnA4(float width, float height, bool landscape)
    {
        var metrics = CertificatePageGeometry.Measure(new SKSize(width, height));
        Assert.Equal(landscape, metrics.IsLandscape);
        Assert.Equal(landscape ? CertificatePageGeometry.A4Long : CertificatePageGeometry.A4Short, metrics.PageWidth);
        Assert.Equal(landscape ? CertificatePageGeometry.A4Short : CertificatePageGeometry.A4Long, metrics.PageHeight);
        var rect = metrics.TemplateRect;
        Assert.InRange(Math.Abs(rect.Width / rect.Height - width / height), 0, 0.0001f);
        Assert.InRange(rect.Left, 0, metrics.PageWidth);
        Assert.InRange(rect.Top, 0, metrics.PageHeight);
        Assert.InRange(Math.Abs(rect.Left - (metrics.PageWidth - rect.Right)), 0, 0.0001f);
        Assert.InRange(Math.Abs(rect.Top - (metrics.PageHeight - rect.Bottom)), 0, 0.0001f);
        Assert.Equal(rect.MidX, CertificatePageGeometry.Anchor(rect, 0.5, 0.5).X);
        Assert.Equal(rect.MidY, CertificatePageGeometry.Anchor(rect, 0.5, 0.5).Y);
        var larger = CertificatePageGeometry.Measure(new SKSize(width * 2, height * 2));
        Assert.Equal(rect, larger.TemplateRect);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Measure_RejectsInvalidDimensions(float value)
    {
        Assert.Throws<ToolboxException>(() => CertificatePageGeometry.Measure(new SKSize(value, 600)));
        Assert.Throws<ToolboxException>(() => CertificatePageGeometry.Measure(new SKSize(900, value)));
    }

    [Fact]
    public void Placement_ClampsFiniteCoordinatesAndFontScales()
    {
        Assert.Equal(new CertificateTextPlacement(0, 1, 1, 0, 0.01, 0.40),
            new CertificateTextPlacement(-2, 2, 3, -3, -1, 2).Clamp());
    }

    [Fact]
    public void FitFontSize_ShrinksLongTextToTemplateWidth()
    {
        using var font = new SKFont();
        var rect = SKRect.Create(20, 30, 400, 300);
        var text = new string('W', 100);
        var fitted = CertificatePageGeometry.FitFontSize(rect, 0.4, text, font);
        Assert.InRange(fitted, 0.001f, 119.999f);
        font.Size = fitted;
        Assert.InRange(font.MeasureText(text), rect.Width * 0.88f - 0.1f, rect.Width * 0.88f + 0.1f);
    }
}
