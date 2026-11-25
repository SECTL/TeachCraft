using System.Text.Json.Serialization;

namespace TeachCraft.Core.Cloud;

/// <summary>
/// 共享名单文档（冻结 schema v1，文档类型 <c>teachcraft.roster.v1</c>）。
/// <para>
/// 这是**数据契约**，不含任何 HTTP / 存储实现：一份文档对应一个 KV，索引另存一份。
/// 必填字段是姓名 / 性别 / 学号；可选字段默认关闭，未勾选的值必须是 null，
/// 由 <see cref="Fields"/> 记录上传者实际勾选了哪些字段。
/// </para>
/// </summary>
public sealed class SharedRosterDocument
{
    [JsonPropertyName("v")]
    public int V { get; set; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "teachcraft.roster.v1";

    [JsonPropertyName("roster_id")]
    public string RosterId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonPropertyName("fields")]
    public RosterFieldPresence Fields { get; set; } = RosterFieldPresence.None;

    [JsonPropertyName("students")]
    public List<SharedRosterStudent> Students { get; set; } = new();
}

/// <summary>共享名单里的一名学生。Index 是 1 基序号，与姓名一起构成约束的持久键。</summary>
public sealed class SharedRosterStudent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("gender")]
    public string Gender { get; set; } = "未知";

    [JsonPropertyName("number")]
    public string Number { get; set; } = "";

    [JsonPropertyName("height")]
    public double? Height { get; set; }

    [JsonPropertyName("vision")]
    public double? Vision { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("original_group")]
    public string? OriginalGroup { get; set; }
}

/// <summary>
/// 文档里实际存在的可选字段。默认 <see cref="None"/>（最小披露）。
/// <see cref="PresentFieldNames"/> 用规范小写键，与归位侧的 FieldsPresent 一致。
/// </summary>
public sealed record RosterFieldPresence(
    [property: JsonPropertyName("height")] bool Height,
    [property: JsonPropertyName("vision")] bool Vision,
    [property: JsonPropertyName("score")] bool Score,
    [property: JsonPropertyName("notes")] bool Notes,
    [property: JsonPropertyName("original_group")] bool OriginalGroup)
{
    public static RosterFieldPresence None { get; } = new(false, false, false, false, false);

    public static RosterFieldPresence All { get; } = new(true, true, true, true, true);

    /// <summary>未勾选字段的中文标签，按固定顺序，供提示与校验文案使用。</summary>
    public IReadOnlyList<string> MissingOptionalFields
    {
        get
        {
            var labels = new List<string>(5);
            if (!Height) labels.Add("身高");
            if (!Vision) labels.Add("视力");
            if (!Score) labels.Add("成绩");
            if (!Notes) labels.Add("备注");
            if (!OriginalGroup) labels.Add("原小组");
            return labels;
        }
    }

    /// <summary>已勾选字段的规范小写键，按固定顺序。</summary>
    public IReadOnlyList<string> PresentFieldNames
    {
        get
        {
            var names = new List<string>(5);
            if (Height) names.Add("height");
            if (Vision) names.Add("vision");
            if (Score) names.Add("score");
            if (Notes) names.Add("notes");
            if (OriginalGroup) names.Add("originalGroup");
            return names;
        }
    }
}

/// <summary>上传者选择发布哪些可选字段。默认 <see cref="Minimal"/>（全关）。</summary>
public sealed record RosterFieldSelection(
    bool Height, bool Vision, bool Score, bool Notes, bool OriginalGroup)
{
    public static RosterFieldSelection Minimal { get; } = new(false, false, false, false, false);

    public RosterFieldPresence ToPresence()
        => new(Height, Vision, Score, Notes, OriginalGroup);

    public static RosterFieldSelection FromPresence(RosterFieldPresence presence)
        => new(presence.Height, presence.Vision, presence.Score, presence.Notes, presence.OriginalGroup);
}
