using System.Text.Json.Nodes;

namespace TeachCraft.Core.Control;

/// <summary>
/// 归位座位表只读摘要（严格不含学生姓名、学号、座次姓名列表等 PII）。
/// </summary>
public sealed record SeatGridReadSummary(
    bool IsOpen,
    bool IsExam,
    string View,
    int StudentCount,
    int AssignedCount,
    int SeatCount,
    int Groups,
    int Rows,
    int Columns,
    bool Projecting,
    string Title);

/// <summary>时序课程表只读摘要（严格不含教师姓名或学生名单）。</summary>
public sealed record ClassBlockReadSummary(
    bool IsOpen,
    bool HasMaster,
    string Grade,
    string Term,
    int ClassCount,
    string? SelectedClass,
    IReadOnlyList<string> Classes);

/// <summary>织序全年级排课只读摘要（按冲突类型汇总计数，不返回教师姓名或单节课明细）。</summary>
public sealed record GradeWeaveReadSummary(
    bool IsOpen,
    bool HasProject,
    string ProjectTitle,
    int ClassCount,
    IReadOnlyList<string> Classes,
    int ConflictTotal,
    int TeacherClash,
    int ClassClash,
    int TeacherUnavailable,
    int LayeredClash,
    int MissingTeacher,
    int PeriodMismatch,
    int Unplaced);

/// <summary>
/// 由 UI 宿主实现的集控动作执行器；实现方负责切回 UI 线程（Dispatcher.UIThread）操作窗口。
/// </summary>
public interface ITeachCraftControlHost
{
    Task<string> OpenSeatGridAsync(string? view, CancellationToken ct);
    Task<string> SetSeatGridViewAsync(string view, CancellationToken ct);
    Task<bool> SetSeatGridProjectionAsync(bool on, CancellationToken ct);
    Task<SeatGridReadSummary> SetSeatGridExamAsync(bool on, int columns, CancellationToken ct);
    Task<string?> ShowClassBlockAsync(string? className, CancellationToken ct);
    Task<SeatGridReadSummary> ReadSeatGridAsync(CancellationToken ct);
    Task<ClassBlockReadSummary> ReadClassBlockAsync(CancellationToken ct);
    Task<GradeWeaveReadSummary> ReadGradeWeaveAsync(CancellationToken ct);
}

/// <summary>
/// 把 TeachCraft 支持的 8 条集控指令注册到 <see cref="ControlCenterService"/>，
/// 做参数校验并在后台异步执行 + 通过 cmd.result 回执（绝不阻塞 WebSocket 接收循环）。
/// </summary>
public sealed class TeachCraftControlDispatcher
{
    private readonly ControlCenterService _service;
    private readonly ITeachCraftControlHost _host;

    public TeachCraftControlDispatcher(ControlCenterService service, ITeachCraftControlHost host)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public void RegisterAll()
    {
        _service.RegisterHandler("seatgrid.open", HandleSeatGridOpen);
        _service.RegisterHandler("seatgrid.view", HandleSeatGridView);
        _service.RegisterHandler("seatgrid.project", HandleSeatGridProject);
        _service.RegisterHandler("seatgrid.exam", HandleSeatGridExam);
        _service.RegisterHandler("classblock.show", HandleClassBlockShow);
        _service.RegisterHandler("seatgrid.read", HandleSeatGridRead);
        _service.RegisterHandler("classblock.read", HandleClassBlockRead);
        _service.RegisterHandler("gradeweave.read", HandleGradeWeaveRead);
    }

    private object HandleSeatGridOpen(CommandInvocation inv)
    {
        string? view = null;
        if (inv.Args.TryGetPropertyValue("view", out var viewNode) && viewNode is not null)
        {
            var raw = JsonHelp.Str(inv.Args, "view").Trim().ToLowerInvariant();
            if (raw.Length > 0)
            {
                if (raw is not ("teacher" or "student"))
                    return FailArgs("view 仅支持 teacher 或 student");
                view = raw;
            }
        }

        return RunDeferred(inv, async ct =>
        {
            var activeView = await _host.OpenSeatGridAsync(view, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ack"] = "seatgrid.open",
                ["opened"] = true,
                ["view"] = activeView,
            };
        });
    }

    private object HandleSeatGridView(CommandInvocation inv)
    {
        var view = JsonHelp.Str(inv.Args, "view").Trim().ToLowerInvariant();
        if (view is not ("teacher" or "student"))
            return FailArgs("seatgrid.view 需要传入 view=teacher 或 student");

        return RunDeferred(inv, async ct =>
        {
            var activeView = await _host.SetSeatGridViewAsync(view, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ack"] = "seatgrid.view",
                ["view"] = activeView,
            };
        });
    }

    private object HandleSeatGridProject(CommandInvocation inv)
    {
        if (!JsonHelp.TryGetBoolArg(inv.Args, "on", out var on))
            return FailArgs("seatgrid.project 需要布尔参数 on");

        return RunDeferred(inv, async ct =>
        {
            var projecting = await _host.SetSeatGridProjectionAsync(on, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ack"] = "seatgrid.project",
                ["projecting"] = projecting,
            };
        });
    }

    private object HandleSeatGridExam(CommandInvocation inv)
    {
        if (!JsonHelp.TryGetBoolArg(inv.Args, "on", out var on))
            return FailArgs("seatgrid.exam 需要布尔参数 on");

        var columns = 8;
        if (inv.Args.ContainsKey("columns"))
        {
            if (!JsonHelp.TryGetIntArg(inv.Args, "columns", out columns) || columns is < 4 or > 8)
                return FailArgs("考场列数 columns 须在 4 至 8 之间");
        }

        return RunDeferred(inv, async ct =>
        {
            var summary = await _host.SetSeatGridExamAsync(on, columns, ct).ConfigureAwait(false);
            var json = ToSeatGridJson(summary);
            json["ack"] = "seatgrid.exam";
            return json;
        });
    }

    private object HandleClassBlockShow(CommandInvocation inv)
    {
        var className = JsonHelp.Str(inv.Args, "class_name").Trim();
        if (className.Length > 20)
            return FailArgs("class_name 长度不能超过 20 个字符");

        return RunDeferred(inv, async ct =>
        {
            var selected = await _host.ShowClassBlockAsync(className.Length > 0 ? className : null, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ack"] = "classblock.show",
                ["opened"] = true,
                ["selected_class"] = selected,
            };
        });
    }

    private object HandleSeatGridRead(CommandInvocation inv)
    {
        return RunDeferred(inv, async ct =>
        {
            var summary = await _host.ReadSeatGridAsync(ct).ConfigureAwait(false);
            return ToSeatGridJson(summary);
        });
    }

    private object HandleClassBlockRead(CommandInvocation inv)
    {
        return RunDeferred(inv, async ct =>
        {
            var summary = await _host.ReadClassBlockAsync(ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["is_open"] = summary.IsOpen,
                ["has_master"] = summary.HasMaster,
                ["grade"] = summary.Grade,
                ["term"] = summary.Term,
                ["class_count"] = summary.ClassCount,
                ["selected_class"] = summary.SelectedClass,
                ["classes"] = new JsonArray(summary.Classes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            };
        });
    }

    private object HandleGradeWeaveRead(CommandInvocation inv)
    {
        return RunDeferred(inv, async ct =>
        {
            var summary = await _host.ReadGradeWeaveAsync(ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["is_open"] = summary.IsOpen,
                ["has_project"] = summary.HasProject,
                ["project_title"] = summary.ProjectTitle,
                ["class_count"] = summary.ClassCount,
                ["classes"] = new JsonArray(summary.Classes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["conflict_total"] = summary.ConflictTotal,
                ["conflict_counts"] = new JsonObject
                {
                    ["teacher_clash"] = summary.TeacherClash,
                    ["class_clash"] = summary.ClassClash,
                    ["teacher_unavailable"] = summary.TeacherUnavailable,
                    ["layered_clash"] = summary.LayeredClash,
                    ["missing_teacher"] = summary.MissingTeacher,
                    ["period_mismatch"] = summary.PeriodMismatch,
                    ["unplaced"] = summary.Unplaced,
                },
            };
        });
    }

    private static JsonObject ToSeatGridJson(SeatGridReadSummary summary) => new()
    {
        ["is_open"] = summary.IsOpen,
        ["is_exam"] = summary.IsExam,
        ["view"] = summary.View,
        ["student_count"] = summary.StudentCount,
        ["assigned_count"] = summary.AssignedCount,
        ["seat_count"] = summary.SeatCount,
        ["groups"] = summary.Groups,
        ["rows"] = summary.Rows,
        ["columns"] = summary.Columns,
        ["projecting"] = summary.Projecting,
        ["title"] = summary.Title,
    };

    private static object FailArgs(string message) => throw new ControlException("bad_args", message);

    private object RunDeferred(CommandInvocation inv, Func<CancellationToken, Task<JsonObject>> execute)
    {
        var timeoutMs = Math.Clamp(inv.DeadlineMs - 250, 500, 4500);
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var result = await execute(cts.Token).ConfigureAwait(false);
                if (_service.Client is { } client)
                    await client.ReportAsync(inv.CommandId, true, result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_service.Client is { } client)
                    await client.ReportAsync(inv.CommandId, false, new JsonObject { ["cmd"] = inv.Cmd }, "timeout", "窗口响应超时").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_service.Client is { } client)
                    await client.ReportAsync(inv.CommandId, false, new JsonObject { ["cmd"] = inv.Cmd }, "failed", ex.Message).ConfigureAwait(false);
            }
        });

        return DeferredReply.Instance;
    }
}
