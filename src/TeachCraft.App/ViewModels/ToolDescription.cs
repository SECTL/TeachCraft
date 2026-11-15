using System;
using System.Collections.Generic;

namespace TeachCraft.App.ViewModels;

/// <summary>
/// 一件工具（或常备应用）的完整说明：一句话定位、功能要点、输入 / 输出 / 处理方式与三条功能摘要。
/// 只有用户点开卡片时才展开，所以文案可以写得比卡片上的短描述更具体。
/// 纯数据、不引用 Avalonia，便于直接做单元测试。
/// </summary>
public sealed class ToolDescription
{
    public ToolDescription(
        string name,
        string tagline,
        IReadOnlyList<string> highlights,
        string inputDetail,
        string outputDetail,
        string processingDetail,
        IReadOnlyList<string> features)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("名称不能为空。", nameof(name));
        if (string.IsNullOrWhiteSpace(tagline))
            throw new ArgumentException("工具说明不能为空。", nameof(tagline));
        if (highlights.Count == 0)
            throw new ArgumentException("至少要有一个功能要点。", nameof(highlights));
        if (features.Count == 0)
            throw new ArgumentException("至少要有一条功能摘要。", nameof(features));

        Name = name;
        Tagline = tagline;
        Highlights = highlights;
        InputDetail = inputDetail;
        OutputDetail = outputDetail;
        ProcessingDetail = processingDetail;
        Features = features;
    }

    /// <summary>
    /// 展示名称，必须与 MainViewModel 里的卡片名称逐字相同。
    /// 引导页要列全部工具与常备应用，名称若在这里缺席，那边就只能硬编码一份副本。
    /// </summary>
    public string Name { get; }

    /// <summary>一句话定位，说清「给谁用、解决什么、哪些坑已经填好」。</summary>
    public string Tagline { get; }

    /// <summary>用 chip 展示的功能要点，3–4 个短词。</summary>
    public IReadOnlyList<string> Highlights { get; }

    public string InputDetail { get; }
    public string OutputDetail { get; }

    /// <summary>处理方式与数据去向（本机 / 位图 / 无损等）。</summary>
    public string ProcessingDetail { get; }

    /// <summary>三条功能摘要，界面上按 A / B / C 编号展示。</summary>
    public IReadOnlyList<string> Features { get; }
}
