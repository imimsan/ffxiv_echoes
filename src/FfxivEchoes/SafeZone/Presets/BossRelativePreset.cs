using System;
using System.Numerics;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// ボス相対プリセット（SPEC.md §6.1）。
/// params: { angle: 度数（ボス向き正面=0、右=90、後=180、左=270）, distance: 数値 }
/// </summary>
public sealed class BossRelativePreset : ISafeZonePreset
{
    public string Method => "boss_relative";

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        if (ctx.Boss is null)
        {
            return null;
        }
        var angleDeg = ParamHelper.GetFloat(calc.Params, "angle") ?? 180f;
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 10f;

        // ボスの向き（Rotation はラジアン、+Z 方向が rotation=0、CCW で正）
        var bossPos = new Vector3(ctx.Boss.Position.X, ctx.Boss.Position.Y, ctx.Boss.Position.Z);
        var bossFacing = ctx.Boss.Rotation;

        // ボス基準角を world 角に変換
        var rad = bossFacing + angleDeg * MathF.PI / 180f;
        // FFXIV：rotation=0 は +Z（南）方向。"前" は +Z、"右" は +X、… それを反映
        var dx = MathF.Sin(rad) * distance;
        var dz = MathF.Cos(rad) * distance;
        var pos = bossPos + new Vector3(dx, 0, dz);

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }
}
