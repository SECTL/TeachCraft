namespace TeachCraft.Core.Updating;

/// <summary>
/// 更新通道。此前由「装的是哪一支」隐式决定——正式版用户永远看不到预览版，
/// 预览版用户两者都能看到。这挡住了两类合理诉求：正式版用户想尝鲜没有门，
/// 预览版用户想回到稳定也没有显式选择（只能靠版本号大小自然发生）。
/// 现在做成可选，默认跟随当前安装形态。
/// </summary>
public enum UpdateChannel
{
    /// <summary>只看正式版。预览版安装的用户选这个就能「降」回最近一个正式版。</summary>
    Stable,

    /// <summary>看预览版 + 正式版，取版本号最高的那个。</summary>
    Preview,
}

public static class UpdateChannelText
{
    public static string DisplayName(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Preview => "预览版",
        _ => "正式版",
    };

    /// <summary>把持久化的字符串解析回枚举；认不出时回退到稳定通道。</summary>
    public static UpdateChannel Parse(string? value) => value switch
    {
        "preview" => UpdateChannel.Preview,
        _ => UpdateChannel.Stable,
    };

    /// <summary>写进状态文件的稳定标识，不随界面文案变化。</summary>
    public static string ToStorageValue(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Preview => "preview",
        _ => "stable",
    };

    /// <summary>
    /// 没手动选过时该用哪个通道：跟随当前安装形态。
    /// 预览版用户默认继续看预览，正式版用户默认只看正式版——这是他们当初的选择。
    /// </summary>
    public static UpdateChannel DefaultFor(SemanticVersion current)
        => current.IsPreview ? UpdateChannel.Preview : UpdateChannel.Stable;
}
