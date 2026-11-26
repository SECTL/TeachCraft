using System;
using System.Collections.Generic;
using System.Linq;

namespace TeachCraft.App.ViewModels;

public enum OobeStepKind
{
    Welcome,
    LocalFirst,
    Capabilities,
    TryFirst,
}

/// <summary>引导的一页。四页共用同一个版式，只有内容不同。</summary>
/// <param name="Kind">页的语义，界面据此决定要不要显示入口按钮。</param>
/// <param name="Index">1 起的序号，用于「01 / 04」。</param>
/// <param name="Eyebrow">等宽小标，如「STEP 01 / 04」。</param>
/// <param name="Title">衬线大标题。</param>
/// <param name="Lead">一句话导语。</param>
/// <param name="Body">正文，可含换行。</param>
/// <param name="Bullets">要点列表。</param>
/// <param name="Actions">第 4 页的可点击入口；其余页为空。</param>
public sealed record OobeStep(
    OobeStepKind Kind,
    int Index,
    string Eyebrow,
    string Title,
    string Lead,
    string Body,
    IReadOnlyList<string> Bullets,
    IReadOnlyList<OobeAction>? Actions = null);

/// <summary>引导里一个可点击入口。动作名由界面层解释，模型层不引用任何 UI 类型。</summary>
public sealed record OobeAction(string Label, OobeActionKind Kind);

public enum OobeActionKind
{
    OpenGradeStrip,
    OpenSeatGrid,
    OpenUpdatePanel,
    OpenAccountPanel,
}

/// <summary>
/// 首次引导的文案。四页固定，每页对应一个「不解释他就会问」的问题：
/// 这是什么 / 文件去哪 / 能做什么 / 先从哪下手。
///
/// 工具与常备应用的名称、描述一律从 ToolDescriptionCatalog 取，
/// 绝不在这里另写一份——同一份文案两处维护是本仓库记录过的踩坑。
/// </summary>
public static class OobeContent
{
    public const int StepCount = 4;

    private static readonly Lazy<IReadOnlyList<OobeStep>> LazySteps = new(Build);

    public static IReadOnlyList<OobeStep> Steps => LazySteps.Value;

    /// <summary>界面上「首次引导」这个入口的标题。</summary>
    public const string EntryTitle = "首次引导";

    private static IReadOnlyList<OobeStep> Build()
    {
        return new[]
        {
            new OobeStep(
                OobeStepKind.Welcome,
                1,
                "STEP 01 / 04",
                "知微，一只放在案头的盒子",
                "见微知著。把成绩、座位、课表、图片与文档里的琐碎，收进一个窗口。",
                $"这是一个面向老师的本地工具箱：{ToolDescriptionCatalog.ToolIds.Count} 件图片、PDF 与表格小工具，加上 {ToolDescriptionCatalog.AppKeys.Count} 件常备应用。\n"
                + "不用注册、不用配置，打开就能用。",
                new[]
                {
                    "小工具覆盖图片处理、PDF 互转与拆合、名单核对和 Excel 按班拆表。",
                    "6 件常备应用：分晓、归位、时序、织序、竞名、辑册。",
                    "右上角的搜索框可以按名称或格式搜索，也可以直接点开任意一张卡。",
                }),

            new OobeStep(
                OobeStepKind.LocalFirst,
                2,
                "STEP 02 / 04",
                "文件在这台电脑上处理",
                "联网只发生在你开启的功能里。",
                "你的图片、表格与文档都在本机处理，处理结果只写到你选择的位置。\n"
                + "程序会在本机留存更新记录、账号与集控设置与这条引导自身的状态。",
                new[]
                {
                    "处理结果不会覆盖导入的原文件。",
                    "联网只有三处：点「检查更新」读取版本号；登录 SECTL 账号后，由你选择字段并手动上传或拉取共享名单与座位表；主动开启「启用 SECTL 集控」后保持一条长连接接收大屏指令。",
                    "共享名单默认只含姓名、性别、学号；身高、视力、成绩、备注、原小组由你逐项勾选，默认不包含。",
                    "账号与云同步、集控都默认关闭，关闭时不发送任何信息。",
                }),

            new OobeStep(
                OobeStepKind.Capabilities,
                3,
                "STEP 03 / 04",
                "盒子里有什么",
                $"六件常备应用各管一件事，{ToolDescriptionCatalog.ToolIds.Count} 件小工具补上零碎活。",
                string.Empty,
                BuildCapabilityBullets()),

            new OobeStep(
                OobeStepKind.TryFirst,
                4,
                "STEP 04 / 04",
                "先从这四件开始",
                "这四条入口点开就能用，不用先读文档。",
                "登录只打开账号页，不会自动连接。其余三条调用的就是左侧与卡片上的同一个入口：不会替你选好文件，也不会载入示例数据。",
                new[]
                {
                    "随时可以从左侧底部的「首次引导」重看这一页。",
                },
                new[]
                {
                    new OobeAction("打开分晓 · 成绩条", OobeActionKind.OpenGradeStrip),
                    new OobeAction("打开归位 · 座位表", OobeActionKind.OpenSeatGrid),
                    new OobeAction("看看检查更新", OobeActionKind.OpenUpdatePanel),
                    new OobeAction("账号与云同步（可选）", OobeActionKind.OpenAccountPanel),
                }),
        };
    }

    /// <summary>
    /// 第 3 页的条目全部从目录取。目录里加一件工具或常备应用，这里自动跟上，
    /// 不需要改引导——若条目多到一屏放不下，界面按 OobeOverflow 规则折叠。
    /// </summary>
    private static IReadOnlyList<string> BuildCapabilityBullets()
    {
        var bullets = new List<string>();

        foreach (var key in ToolDescriptionCatalog.AppKeys)
        {
            var description = ToolDescriptionCatalog.ForApp(key)
                ?? throw new InvalidOperationException($"目录里缺少常备应用「{key}」的说明，引导无法生成。");
            bullets.Add($"{description.Name} · {description.Tagline}");
        }

        var toolNames = ToolDescriptionCatalog.ToolIds
            .Select(id => (ToolDescriptionCatalog.ForTool(id)
                ?? throw new InvalidOperationException($"目录里缺少工具「{id}」的说明，引导无法生成。")).Name)
            .ToList();

        bullets.Add("小工具：" + string.Join("、", toolNames));
        return bullets;
    }
}
