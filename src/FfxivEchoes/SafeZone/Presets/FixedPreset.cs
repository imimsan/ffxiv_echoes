using System;
using System.Numerics;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 固定方角プリセット（SPEC.md §6.1）。
/// params: { direction: "N"/"NE"/.../"north"/"south"/..., distance: 数値,
///          origin: "self"/"boss"/"arena_center" — 既定 self }
/// </summary>
public sealed class FixedPreset : ISafeZonePreset
{
    public string Method => "fixed";

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var directionStr = ParamHelper.GetString(calc.Params, "direction") ?? "N";
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 10f;
        var origin = ParamHelper.GetString(calc.Params, "origin") ?? "self";

        var basePos = origin switch
        {
            "boss" when ctx.Boss is { } b => new Vector3(b.Position.X, b.Position.Y, b.Position.Z),
            "arena_center" => ctx.ArenaCenter,
            _ => ctx.SelfPosition,
        };

        var angleDeg = ParseDirection(directionStr);
        var angleRad = angleDeg * MathF.PI / 180f;
        // FFXIV：北 = -Z 方向
        var dx = MathF.Sin(angleRad) * distance;
        var dz = -MathF.Cos(angleRad) * distance;
        var pos = basePos + new Vector3(dx, 0, dz);

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    private static float ParseDirection(string s)
    {
        var trimmed = s.Trim().ToUpperInvariant();
        return trimmed switch
        {
            "N" or "NORTH" or "北" => 0,
            "NE" or "NORTHEAST" or "北東" => 45,
            "E" or "EAST" or "東" => 90,
            "SE" or "SOUTHEAST" or "南東" => 135,
            "S" or "SOUTH" or "南" => 180,
            "SW" or "SOUTHWEST" or "南西" => 225,
            "W" or "WEST" or "西" => 270,
            "NW" or "NORTHWEST" or "北西" => 315,
            _ when float.TryParse(trimmed, out var deg) => deg,
            _ => 0,
        };
    }
}
