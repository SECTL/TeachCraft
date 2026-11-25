using System.Security.Cryptography;
using System.Text;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// RFC 7636 的 PKCE 参数生成。verifier / state 都是 32 字节随机数转 43 字符 base64url，
/// challenge 是 verifier 的 ASCII 字节做 SHA-256 后再转 base64url（S256）。
/// </summary>
public static class Pkce
{
    public static string CreateVerifier() => ToBase64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateChallenge(string verifier)
        => ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => ToBase64Url(RandomNumberGenerator.GetBytes(32));

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
