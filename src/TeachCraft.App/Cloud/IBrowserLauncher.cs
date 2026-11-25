namespace TeachCraft.App.Cloud;

/// <summary>
/// 用系统默认浏览器打开授权地址。宿主层抽象，便于测试与替换实现。
/// </summary>
public interface IBrowserLauncher
{
    Task OpenAsync(Uri url, CancellationToken ct);
}
