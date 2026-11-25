
namespace TeachCraft.Core.Cloud;

/// <summary>
/// 云端账号流程的失败。Message 一律是能直接进状态栏的中文句子；
/// ErrorCode 只用于内部区分 invalid_grant 这类需要清凭据的情况，不进界面。
/// </summary>
public sealed class CloudAuthException : Exception
{
    public string? ErrorCode { get; }

    public CloudAuthException(string message, string? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string UserFacingMessage => Message;
}
