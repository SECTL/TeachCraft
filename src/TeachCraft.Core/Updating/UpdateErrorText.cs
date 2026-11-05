using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace TeachCraft.Core.Updating;

/// <summary>
/// 检查更新时的失败话术。
///
/// 每条都是**完整整句**（原因 + 一句可执行建议），不带「{action}失败：」前缀——
/// 那个前缀是给「文件处理」类失败用的，套到网络失败上会变成
/// 「处理失败：这台电脑现在连不上网」，读起来是错的。
///
/// 放在 Core 而不是 src/Shared/ErrorText.cs，是因为后者以链接文件编进各个 App，
/// 是 internal 类型，没法直接单测。
/// </summary>
public static class UpdateErrorText
{
    public const string NoNetwork = "这台电脑现在连不上网，请检查网络后重试。";
    public const string Timeout = "连接 GitHub 超时，可能是网络不稳定或被拦截，请稍后重试。";
    public const string TlsIntercepted = "网络连接被中间设备拦截，无法安全访问 GitHub；请让网管信任代理根证书，或改用手机热点。";
    public const string ProxyNeedsLogin = "需要先登录学校的上网认证，请用浏览器打开任意网页完成登录后重试。";
    public const string RateLimited = "请求过于频繁，请几分钟后再试。";
    public const string NotFound = "读取版本信息失败，可能是发布页暂时不可用，请稍后再试。";
    public const string BlockedByPortal = "网络被拦截或需要登录代理，请先用浏览器打开 github.com 确认能正常访问。";
    public const string ChecksumsMissing = "这次更新缺少完整性校验清单，为保证安全已中止下载。";
    public const string AssetNotInChecksums = "这个安装包暂时没有出现在校验清单里，可能发布还在进行中，请稍后再试。";
    public const string DownloadFailed = "安装包没有下载完整，已清理残留文件，请重试。";
    public const string ChecksumMismatch = "安装包校验失败，可能下载过程中出错，已删除，请重试。";

    /// <summary>把异常归一化为一句中文原因；认不出的异常交给调用方的通用话术。</summary>
    public static string? Classify(Exception ex)
    {
        switch (ex)
        {
            case TaskCanceledException:
            case TimeoutException:
                return Timeout;

            case HttpRequestException httpEx:
                if (FindInner<AuthenticationException>(httpEx) is not null)
                    return TlsIntercepted;
                if (FindInner<SocketException>(httpEx) is not null)
                    return NoNetwork;
                return null;

            case AuthenticationException:
                return TlsIntercepted;

            case SocketException:
                return NoNetwork;

            default:
                return null;
        }
    }

    /// <summary>
    /// 返回整句原因；认不出时返回 null，由调用方回落自己的兜底句。
    /// </summary>
    public static string? Describe(Exception ex, string action)
        => Classify(ex);

    /// <summary>
    /// 界面直接用的入口：认得出就给整句，认不出就套通用兜底。
    /// 兜底句与 src/Shared/ErrorText.cs 的措辞保持一致，避免同一个 App 里两种口气。
    /// </summary>
    public static string DescribeForUi(Exception ex, string action)
        => Classify(ex) ?? $"{action}失败：请检查网络后重试；若反复出现，请反馈给我们。";

    /// <summary>
    /// 把 HTTP 状态码翻译成人话。
    ///
    /// action 决定措辞的语境：检查更新时说「读取版本信息」，
    /// 下载安装包时必须说「下载」，否则用户会以为版本信息出了问题。
    /// </summary>
    public static string ForStatus(HttpStatusCode status, string action)
    {
        var isDownload = action.Contains("下载", StringComparison.Ordinal);

        return status switch
        {
            HttpStatusCode.ProxyAuthenticationRequired => ProxyNeedsLogin,
            (HttpStatusCode)403 => RateLimited,
            HttpStatusCode.NotFound => isDownload
                ? "这个安装包在发布页上找不到了，可能正在重新发布，请稍后再试。"
                : NotFound,
            _ => isDownload
                ? $"下载安装包失败（HTTP {(int)status}），请稍后再试。"
                : $"读取版本信息失败（HTTP {(int)status}），请稍后再试。",
        };
    }

    /// <summary>服务器返回 200 但内容不是 JSON —— 典型的校园网/门禁登录页。</summary>
    public static string ForNonJsonBody(string action) => BlockedByPortal;

    public static string ForMissingChecksums(string action) => ChecksumsMissing;

    /// <summary>
    /// 清单存在、但没有列出当前这个资产——这是发布进行中的状态，
    /// 与「整个发布缺清单」「取不到清单」是三种不同的情况，话术必须分开。
    /// </summary>
    public static string ForAssetNotInChecksums(string action) => AssetNotInChecksums;

    private static TException? FindInner<TException>(Exception ex) where TException : Exception
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is TException match) return match;
        }
        return null;
    }
}
