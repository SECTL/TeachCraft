using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 共享名单文档的 JSON 编解码。只做序列化与版本门禁，不校验内容、不碰网络。
/// 未知字段一律容忍，方便后续版本加字段时旧客户端仍能读取。
/// </summary>
public static class SharedRosterCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Encode(SharedRosterDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return JsonSerializer.Serialize(doc, Options);
    }

    public static SharedRosterDocument Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("共享名单内容为空，无法读取。");

        var doc = JsonSerializer.Deserialize<SharedRosterDocument>(json, Options)
            ?? throw new ArgumentException("共享名单内容无法解析。");

        if (doc.V > 1)
            throw new InvalidOperationException(
                $"共享名单文档版本 {doc.V} 不受支持，当前只支持版本 1，请让发布者使用兼容版本。");

        return doc;
    }
}
