
namespace TeachCraft.Core.Cloud;

/// <summary>
/// 云端文档版本冲突。<see cref="Remote"/> 是服务端当前版本，
/// 调用方据此提示「重新拉取后再保存」；本存储不做自动合并。
/// </summary>
public sealed class CloudConflictException : Exception
{
    public CloudDocumentInfo Remote { get; }

    public CloudConflictException(CloudDocumentInfo remote)
        : base($"云端内容已被其他设备更新到版本 {remote.Revision}，请重新拉取后再保存。")
    {
        Remote = remote;
    }

    public string UserFacingMessage => Message;
}
