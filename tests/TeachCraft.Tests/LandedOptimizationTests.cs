using System.Text.Json;
using System.Xml.Linq;
using ClassBlock.App;
using SeatGrid.App;

namespace TeachCraft.Tests;

/// <summary>
/// GAP-7：把**已落地**的性能修复锁住，改回原样就变红。
/// <para>
/// 这四条都是 1.11.1 那轮修好的、之后被反复回退过的项。它们不会让任何功能
/// 测试失败（功能都对，只是变慢了），所以必须单独盯。
/// </para>
/// <para>
/// 判定一律用「读源码 / 读配置文件」的结构断言，不用计时断言——计时在 CI
/// 机器上必然抖动，而下面每一条失效时都呈现为可观测的结构差异。
/// </para>
/// </summary>
public sealed class LandedOptimizationTests
{
    private static string RepoRoot()
    {
        // 与既有测试一致：从测试程序集向上找到含 tests/ 的仓库根。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadSource(params string[] relative)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"找不到 {path}");
        return File.ReadAllText(path);
    }

    // ---------------------------------------------------------------- 1
    /// <summary>
    /// 归位预览必须按显示宽度解码（<c>DecodeToWidth</c>），不能整图解码。
    /// 失效形态：退回 <c>new Bitmap(path)</c>。最大布局单次峰值会从约 8MB 涨回 84MB。
    /// </summary>
    [Fact]
    public void SeatGridPreview_DecodesAtDisplayWidth()
    {
        var source = ReadSource("src", "SeatGrid.App", "SeatPreviewWindow.axaml.cs");

        Assert.Contains("DecodeToWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Bitmap(path)", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 2
    /// <summary>
    /// GC 内存压缩配置必须留在 <c>runtimeconfig.template.json</c> 里。
    /// 失效形态：文件被删或值被改掉。1.11.1 实测空载 270MB → 247MB 就是靠它。
    /// </summary>
    [Fact]
    public void RuntimeConfig_KeepsGcConserveMemory()
    {
        var path = Path.Combine(RepoRoot(), "src", "TeachCraft.App", "runtimeconfig.template.json");
        Assert.True(File.Exists(path), "runtimeconfig.template.json 不见了");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            doc.RootElement.TryGetProperty("configProperties", out var props),
            "缺少 configProperties 段");

        Assert.True(
            props.TryGetProperty("System.GC.ConserveMemory", out var value),
            "缺少 System.GC.ConserveMemory");

        Assert.Equal(9, value.GetInt32());
    }

    // ---------------------------------------------------------------- 3
    /// <summary>
    /// 时序预览必须按 1.25 倍渲染，不能退回 2.0 倍。
    /// 失效形态：倍率被改大。常驻内存从约 8MB 涨回约 21MB（4 倍像素）。
    /// </summary>
    [Fact]
    public void ClassBlockPreview_RendersAtOnePointTwoFive()
    {
        var source = ReadSource("src", "ClassBlock.App", "ClassBlockViewModel.cs");

        Assert.Contains("RenderPngBytes(doc, 1.25f", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderPngBytes(doc, 2.0f", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 4
    /// <summary>
    /// 织序总表必须复用课程格（按「班级 + 天 + 节」索引），不能每次全量重建。
    /// 失效形态：<c>_masterCells</c> 的复用分支被删掉，拖拽 / 刷新时重建上千个 Border。
    /// </summary>
    [Fact]
    public void GradeWeaveMasterGrid_ReusesCellsBySignature()
    {
        var source = ReadSource("src", "GradeWeave.App", "GradeWeaveWindow.axaml.cs");

        Assert.Contains("_masterCells", source, StringComparison.Ordinal);
        // 签名命中即复用，不再重建。
        Assert.Contains("RefreshMasterCells", source, StringComparison.Ordinal);
        Assert.Contains("_masterGridSignature", source, StringComparison.Ordinal);
    }
}
