using Homework.Core.Scanning;

namespace TeachCraft.Tests;

public sealed class HomeworkScanFileClassifierTests
{
    [Theory]
    [InlineData("a.png", ScanFileKind.Image)]
    [InlineData("a.JPG", ScanFileKind.Image)]
    [InlineData("a.jpeg", ScanFileKind.Image)]
    [InlineData("a.bmp", ScanFileKind.Image)]
    [InlineData("a.BMP", ScanFileKind.Image)]
    [InlineData("a.pdf", ScanFileKind.Pdf)]
    [InlineData("a.PDF", ScanFileKind.Pdf)]
    [InlineData("a.tif", ScanFileKind.Unknown)]
    [InlineData("a.txt", ScanFileKind.Unknown)]
    [InlineData("", ScanFileKind.Unknown)]
    [InlineData(null, ScanFileKind.Unknown)]
    public void KindOf_RecognizesSupportedFormats(string? path, ScanFileKind expected) =>
        Assert.Equal(expected, ScanFileClassifier.KindOf(path));

    [Theory]
    [InlineData("~$scan.pdf", true)]
    [InlineData("~scan.pdf", true)]
    [InlineData(".hidden.pdf", true)]
    [InlineData("scan.pdf.tmp", true)]
    [InlineData("scan.part", true)]
    [InlineData("scan.crdownload", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("desktop.ini", true)]
    [InlineData("scan.pdf", false)]
    [InlineData("scan.png", false)]
    public void IsTemporary_DetectsInProgressAndMetadataFiles(string path, bool expected) =>
        Assert.Equal(expected, ScanFileClassifier.IsTemporary(path));

    [Fact]
    public void IsImportable_RequiresSupportedAndNotTemporary()
    {
        Assert.True(ScanFileClassifier.IsImportable("scan.pdf"));
        Assert.True(ScanFileClassifier.IsImportable("scan.png"));
        Assert.True(ScanFileClassifier.IsImportable("scan.bmp"));
        Assert.False(ScanFileClassifier.IsImportable("~$scan.pdf"));
        Assert.False(ScanFileClassifier.IsImportable("scan.tif"));
    }
}
