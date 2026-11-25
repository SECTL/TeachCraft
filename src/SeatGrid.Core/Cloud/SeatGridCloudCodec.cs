using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeatGrid.Core.Cloud;

/// <summary>
/// 云端座位表文档的 JSON 编解码。未知字段忽略、缺字段走默认值；版本高于本模块支持的版本时
/// 明确拒绝，绝不把新版本文档当成旧版本读（那会静默丢字段）。
/// </summary>
public static class SeatGridCloudCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Encode(SeatGridCloudDocument document) =>
        JsonSerializer.Serialize(document, Options);

    public static SeatGridCloudDocument Decode(string json)
    {
        var document = JsonSerializer.Deserialize<SeatGridCloudDocument>(json, Options)
                       ?? throw new SeatGridException("云端座位表内容为空，无法读取。");

        if (document.V > SeatGridCloudMapper.CurrentVersion)
            throw new SeatGridException(
                $"这份云端座位表的版本（v{document.V}）比本软件支持的版本（v{SeatGridCloudMapper.CurrentVersion}）更新，请升级知微后再打开。");

        return document;
    }
}
