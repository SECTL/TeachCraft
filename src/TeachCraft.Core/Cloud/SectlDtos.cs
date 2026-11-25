using System.Text.Json.Serialization;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 换 token / 刷新成功后的结果。刻意用 class 而不是 record，并手写 ToString：
/// record 自动生成的 ToString 会把 access_token / refresh_token 明文打进日志。
/// </summary>
public sealed class SectlTokenSet
{
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public int ExpiresIn { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public string Scope { get; init; } = "";

    public override string ToString()
        => $"SectlTokenSet(TokenType={TokenType}, ExpiresIn={ExpiresIn}, " +
           $"ExpiresAtUtc={ExpiresAtUtc:O}, HasRefreshToken={!string.IsNullOrWhiteSpace(RefreshToken)})";
}

/// <summary>userinfo 的容错结果。取不到显示名时按优先级回退，最后落到「已登录」。</summary>
public sealed class SectlUserInfo
{
    public string Sub { get; init; } = "";
    public string? Name { get; init; }
    public string? PreferredUsername { get; init; }
    public string? Email { get; init; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name!;
            if (!string.IsNullOrWhiteSpace(PreferredUsername)) return PreferredUsername!;
            if (!string.IsNullOrWhiteSpace(Email)) return Email!;
            return "已登录";
        }
    }
}

/// <summary>token 端点的线上格式。未知字段忽略、缺失字段留空，绝不臆造 token。</summary>
internal sealed class TokenResponseDto
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    public SectlTokenSet ToTokenSet(DateTimeOffset obtainedAtUtc) => new()
    {
        AccessToken = AccessToken ?? "",
        RefreshToken = string.IsNullOrWhiteSpace(RefreshToken) ? null : RefreshToken,
        TokenType = string.IsNullOrWhiteSpace(TokenType) ? "Bearer" : TokenType!,
        ExpiresIn = ExpiresIn ?? 0,
        ExpiresAtUtc = ExpiresIn is > 0 ? obtainedAtUtc.AddSeconds(ExpiresIn.Value) : null,
        Scope = Scope ?? "",
    };

    public override string ToString()
        => $"TokenResponseDto(TokenType={TokenType}, ExpiresIn={ExpiresIn}, " +
           $"HasRefreshToken={!string.IsNullOrWhiteSpace(RefreshToken)})";
}

/// <summary>userinfo 端点的线上格式。</summary>
internal sealed class UserInfoResponseDto
{
    [JsonPropertyName("sub")] public string? Sub { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("preferred_username")] public string? PreferredUsername { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }

    public SectlUserInfo ToUserInfo() => new()
    {
        Sub = Sub ?? "",
        Name = Name,
        PreferredUsername = PreferredUsername,
        Email = Email,
    };
}

/// <summary>introspect 端点的线上格式，供后续校验 token 状态使用。</summary>
internal sealed class IntrospectResponseDto
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("sub")] public string? Sub { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("exp")] public long? Exp { get; set; }
}
