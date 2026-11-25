namespace TeachCraft.Core.Cloud;

/// <summary>
/// 本地保存的登录凭据。刻意用 class 而不是 record——
/// record 自动生成的 ToString 会把 token 明文打出来，日志里一漏就是事故。
/// </summary>
public sealed class CloudTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string TokenType { get; set; } = "Bearer";
    public DateTimeOffset ObtainedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string Protector { get; set; } = "";

    /// <summary>只暴露保护方式与到期时间，绝不输出任何 token 值。</summary>
    public override string ToString()
        => $"CloudTokens(Protector={Protector}, ExpiresAtUtc={ExpiresAtUtc:O})";
}
