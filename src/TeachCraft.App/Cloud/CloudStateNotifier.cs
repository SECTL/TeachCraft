namespace TeachCraft.App.Cloud;

/// <summary>
/// 云同步开关的广播点。只发信号，不携带数据——可用性一律回读设置存储，
/// 这样「关掉同步」之后任何缓存了旧状态的窗口都不会再读到过期的 true。
/// </summary>
public sealed class CloudStateNotifier
{
    public event EventHandler? StateChanged;

    public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
}
