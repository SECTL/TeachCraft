using Homework.Core;
using Homework.Core.Models;
using Homework.Core.Pages;
using SkiaSharp;

namespace TeachCraft.Tests;

/// <summary>
/// 图片导入 / 解码 / 缩略图缓存。全部使用合成临时文件，测试结束即删除。
/// </summary>
public sealed class HomeworkPageImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "TeachCraft", "Homework", "tests", Guid.NewGuid().ToString("N"));
    private readonly List<PageThumbnailCache> _caches = [];

    public HomeworkPageImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var cache in _caches) cache.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private PageThumbnailCache NewCache()
    {
        var cache = new PageThumbnailCache();
        _caches.Add(cache);
        return cache;
    }

    private string WriteBmp(string name, int width, int height)
    {
        var path = Path.Combine(_root, name);
        const int bytesPerPixel = 3;
        var rowStride = ((width * bytesPerPixel + 3) / 4) * 4;
        var pixelBytes = rowStride * height;
        var fileSize = 54 + pixelBytes;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);              // BITMAPINFOHEADER 长度
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);        // 平面数
        writer.Write((short)24);       // 24 位色
        writer.Write(0);               // 未压缩
        writer.Write(pixelBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        var row = new byte[rowStride];
        for (var i = 0; i < width; i++)
        {
            row[i * 3] = 0x57;         // BGR: 知微橙 #D97757
            row[i * 3 + 1] = 0x77;
            row[i * 3 + 2] = 0xD9;
        }
        for (var y = 0; y < height; y++) writer.Write(row);
        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private string WritePng(string name, int width, int height)
    {
        var path = Path.Combine(_root, name);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x2E, 0x86, 0xC1));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }
        using var stream = File.Create(path);
        Assert.True(bitmap.Encode(stream, SKEncodedImageFormat.Png, 100));
        return path;
    }

    /// <summary>写一张 JPEG，并在 SOI 之后插入带 Orientation 的 EXIF APP1 段。</summary>
    private string WriteJpegWithOrientation(string name, int width, int height, int orientation)
    {
        var path = Path.Combine(_root, name);
        using (var bitmap = new SKBitmap(width, height))
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(0xD9, 0x77, 0x57));
            using var stream = File.Create(path);
            Assert.True(bitmap.Encode(stream, SKEncodedImageFormat.Jpeg, 95));
        }

        var jpeg = File.ReadAllBytes(path);
        var exif = BuildExifApp1(orientation);
        var merged = new byte[jpeg.Length + exif.Length];
        merged[0] = jpeg[0]; // SOI 0xFF
        merged[1] = jpeg[1]; // SOI 0xD8
        Array.Copy(exif, 0, merged, 2, exif.Length);
        Array.Copy(jpeg, 2, merged, 2 + exif.Length, jpeg.Length - 2);
        File.WriteAllBytes(path, merged);
        return path;
    }

    private static byte[] BuildExifApp1(int orientation)
    {
        // 小端 TIFF：II / 42 / IFD0@8 / 1 条 / Orientation(SHORT)=orientation / 下一 IFD=0
        var tiff = new List<byte>
        {
            0x49, 0x49,
            0x2A, 0x00,
            0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01,
            0x03, 0x00,
            0x01, 0x00, 0x00, 0x00,
        };
        var value = (ushort)orientation;
        tiff.Add((byte)(value & 0xFF));
        tiff.Add((byte)(value >> 8));
        tiff.AddRange([0x00, 0x00]);          // SHORT 值补齐 4 字节
        tiff.AddRange([0x00, 0x00, 0x00, 0x00]); // 下一 IFD 偏移 = 0

        var payload = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00 };
        payload.AddRange(tiff);

        var length = payload.Count + 2; // 长度字段自身占 2 字节
        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };
        segment.AddRange(payload);
        return segment.ToArray();
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _action;
        public SyncProgress(Action<int> action) => _action = action;
        public void Report(int value) => _action(value);
    }

    // ----------------------------- 导入顺序与去重 -----------------------------

    [Fact]
    public void ImportImages_NaturalSort_IgnoresPickerOrder()
    {
        var p10 = WritePng("IMG_10.png", 24, 24);
        var p2 = WritePng("IMG_2.png", 24, 24);
        var p1 = WritePng("IMG_1.png", 24, 24);
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var added = PageImporter.ImportImages(
            batch, [p10, p2, p1], cache, null, CancellationToken.None, out var warnings);

        Assert.Equal(3, added.Count);
        Assert.Empty(warnings);
        Assert.Equal(new[] { p1, p2, p10 }, batch.Pages.Select(p => p.SourcePath).ToArray());
        Assert.Equal(3, Directory.GetFiles(cache.SessionDirectory, "*.png").Length);
    }

    [Fact]
    public void ImportImages_DuplicatePathAndPage_SkippedWithWarning()
    {
        var path = WritePng("same.png", 24, 24);
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var added = PageImporter.ImportImages(
            batch, [path, path], cache, null, CancellationToken.None, out var warnings);

        Assert.Single(added);
        Assert.Single(batch.Pages);
        Assert.Single(warnings);
        Assert.Contains("已经加入过", warnings[0]);
    }

    [Fact]
    public void ImportImages_CorruptFile_WarnsAndContinues()
    {
        var bad = Path.Combine(_root, "bad.png");
        File.WriteAllText(bad, "not an image at all");
        var good = WritePng("good.png", 24, 24);
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        var added = PageImporter.ImportImages(
            batch, [bad, good], cache, null, CancellationToken.None, out var warnings);

        Assert.Single(added);
        Assert.Single(batch.Pages);
        Assert.Equal(good, batch.Pages[0].SourcePath);
        Assert.Single(warnings);
        Assert.Contains("bad.png", warnings[0]);
    }

    [Fact]
    public void ImportImages_CancelBetweenFiles_KeepsEarlierFiles()
    {
        var p1 = WritePng("IMG_1.png", 24, 24);
        var p2 = WritePng("IMG_2.png", 24, 24);
        var p3 = WritePng("IMG_3.png", 24, 24);
        var cts = new CancellationTokenSource();
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();

        PageImporter.ImportImages(
            batch, [p1, p2, p3], cache,
            new SyncProgress(p => { if (p >= 33) cts.Cancel(); }), cts.Token, out var warnings);

        Assert.Single(batch.Pages);
        Assert.Equal(p1, batch.Pages[0].SourcePath);
        Assert.Contains("取消", string.Join("", warnings));
    }

    // ----------------------------- 解码 -----------------------------

    [Fact]
    public void DecodePreview_ScaledDecode_RespectsMaxEdge()
    {
        var path = WritePng("big.png", 800, 600);

        using var decoded = PageImageDecoder.DecodePreview(path, 100);

        Assert.Equal(100, decoded.Width);
        Assert.Equal(75, decoded.Height);
        Assert.True(decoded.Width <= 100 && decoded.Height <= 100);
        Assert.Equal(64, decoded.ContentHash.Length);
    }

    [Fact]
    public void DecodePreview_Bmp_DecodesAndHashes()
    {
        var path = WriteBmp("scan.bmp", 120, 160);

        using var decoded = PageImageDecoder.DecodePreview(path, 200);

        Assert.Equal(120, decoded.Width);
        Assert.Equal(160, decoded.Height);
        Assert.Equal(64, decoded.ContentHash.Length);
    }

    [Fact]
    public void RenderPreview_FromOriginal_IsSharperThanThumbnail()
    {
        var source = WriteBmp("large-scan.bmp", 2400, 3200);
        var batch = HomeworkBatch.Create("作业");
        using var cache = NewCache();
        PageImporter.ImportImages(batch, [source], cache, null, CancellationToken.None, out _);
        var page = Assert.Single(batch.Pages);

        var previewPath = PageImporter.RenderPreview(page, cache, 2400, CancellationToken.None);

        var thumbnailPath = Directory.EnumerateFiles(cache.SessionDirectory, "*.png").First();
        using var preview = PageImageDecoder.DecodePreview(previewPath, 6000);
        using var thumbnail = PageImageDecoder.DecodePreview(thumbnailPath, 6000);
        Assert.True(preview.Width > thumbnail.Width,
            $"paths preview={previewPath} thumb={thumbnailPath} sizes preview={preview.Width}x{preview.Height} thumb={thumbnail.Width}x{thumbnail.Height}");
        Assert.Equal(1800, preview.Width);
        Assert.Equal(2400, preview.Height);
        Assert.False(string.IsNullOrEmpty(cache.FindPreview(page, 2400)));
    }

    [Fact]
    public void DecodePreview_ExifOrientation_SwapsDimensions()
    {
        // Orientation=6（顺时针 90°）：40×20 的横图摆正后应为 20×40 的竖图。
        var path = WriteJpegWithOrientation("rot.jpg", 40, 20, 6);

        using var decoded = PageImageDecoder.DecodePreview(path, 200);

        Assert.Equal(20, decoded.Width);
        Assert.Equal(40, decoded.Height);
    }

    [Fact]
    public void DecodePreview_Corrupt_ThrowsChinese()
    {
        var path = Path.Combine(_root, "broken.jpg");
        File.WriteAllText(path, "this is not a jpeg");

        var ex = Assert.Throws<HomeworkException>(() => PageImageDecoder.DecodePreview(path, 100));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Contains("图片", ex.Message);
    }

    [Fact]
    public void ComputeContentHash_SameContent_SameHash()
    {
        var a = WritePng("a.png", 40, 40);
        var b = WritePng("b.png", 40, 40);
        using var da = PageImageDecoder.DecodePreview(a, 100);
        using var db = PageImageDecoder.DecodePreview(b, 100);

        Assert.Equal(da.ContentHash, db.ContentHash);
        Assert.Equal(64, da.ContentHash.Length);
    }

    // ----------------------------- 缩略图缓存 -----------------------------

    [Fact]
    public void ThumbnailCache_Dispose_RemovesSessionDir()
    {
        var cache = new PageThumbnailCache();
        var dir = cache.SessionDirectory;
        using var bitmap = new SKBitmap(10, 10);

        var path = cache.SaveThumbnail("source.png", 3, bitmap);

        Assert.True(File.Exists(path));
        Assert.True(Directory.Exists(dir));
        cache.Dispose();
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ThumbnailCache_CleanStale_RemovesOldDirs()
    {
        var cache = NewCache();
        var dir = cache.SessionDirectory;
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromDays(2));

        PageThumbnailCache.CleanStale(TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(dir));
    }
}
