using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using SeatGrid.Core.Models;

namespace SeatGrid.Core.Export;

/// <summary>画布在「座位设置」表里存的 JSON 形状。短字段名是为了压体积。</summary>
internal sealed record SeatChartCanvasDto
{
    public int V { get; init; } = 1;

    /// <summary>过道格：[group, row, column]，全部 0 基。</summary>
    public List<int[]> Aisles { get; init; } = new();

    public float[] RowGaps { get; init; } = Array.Empty<float>();
    public float[] ColumnGaps { get; init; } = Array.Empty<float>();
    public float[] GroupGaps { get; init; } = Array.Empty<float>();

    public List<RoomDoorDto> Doors { get; init; } = new();

    internal sealed record RoomDoorDto(string Edge, double Ratio, string Label);
}

/// <summary>画布与 JSON 的互转。用 System.Text.Json，未知字段忽略、缺字段走默认值。</summary>
internal static class SeatChartCanvasCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static string Encode(SeatChartCanvas canvas)
    {
        if (canvas.IsEmpty) return string.Empty;

        var dto = new SeatChartCanvasDto
        {
            Aisles = canvas.DisabledSeats
                .OrderBy(s => s.Group).ThenBy(s => s.Row).ThenBy(s => s.Column)
                .Select(s => new[] { s.Group, s.Row, s.Column })
                .ToList(),
            RowGaps = canvas.RowCorridors.IsDefaultOrEmpty ? Array.Empty<float>() : canvas.RowCorridors.ToArray(),
            ColumnGaps = canvas.ColumnCorridors.IsDefaultOrEmpty
                ? Array.Empty<float>()
                : canvas.ColumnCorridors.ToArray(),
            GroupGaps = canvas.GroupCorridors.IsDefaultOrEmpty ? Array.Empty<float>() : canvas.GroupCorridors.ToArray(),
            Doors = canvas.Doors.IsDefaultOrEmpty
                ? new List<SeatChartCanvasDto.RoomDoorDto>()
                : canvas.Doors.Select(d => new SeatChartCanvasDto.RoomDoorDto(d.Edge.ToString(), d.OffsetRatio, d.Label))
                    .ToList(),
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>解析失败一律回落空画布——画布是增强，不能因为一段坏 JSON 让配置打不开。</summary>
    public static SeatChartCanvas Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return SeatChartCanvas.Empty;

        try
        {
            var dto = JsonSerializer.Deserialize<SeatChartCanvasDto>(json, Options);
            if (dto is null) return SeatChartCanvas.Empty;

            var seats = new HashSet<SeatPosition>();
            foreach (var aisle in dto.Aisles)
            {
                if (aisle.Length < 3) continue;
                seats.Add(new SeatPosition(aisle[0], aisle[1], aisle[2]));
            }

            var doors = ImmutableArray.CreateBuilder<RoomDoor>();
            foreach (var door in dto.Doors)
            {
                if (!Enum.TryParse<RoomEdge>(door.Edge, ignoreCase: true, out var edge)) continue;
                doors.Add(new RoomDoor(edge, door.Ratio, door.Label ?? string.Empty));
            }

            return new SeatChartCanvas
            {
                DisabledSeats = seats.ToFrozenSet(),
                RowCorridors = dto.RowGaps.ToImmutableArray(),
                ColumnCorridors = dto.ColumnGaps.ToImmutableArray(),
                GroupCorridors = dto.GroupGaps.ToImmutableArray(),
                Doors = doors.ToImmutable(),
            };
        }
        catch (JsonException)
        {
            return SeatChartCanvas.Empty;
        }
    }
}
