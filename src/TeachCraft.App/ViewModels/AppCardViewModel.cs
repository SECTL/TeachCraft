using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TeachCraft.App.ViewModels;

/// <summary>
/// 案头常备卡的可变状态：展开与否 + 搜索用的检索文本。
/// 卡片本身的版式（大卡 / 行卡、图标画法）仍在 MainWindow.axaml 里手写，
/// 这里只承载状态与文案，避免把手写版式强行抽成一套模板后走样。
/// </summary>
public partial class AppCardViewModel : ObservableObject
{
    public AppCardViewModel(string key, string name, ToolDescription description)
    {
        Key = key;
        Name = name;
        Detail = description;

        // 搜索命中面：应用名、英文 key、一句话定位、要点、输入输出与三条摘要。
        SearchText = string.Join(
            ' ',
            name,
            key,
            description.Tagline,
            string.Join(' ', description.Highlights),
            description.InputDetail,
            description.OutputDetail,
            description.ProcessingDetail,
            string.Join(' ', description.Features)).ToLowerInvariant();
    }

    public string Key { get; }
    public string Name { get; }
    public ToolDescription Detail { get; }
    public string SearchText { get; }

    [ObservableProperty] private bool _isDescriptionExpanded;

    /// <summary>点卡片标题或卡片本身时调用：展开 / 收起这套说明。</summary>
    public void ToggleDescription()
    {
        IsDescriptionExpanded = !IsDescriptionExpanded;
    }

    public string DescriptionToggleLabel => IsDescriptionExpanded ? "收起说明" : "展开说明";

    /// <summary>读屏用的名称：说明这张卡按下会发生什么。</summary>
    public string DescriptionAccessibleName => $"{Name}，{DescriptionToggleLabel}";

    partial void OnIsDescriptionExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(DescriptionToggleLabel));
        OnPropertyChanged(nameof(DescriptionAccessibleName));
    }
}
