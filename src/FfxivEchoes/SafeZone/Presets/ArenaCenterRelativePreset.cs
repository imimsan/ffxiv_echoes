using System;
using System.Numerics;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 戦闘エリア中心相対プリセット（SPEC.md §6.1）。
/// 「外周指定」などに使う。params: { direction: 8 方位, distance: 数値 }
/// アリーナ中心はボス位置で代用（多くのアリーナはボス開始地点が中心）。
/// </summary>
public sealed class ArenaCenterRelativePreset : ISafeZonePreset
{
    public string Method => "arena_center_relative";

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var directionStr = ParamHelper.GetString(calc.Params, "direction") ?? "N";
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 18f;

        var center = ctx.ArenaCenter;

        var angleDeg = ParseDirection(directionStr);
        var angleRad = angleDeg * MathF.PI / 180f;
        var dx = MathF.Sin(angleRad) * distance;
        var dz = -MathF.Cos(angleRad) * distance;
        var pos = center + new Vector3(dx, 0, dz);

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    private static float ParseDirection(string s) => s.Trim().ToUpperInvariant() switch
    {
        "N" or "NORTH" or "北" => 0,
        "NE" or "北東" => 45,
        "E" or "EAST" or "東" => 90,
        "SE" or "南東" => 135,
        "S" or "SOUTH" or "南" => 180,
        "SW" or "南西" => 225,
        "W" or "WEST" or "西" => 270,
        "NW" or "北西" => 315,
        _ when float.TryParse(s, out var deg) => deg,
        _ => 0,
    };
}
