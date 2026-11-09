using System;
using System.Collections.Generic;

namespace TeachCraft.App.ViewModels;

/// <summary>
/// 说明文案的唯一来源。工具 ID 与常备应用 key 都是稳定契约
/// （见 .memory/03-功能与工具面板.md），界面只从这里取，避免同一段话
/// 在 XAML 与视图模型里各写一份。纯静态数据，可直接做单元测试。
/// </summary>
public static class ToolDescriptionCatalog
{
    public const string GradeStripKey = "gradestrip";
    public const string SeatGridKey = "seatgrid";
    public const string ClassBlockKey = "classblock";
    public const string GradeWeaveKey = "gradeweave";
    public const string MeetRosterKey = "meetroster";

    /// <summary>顺序与 .memory/03 的工具契约表一致：01–04 图片、05–10 格式工厂。</summary>
    public static IReadOnlyList<string> ToolIds { get; } = new[]
    {
        "image-convert",
        "image-resize",
        "image-compress",
        "image-watermark",
        "pdf-word",
        "word-pdf",
        "images-pdf",
        "pdf-images",
        "pdf-merge",
        "pdf-split",
    };

    /// <summary>案头常备顺序：分晓 / 归位 / 时序 / 织序 / 竞名。</summary>
    public static IReadOnlyList<string> AppKeys { get; } = new[]
    {
        GradeStripKey,
        SeatGridKey,
        ClassBlockKey,
        GradeWeaveKey,
        MeetRosterKey,
    };

    private static readonly Dictionary<string, ToolDescription> Tools = new(StringComparer.Ordinal)
    {
        ["image-convert"] = new ToolDescription(
            "把 PNG、JPG、WebP 互相转一遍。手机拍的竖图按 EXIF 方向摆正；透明背景转 JPG 时先铺白底，不会压出一块黑。",
            new[] { "三格式互转", "EXIF 摆正", "白底兜底", "画质可调" },
            "PNG / JPG / WebP / BMP / GIF",
            "*-转换.png | jpg | webp",
            "本机 SkiaSharp 解码与编码，文件不上传",
            new[]
            {
                "格式任选：PNG 无损，JPG / WebP 可调画质 40–100。",
                "透明背景转 JPG 前先画一层白底，透明区不会变成纯黑。",
                "输出名自动加「-转换」，不会顺手覆盖刚导入的原图。",
            }),

        ["image-resize"] = new ToolDescription(
            "按宽高、按百分比或等比缩放，一次把像素尺寸改到位；EXIF 方向照样摆正，竖拍的照片不会躺倒。",
            new[] { "宽高像素", "等比缩放", "百分比", "方向摆正" },
            "任意常见图片（PNG / JPG / WebP / BMP / GIF）",
            "*-缩放.<原扩展名>",
            "本机 SkiaSharp 三次卷积缩放，文件不上传",
            new[]
            {
                "只填宽度或高度，另一边按原图比例自动算，不会被拉变形。",
                "填百分比则整图等比缩到指定比例，适合做课件配图。",
                "目标边长超过 40000 像素会直接拒绝，避免内存被吃满。",
            }),

        ["image-compress"] = new ToolDescription(
            "在画质与体积之间找平衡：JPG / WebP 调画质，所有格式都能限制最长边；完成后直接告诉你从多少 KB 压到多少 KB。",
            new[] { "画质 40–100", "最长边限制", "体积对比", "PNG 无损" },
            "PNG / JPG / WebP",
            "*-压缩.<原扩展名>",
            "本机重编码，压缩前后都只在这台电脑上",
            new[]
            {
                "PNG 无损，画质滑块不改画质，体积收益主要来自缩小边长。",
                "JPG / WebP 按画质重新编码，适合把照片压进公众号或课件。",
                "结果栏显示压缩前后体积，一眼看出这次到底省了多少。",
            }),

        ["image-watermark"] = new ToolDescription(
            "给图片压一行文字水印：五个落点、字号与不透明度都可调；深色图自动用白字、浅色图自动用墨字，并加一层反色阴影保证可读。",
            new[] { "五档位置", "自动黑白字", "反色阴影", "字号倍率" },
            "任意常见图片（PNG / JPG / WebP / BMP / GIF）",
            "*-水印.<原扩展名>",
            "本机 SkiaSharp 绘制，水印文字不离开这台电脑",
            new[]
            {
                "字号按图宽的 3.2% 起算，可在 0.4–4 倍之间调整。",
                "先画阴影再画正文，浅底深底都看得清，不糊成一团。",
                "水印文字留空会提示先填内容，不会输出一张白图。",
            }),

        ["pdf-word"] = new ToolDescription(
            "两种转换方式：要改文字选「可编辑文字」，要保版式选「保留版面」。前者按内容顺序抽出段落，后者把每页渲成图片嵌进 DOCX。",
            new[] { "文字可编辑", "版面保真", "DPI 72–300", "分页保留" },
            "PDF（加密文档需先解除密码）",
            "*-转换.docx",
            "本机 PdfPig 提取 / PDFium 渲染，文件不上传",
            new[]
            {
                "文字模式逐行抽取正文并保留分页，适合二次编辑。",
                "版面模式每页按 DPI 渲成 PNG，再按 A4 内容区嵌入。",
                "版面模式视觉保真但不可编辑，用来存档或打印更稳。",
            }),

        ["word-pdf"] = new ToolDescription(
            "把 DOCX 排成 A4 PDF：标题层级、加粗斜体下划线、居左居右与表格边框都保留，页脚带页码。",
            new[] { "A4 自动排版", "表格保留", "页脚页码", "无需装 Word" },
            ".docx（Word 文档）",
            "与原文件同名的 .pdf（可另选位置）",
            "本机 QuestPDF 排版，文件不上传",
            new[]
            {
                "读段落与表格后按 A4 排版，不依赖本机是否装了 Word。",
                "不渲染内嵌图片、页眉页脚、分栏与浮动对象，长文档先自查。",
                "输出路径与源 DOCX 相同时会被拒绝，原文件始终安全。",
            }),

        ["images-pdf"] = new ToolDescription(
            "多张图片按你排的顺序合成一份 PDF，一页一张；A4 按图片横竖自动切换，长边适配、内容居中。",
            new[] { "顺序可控", "横竖自适应", "A4 居中", "一页一图" },
            "多张图片（可一次多选，也可以追加）",
            "图片合成.pdf",
            "本机 QuestPDF 排版，图片不上传",
            new[]
            {
                "列表里每行都能上移、下移、移除，顺序就是最终页序。",
                "横图自动用横向 A4，竖图用纵向 A4，长边贴合页面。",
                "保存时若已存在同名文件，会先问一次是否覆盖。",
            }),

        ["pdf-images"] = new ToolDescription(
            "把 PDF 逐页导成 PNG，DPI 可从 72 调到 300；输出到指定文件夹，长文档会在进度条上显示「第 N 页 / 共 M 页」。",
            new[] { "逐页 PNG", "DPI 72–300", "进度可见", "输出到文件夹" },
            "PDF 文档",
            "<输出文件夹>\\*-第N页.png",
            "本机 PDFium 渲染，逐页写盘，不上传",
            new[]
            {
                "逐页渲染逐页写盘，几十页的扫描件也不会把内存撑爆。",
                "150 DPI 适合课件与截图，300 DPI 适合印刷与投稿。",
                "导出目录可以和 PDF 放在一起，但绝不会写回源 PDF。",
            }),

        ["pdf-merge"] = new ToolDescription(
            "两份以上的 PDF 按你排的顺序拼成一份；列表顺序就是页序，排错了可以上移下移再合并。",
            new[] { "顺序可调", "至少两份", "原文件不动", "输出保护" },
            "多份 PDF（至少 2 份）",
            "合并结果.pdf",
            "本机 PDFsharp 导入页对象，文件不上传",
            new[]
            {
                "少于两份会提示先补齐，不会生成一份空文件。",
                "输出路径不能覆盖任何一份输入，Core 层直接拒绝。",
                "只想留中间几页时，合并后再用「PDF 拆分提取」。",
            }),

        ["pdf-split"] = new ToolDescription(
            "填一段页码范围就能抽出想要的那几页，写成 1-3,5 就够；留空表示整份原样另存一份。",
            new[] { "页码范围", "中英文逗号", "越界即报错", "原文件不动" },
            "PDF 文档",
            "*-提取.pdf",
            "本机 PDFsharp 复制页对象，文件不上传",
            new[]
            {
                "支持 1-3,5 与 1–3、1~3 等写法，中英文逗号都能识别。",
                "页码超出总页数会明确报错，不会静默截成半份文件。",
                "输出路径与源 PDF 相同时拒绝写入，原文件全程只读。",
            }),
    };

    private static readonly Dictionary<string, ToolDescription> Apps = new(StringComparer.Ordinal)
    {
        [GradeStripKey] = new ToolDescription(
            "一位老师从智学网导出总表，到手里攥着一叠裁好的成绩条，中间只隔着一次拖放。多场考试按顺序排好，进退步、学科诊断与学生画像一并计算。",
            new[] { "成绩条导出", "多考对比", "学科诊断", "学生画像" },
            ".xlsx / .xlsm / .xls（成绩总表与智学网分析报表）",
            "A4 横向成绩条 .xlsx",
            "文件不上传",
            new[]
            {
                "多场考试按时间排序，任选一场作基准算进退步。",
                "学科诊断：得分率、分段分布与教学建议。",
                "学生画像：单科对照、班级位置、历次走势。",
            }),

        [SeatGridKey] = new ToolDescription(
            "导入班级名单，自动识别姓名、性别、身高与视力；4 组 × 6 排 × 2 列的默认布局可直接用，排座、预览、打印与导出一次完成。",
            new[] { "名单自动识别", "六种排法", "同桌性别", "三种导出" },
            ".xlsx / .xls / .csv（带表头或不带表头的名单都认）",
            "PNG / A4 PDF / Excel 座位表 + 配置 .xlsx",
            "本机 SkiaSharp + QuestPDF，文件不上传",
            new[]
            {
                "排法：名单顺序、男女交替、随机、按身高、按视力、同性同桌。",
                "座位可直接拖动互换，界面预览与三种导出共用同一份布局。",
                "PDF 用高分辨率位图嵌入，不会把整个中文字体内嵌成 20MB+。",
            }),

        [ClassBlockKey] = new ToolDescription(
            "导入课程总表，自动认出年级、学期、月份与全部班级；选中一个班，就能得到一张可直接打印的课程表。",
            new[] { "只收 xlsx", "班级自动识别", "作息模板", "PNG + PDF" },
            ".xlsx 课程总表（带分工表、午学、课服 / 晚修更好）",
            "PNG 图片 / A4 PDF",
            "本机 ClosedXML 读取 + QuestPDF 排版，文件不上传",
            new[]
            {
                "行序由作息模板驱动，内置七 / 八 / 九三套预设可另存。",
                "主课默认只显示学科，勾选后学科加粗、教师排第二行。",
                "课服五天内容一致才合并成一格，不一致就逐日如实显示。",
            }),

        [GradeWeaveKey] = new ToolDescription(
            "时序的上游：先把全年级的课排出来，再交给时序排版。教研、走班、会议先定，剩下的一次排清冲突。",
            new[] { "五步流程", "自动编排", "七类冲突", "Excel / PNG / PDF" },
            "年级工作簿 .xlsx（分工表 / 确认表 / 课程表 / 会议备注）或 .weave.json 工程",
            "总表 / 分工表 / 教师职位表 / 班级课表 / 教师课表 / 冲突页",
            "本机 ClosedXML + QuestPDF，课表不上传",
            new[]
            {
                "作息未确认时生成按钮禁用，节次一改就要重新确认。",
                "20 班 / 62 教师 / 694 节正课的真实规模能排满，排不完会如实报告。",
                "每 2 秒自动保存，关窗前再补写一次，不会丢最后几秒的改动。",
            }),

        [MeetRosterKey] = new ToolDescription(
            "一份秩序册也能整本导进来：名单、日程与分组表一次读齐，选中班级即高亮，还能导出只含该班选手的参赛手册。",
            new[] { "秩序册整本导入", "规则体检", "单班高亮", "导出参赛手册" },
            "班级报名表 .xlsx（可多选）、秩序册 .xlsx，或往届「竞赛日程 / 项目分组表」",
            "按班回写的报名表 + 全部或单班的 Excel / PDF 参赛手册",
            "本机 ClosedXML 读写、SkiaSharp 排版，报名表与秩序册不上传",
            new[]
            {
                "秩序册一次读齐名单与赛程，号码布、领队与工作人员都带进来。",
                "赛程勾「仅显示该班选手」只留本班条目，导出时整行加底色。",
                "导出分四页 Excel 或纸墨版式 A4 PDF，跨页自动重复表头。",
            }),
    };

    /// <summary>按工具 ID 取说明；ID 不存在时返回 null（界面隐藏说明区，不崩）。</summary>
    public static ToolDescription? ForTool(string? toolId) =>
        toolId is not null && Tools.TryGetValue(toolId, out var description) ? description : null;

    /// <summary>按常备应用 key 取说明；key 不存在时返回 null。</summary>
    public static ToolDescription? ForApp(string? appKey) =>
        appKey is not null && Apps.TryGetValue(appKey, out var description) ? description : null;
}
