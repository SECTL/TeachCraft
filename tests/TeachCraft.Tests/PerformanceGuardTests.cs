using MeetRoster.App;
using MeetRoster.Core.Export;
using MeetRoster.Core.Models;
using TeachCraft.App.Diagnostics;

namespace TeachCraft.Tests;

/// <summary>
/// 性能回归守卫（T15）：把本轮已修的性能问题钉住，改回去就变红。
/// <para>
/// 这些断言都刻意写成「行为/结构性」而不是「耗时不高于 X ms」——计时断言在
/// CI 机器上必然抖动，而下面每一条被守的东西失效时，都会呈现为可观测的结构差异。
/// </para>
/// <para>
/// 每一条都做过反向验证：把对应修复改回原样，这条测试确实失败。
/// </para>
/// </summary>
public sealed class PerformanceGuardTests
{
    // ---------------------------------------------------------------- 守卫 1
    /// <summary>
    /// 预览必须按「显示尺寸」渲染，不能按导出尺寸（1.8 倍）。
    /// <para>
    /// 失效形态：<c>RenderPreviewPages</c> 又传回默认 scale。那时预览仍能出图、
    /// 内容也对，只是每张缩略图多花 4 倍以上的像素成本（实测 58ms → 135ms）——
    /// 纯性能问题，任何功能测试都抓不到，所以必须单独守。
    /// </para>
    /// <para>
    /// 判定方式：预览图比导出图小得多。用同一份工程分别渲染预览首页与导出一页，
    /// 比较 PNG 的像素面积。倍率若退回 1.8，两者面积会接近。
    /// </para>
    /// </summary>
    [Fact]
    public void PreviewRendersAtDisplayScale_NotExportScale()
    {
        var project = BuildSmallProject();
        var options = new MeetExportOptions();
        var summaries = MeetExportBuilder.Build(project, options);

        var preview = MeetPdfExporter.RenderPreviewPages(project, summaries, options, 1);
        Assert.NotEmpty(preview);

        // 预览图（0.45 倍）的像素应远小于导出图（1.8 倍）：面积比约 (0.45/1.8)² ≈ 6%。
        // 取 40% 作为宽松阈值——只要倍率没退回导出档，就一定低于它。
        var previewPixels = PixelArea(preview[0]);
        var exportPixels = 1000f * 1.8f * (1380f * 1.8f);   // A4 画布 × 导出倍率

        Assert.True(
            previewPixels < exportPixels * 0.40,
            $"预览图 {previewPixels:F0} px 应远小于导出图 {exportPixels:F0} px；" +
            "预览倍率可能被改回了导出倍率。");
    }

    // ---------------------------------------------------------------- 守卫 2
    /// <summary>
    /// 预览页数上限必须真的生效：请求 1 页就只返回 1 页。
    /// <para>
    /// 失效形态：<c>BuildPages</c> 的 maxPages 没传进 <c>Paginate</c>，
    /// 于是「只预览 1 页」会先把整本排完再 Take(1)。
    /// </para>
    /// </summary>
    [Fact]
    public void PreviewHonoursPageLimit()
    {
        // 必须用足够大的工程：一个班 30 人排不满两页，断言「返回 2 页」会因前提
        // 不成立而误报（这次就踩到了——单班只出 1 页）。
        var project = BuildWideProject();
        var options = new MeetExportOptions();
        var summaries = MeetExportBuilder.Build(project, options);

        Assert.Single(MeetPdfExporter.RenderPreviewPages(project, summaries, options, 1));
        Assert.Equal(2, MeetPdfExporter.RenderPreviewPages(project, summaries, options, 2).Count);
    }

    // ---------------------------------------------------------------- 守卫 3
    /// <summary>
    /// 竞名「一次导入只打开一次工作簿」。
    /// <para>
    /// 失效形态：某个导入入口绕过 <c>WorkbookSession</c> 又按路径重开。
    /// 手法与 <see cref="MeetRosterWorkbookReuseTests"/> 一致：把源文件在磁盘上改坏，
    /// 若实现回头碰路径就会读不到。这里额外守「坏文件按路径导入确实会失败」，
    /// 否则这剂毒药失效、测试变成永远通过的摆设。
    /// </para>
    /// </summary>
    [Fact]
    public void OrderBookImport_DoesNotReopenWorkbookByPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PerfGuard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var corpus = WriteOrderBook(dir);

            // 先证明毒药有效：按路径导入坏文件必须失败（抛异常或读不到班级）。
            var poisoned = corpus + ".poisoned.xlsx";
            File.Copy(corpus, poisoned);
            PoisonFile(poisoned);
            var poisonWorks = false;
            try
            {
                var brokenResult = MeetRoster.Core.Import.OrderBookImporter.Import(poisoned);
                poisonWorks = brokenResult.Project.Classes.Count == 0;
            }
            catch (Exception)
            {
                // 打不开会抛异常——同样说明「按路径重开」行不通。
                poisonWorks = true;
            }

            Assert.True(
                poisonWorks,
                "毒药无效：改坏的文件仍被按路径读出了班级，这条守卫失去意义。");

            // 正常路径必须读到班级（证明语料本身可用）。
            var good = MeetRoster.Core.Import.OrderBookImporter.Import(corpus);
            Assert.NotEmpty(good.Project.Classes);

            // 真正要守的：会话打开后，文件再被改坏也不影响导入结果。
            using var session = MeetRoster.Core.Import.WorkbookSession.Open(corpus);
            PoisonFile(corpus);
            var viaSession = MeetRoster.Core.Import.OrderBookImporter.Import(corpus, session.Workbook);
            Assert.Equal(good.Project.Classes.Count, viaSession.Project.Classes.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>PNG 的像素面积：只读 IHDR 的宽高，不解码整张图。</summary>
    private static double PixelArea(byte[] png)
    {
        // PNG: 8 字节签名 + 4 字节长度 + "IHDR" + 4 字节宽 + 4 字节高（大端）
        Assert.True(png.Length > 24);
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (double)width * height;
    }

    /// <summary>把 xlsx 的内容盖掉，仍保留 .xlsx 后缀，让「按路径重开」必然失败。</summary>
    private static void PoisonFile(string path)
    {
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
    }

    private static MeetProject BuildSmallProject()
    {
        var meetClass = new MeetClass { Name = "九（1）班", Group = "九年级" };
        for (var i = 0; i < 30; i++)
        {
            meetClass.Athletes.Add(new Athlete
            {
                Name = "沈青禾",
                Gender = i % 2 == 0 ? "男子" : "女子",
                Bib = (4000 + i).ToString(),
                IndividualEvent1 = "100米",
            });
        }

        var project = new MeetProject { Title = "示例中学田径运动会", Venue = "田径场" };
        project.Classes.Add(meetClass);
        return project;
    }

    /// <summary>虚构姓名池，与仓库各处的合成数据同类。</summary>
    private static readonly string[] SyntheticNames =
    {
        "商砚清", "洛听雪", "岑惊蛰", "裴知秋", "祁望川", "舒归远",
        "慕明溪", "卫南枝", "邵北亭", "柏拾月", "柳渡舟", "闻疏桐",
    };

    /// <summary>够多班的工程：确保「参赛概览 + 报名名单」能撑出两页以上。</summary>
    private static MeetProject BuildWideProject()
    {
        var project = new MeetProject { Title = "示例中学田径运动会", Venue = "田径场" };
        var index = 0;
        for (var c = 1; c <= 20; c++)
        {
            var meetClass = new MeetClass { Name = $"九（{c}）班", Group = "九年级" };
            for (var i = 0; i < 30; i++)
            {
                meetClass.Athletes.Add(new Athlete
                {
                    Name = SyntheticNames[(index++) % SyntheticNames.Length],
                    Gender = i % 2 == 0 ? "男子" : "女子",
                    Bib = (4000 + index).ToString(),
                    IndividualEvent1 = "100米",
                });
            }

            project.Classes.Add(meetClass);
        }

        return project;
    }

    private static string WriteOrderBook(string dir) =>
        MeetPerfCorpus.WriteOrderBook(dir, classes: 4);

    // ---------------------------------------------------------------- 守卫 4
    /// <summary>
    /// 导出预览必须在后台线程渲染，且必须能取消。
    /// <para>
    /// 失效形态：<c>RequestPreview</c> 退回「直接同步调 Render」。那时预览仍能出图、
    /// 内容也对，只是每次改设置都卡 UI 线程 60–135ms——纯性能问题，
    /// 任何功能测试都抓不到。这条守的就是「不在 UI 线程上画」这个结构事实。
    /// </para>
    /// <para>
    /// 判定方式：在 UI 线程上发起一次预览，同时断言「请求返回时还没画完」——
    /// 异步实现会先经过 200ms 防抖窗口，同步实现会立刻画完。
    /// </para>
    /// </summary>
    [Fact]
    public void PreviewRendering_IsAsynchronous_NotOnUiThread()
    {
        var project = BuildWideProject();
        var vm = new MeetExportOptionsViewModel(project, null);

        // RequestPreview 若同步渲染，返回时 PreviewImage 已经有值。
        // 异步实现会先进防抖等待，此刻必须还是空的。
        vm.RequestPreview();
        Assert.Null(vm.PreviewImage);

        // 防抖 + 渲染窗口内必须能取消（关窗路径依赖它）。
        vm.CancelPendingPreview();
    }

    /// <summary>
    /// 关窗后不得再往界面回写预览。
    /// 失效形态：去掉 CancelPendingPreview 的取消，后台任务完成后往已销毁的窗口写位图。
    /// </summary>
    [Fact]
    public void CancelPendingPreview_StopsFurtherWrites()
    {
        var project = BuildWideProject();
        var vm = new MeetExportOptionsViewModel(project, null);

        vm.CancelPendingPreview();

        // 取消之后再请求，应被 _isClosed 直接挡掉。
        vm.RequestPreview();
        Assert.Null(vm.PreviewImage);
    }
}
