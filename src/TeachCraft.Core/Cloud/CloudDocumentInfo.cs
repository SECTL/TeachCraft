namespace TeachCraft.Core.Cloud;

/// <summary>索引里的一条文档摘要。Revision 是 1 基版本号，覆盖时由调用方比对。</summary>
public sealed record CloudDocumentInfo(
    string Id,
    string Name,
    string Kind,
    DateTimeOffset UpdatedAtUtc,
    int Bytes,
    int Revision);

/// <summary>一份文档的摘要 + 原始 JSON 内容（已解压）。</summary>
public sealed record CloudDocumentPayload(CloudDocumentInfo Info, string Json);
