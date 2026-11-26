using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeachCraft.Core.Control;

/// <summary>与 SECTL Control Hub v4 对齐的 TeachCraft 集控协议常量与只读能力集合。</summary>
public static class SectlControlProtocol
{
    public const int ProtocolVersion = 4;
    public const string PlatformName = "teachcraft";
    public const string WsPath = "/control/ws";

    public const int DefaultCommandTimeoutMs = 5000;
    public const int DefaultHeartbeatSeconds = 25;
    public const int DefaultOfflineAfterSeconds = 75;
    public const int CommandCacheSize = 512;
    public const double BackoffMaxSeconds = 60.0;

    /// <summary>
    /// TeachCraft 允许声明的全部 8 个能力。
    /// 严禁出现 roster.* / draw.* / grades* / meetroster.*。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCapabilities = new[]
    {
        "seatgrid.open",
        "seatgrid.view",
        "seatgrid.project",
        "seatgrid.exam",
        "classblock.show",
        "seatgrid.read",
        "classblock.read",
        "gradeweave.read",
    };

    public static readonly IReadOnlyList<string> NoRetryCodes = new[]
    {
        "protocol_too_old",
        "bad_secret",
        "device_revoked",
        "not_authed",
    };
}

public enum ControlState
{
    Unconfigured,
    Configured,
    Connecting,
    Connected,
    Selected,
    Superseded,
    Revoked,
    Stopped,
}

public static class ControlStateExtensions
{
    public static string WireName(this ControlState state) => state switch
    {
        ControlState.Unconfigured => "unconfigured",
        ControlState.Configured => "configured",
        ControlState.Connecting => "connecting",
        ControlState.Connected => "connected",
        ControlState.Selected => "selected",
        ControlState.Superseded => "superseded",
        ControlState.Revoked => "revoked",
        _ => "stopped",
    };
}

public class ControlException : Exception
{
    public ControlException(string code, string message = "", JsonObject? payload = null, Exception? inner = null)
        : base(message.Length > 0 ? message : code, inner)
    {
        Code = code;
        Detail = message;
        Payload = payload is null ? new JsonObject() : JsonHelp.CloneObject(payload);
    }

    public string Code { get; }
    public string Detail { get; }
    public JsonObject Payload { get; }
}

public sealed class UnsupportedCommandException : ControlException
{
    public UnsupportedCommandException(string message = "客户端不支持这个指令")
        : base("unsupported", message)
    {
    }
}

public sealed class CommandInvocation
{
    public string Id { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string Cmd { get; init; } = string.Empty;
    public JsonObject Args { get; init; } = new();
    public string Issuer { get; init; } = string.Empty;
    public int DeadlineMs { get; init; } = SectlControlProtocol.DefaultCommandTimeoutMs;
    public long ExpiresAt { get; init; }
}

public sealed class DeferredReply
{
    private DeferredReply() { }
    public static DeferredReply Instance { get; } = new();
}

/// <summary>已登录 SECTL 账号可见的地点摘要（GET /control/api/sites）。</summary>
public sealed record ControlSiteSummary(
    string Id,
    string PublicId,
    string Name,
    string Role,
    string RoleText,
    int DeviceCount)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(PublicId) ? Name : $"{Name}（{PublicId}）";
}

public sealed record ControlTreeClass(string Id, string Name);

public sealed record ControlTreeGrade(string Id, string Name, IReadOnlyList<ControlTreeClass> Classes);

/// <summary>POST /control/api/device/tree 返回的地点结构树（不含成员与学生）。</summary>
public sealed record ControlTreeSnapshot(
    string SitePublicId,
    string SiteName,
    string DeviceId,
    string DeviceName,
    string AssignedClassId,
    IReadOnlyList<ControlTreeGrade> Grades);

/// <summary>POST /control/api/device/seats 返回的座位布局摘要（仅几何与座签，不含学生姓名）。</summary>
public sealed record ControlDeviceSeatsSnapshot(
    bool ClassRequired,
    string ClassId,
    string ClassName,
    int Rows,
    int Columns,
    string Source,
    int LabelCount);

/// <summary>v4 双因子接入码与错误文案翻译。</summary>
public static class SectlEnroll
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string NormalizeSiteId(string? raw)
    {
        var s = Normalize(raw);
        if (s.Length == 6) s = "SC" + s;
        if (s.Length != 8 || !s.StartsWith("SC", StringComparison.Ordinal)) return string.Empty;
        return s;
    }

    public static string NormalizePairCode(string? raw)
    {
        var s = Normalize(raw);
        if (s.Length != 8) return string.Empty;
        foreach (var ch in s)
        {
            if (Alphabet.IndexOf(ch) < 0) return string.Empty;
        }

        return s;
    }

    public static string FormatSiteId(string? normalized)
    {
        var s = Normalize(normalized);
        if (s.Length == 6) return "SC-" + s;
        return s.Length == 8 ? s[..2] + "-" + s[2..] : s;
    }

    public static string FormatCode(string? normalized)
    {
        var s = Normalize(normalized);
        return s.Length == 8 ? s[..4] + "-" + s[4..] : s;
    }

    private static string Normalize(string? raw)
    {
        if (raw is null) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is >= '0' and <= '9') sb.Append(ch);
            else if (ch is >= 'a' and <= 'z') sb.Append((char)(ch - 32));
            else if (ch is >= 'A' and <= 'Z') sb.Append(ch);
        }

        return sb.ToString();
    }

    public static string DescribeError(string code, JsonObject? detail)
    {
        var d = detail ?? new JsonObject();
        switch (code)
        {
            case "bad_request":
                return "接入信息不完整，请核对地点ID、配对码或参数格式。";
            case "site_not_found":
                return "找不到这个地点，请核对地点ID（形如 SC-6M34RQ）。";
            case "code_not_found":
                return "配对码不存在，请到 SECTL 网页端重新生成。";
            case "code_expired":
                return "配对码已过期，请到 SECTL 网页端重新生成。";
            case "code_used":
                return "配对码已经使用过了（一个配对码只能接入一台设备）。";
            case "code_revoked":
                return "配对码已被管理员撤销。";
            case "code_site_mismatch":
                return "这个配对码不属于该地点，请核对地点ID。";
            case "platform_not_allowed":
                return "该配对码或地点当前未允许「TeachCraft」平台接入。";
            case "not_authed":
                return "SECTL 登录会话已失效或服务端未开通该登录令牌，请重新登录或改用地点ID + 配对码接入。";
            case "permission_denied":
            case "forbidden":
                return "当前账号在该地点没有接入设备的权限（需成员或管理员角色）。";
            case "device_already_enrolled":
                return "这台设备已经接入该地点；普通成员不可覆盖，请使用现有凭据或在网页端管理该设备。";
            case "device_conflict":
            {
                var siteName = JsonHelp.Str(d, "siteName");
                return siteName.Length > 0
                    ? $"这台机器已经接在「{siteName}」了，请先在 SECTL 网页端或使用清除接入移除旧设备。"
                    : "这台机器已经接入了另一个地点，请先移除旧设备再重新接入。";
            }
            case "too_many_requests":
            {
                var retry = JsonHelp.Num(d, "retryAfter");
                return retry > 0 ? $"尝试次数过多，请 {retry:0} 秒后再试。" : "尝试次数过多，请稍后再试。";
            }
            default:
                return $"集控操作失败（{code}）。";
        }
    }
}

internal static class JsonHelp
{
    public static JsonObject AsObject(JsonNode? node) => node as JsonObject ?? new JsonObject();

    public static JsonArray AsArray(JsonNode? node) => node as JsonArray ?? new JsonArray();

    public static JsonNode? Clone(JsonNode? node)
        => node is null ? null : JsonNode.Parse(node.ToJsonString());

    public static JsonObject CloneObject(JsonObject? obj)
        => obj is null ? new JsonObject() : (JsonNode.Parse(obj.ToJsonString()) as JsonObject ?? new JsonObject());

    public static string Str(JsonObject? obj, string key, string fallback = "")
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s ?? fallback;
        return node.ToString();
    }

    public static bool Bool(JsonObject? obj, string key, bool fallback = false)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<int>(out var i)) return i != 0;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        }

        return fallback;
    }

    public static int Int(JsonObject? obj, string key, int fallback = 0)
        => checked((int)Long(obj, key, fallback));

    public static long Long(JsonObject? obj, string key, long fallback = 0)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return fallback;
    }

    public static double Num(JsonObject? obj, string key, double fallback = 0)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return fallback;
    }

    public static void Merge(JsonObject target, JsonObject? source)
    {
        if (source is null) return;
        foreach (var kv in source)
            target[kv.Key] = Clone(kv.Value);
    }

    public static bool TryGetBoolArg(JsonObject args, string key, out bool value)
    {
        value = false;
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return false;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b))
        {
            value = b;
            return true;
        }

        if (node is JsonValue sv && sv.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    public static bool TryGetIntArg(JsonObject args, string key, out int value)
    {
        value = 0;
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return false;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i))
            {
                value = i;
                return true;
            }

            if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)l;
                return true;
            }

            if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }
}
