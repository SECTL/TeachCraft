using System.Security.Cryptography;
using System.Text;

namespace TeachCraft.Core.Cloud;

/// <summary>凭据落盘前的保护方式。名字写进信封，换实现就打不开旧文件（fail closed）。</summary>
public interface ITokenProtector
{
    string Name { get; }
    bool IsAvailable { get; }
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

/// <summary>Windows DPAPI（CurrentUser 作用域）：同一用户同一台机器才能解开。</summary>
public sealed class DpapiTokenProtector : ITokenProtector
{
    public string Name => "dpapi";
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI 仅在 Windows 可用。");
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));
    }

    public string Unprotect(string protectedText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI 仅在 Windows 可用。");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser));
    }
}

/// <summary>base64 混淆，非 Windows 平台的兜底；它不是加密，只避免明文直接躺在文件里。</summary>
public sealed class PlainTokenProtector : ITokenProtector
{
    public string Name => "plain";
    public bool IsAvailable => true;

    public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedText) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
}

/// <summary>恒等变换，仅供测试使用。</summary>
public sealed class NoopTokenProtector : ITokenProtector
{
    public string Name => "none";
    public bool IsAvailable => true;

    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedText) => protectedText;
}

public static class TokenProtectorFactory
{
    public static ITokenProtector Create()
        => OperatingSystem.IsWindows() ? new DpapiTokenProtector() : new PlainTokenProtector();
}
