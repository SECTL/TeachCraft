using TeachCraft.Shared;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 云端功能的失败话术。全是能直接进状态栏的完整中文句子，
/// 界面只绑定这里，不在 XAML 里另写一份。
/// </summary>
public static class CloudErrorText
{
    public const string NoNetwork = "当前没有网络连接，请检查网络后重试。";
    public const string Timeout = "连接云端超时，请稍后重试。";
    public const string Unauthorized = "登录状态已失效，请重新登录。";
    public const string Forbidden = "你没有权限执行此操作。";
    public const string CloudDisabled = "该账号尚未开通云存储服务，请联系平台管理员。";
    public const string CloudSyncOff = "云同步已关闭，开启后才能访问云端名单。";
    public const string NotFound = "云端没有找到对应的内容，可能已被删除。";
    public const string PayloadTooLarge = "内容超过云端单条容量上限，请精简字段后再试。";
    public const string KeyLimitReached = "云端存储条数已达上限，请清理旧内容后再试。";
    public const string ListPageLimitExceeded = "云端键数量超出可安全读取的上限，请清理部分内容后重试。";
    public const string KeyTooLong = "云端存储键名过长，请缩短名称后重试。";
    public const string SignInCancelled = "已取消登录。";
    public const string SignInTimeout = "登录等待超时，请重新发起登录。";
    public const string SignInWaitingBrowser = "正在打开浏览器，请在浏览器中完成 SECTL 登录，完成后回到这里。";
    public const string ConsentRequiredDetailed = "请先阅读「共享名单包含哪些字段」并在下方点「我已了解，同意上述字段说明」，然后再登录。";
    public const string StateMismatch = "登录校验失败，请重新发起登录。";
    public const string RefreshRevoked = "登录已失效，请重新登录。";
    public const string TokenPersistFailed = "凭据无法保存到本机，请检查本机权限后重新登录。";
    public const string DeviceIdentityPersistFailed = "设备标识无法保存，请检查本机权限。";
    public const string EncryptionUnavailable = "当前系统无法保护本地凭据，登录暂不可用。";
    public const string NotConfigured = "云端功能尚未配置完成，请稍后再试。";
    public const string BrowserOpenFailed = "无法打开浏览器，请手动复制登录地址完成登录。";
    public const string ServerError = "云端服务暂时不可用，请稍后重试。";
    public const string RosterDeleted = "这份共享名单已被发布者删除。";
    public const string RosterEmpty = "共享名单为空，没有可导入的学生。";
    public const string RosterNameDuplicate = "共享名单中有重复姓名，请先处理后再导入。";
    public const string ExamSnapshotRefused = "考场快照不能发布到云端，请先返回日常座位。";
    public const string NoLinkedRoster = "当前座位表没有关联的共享名单。";

    public static string RateLimited(int retryAfterSeconds)
        => $"操作过于频繁，请在 {retryAfterSeconds} 秒后重试。";

    public static string RosterVersionChanged(int oldV, int newV)
        => $"共享名单已从版本 {oldV} 更新到 {newV}，请重新拉取后再操作。";

    public static string RosterFieldMissing(string fields)
        => $"共享名单缺少字段：{fields}，请让发布者补全后重新发布。";

    /// <summary>
    /// 界面翻译入口的单测代理：ErrorText 是 internal 且以链接文件编进各 App，测试程序集不可见，
    /// 这里在 Core 内嵌的那份 ErrorText 上转一层，供测试断言业务异常原样透传。
    /// </summary>
    public static string DescribeForUi(Exception ex, string action) => ErrorText.Describe(ex, action);
}
