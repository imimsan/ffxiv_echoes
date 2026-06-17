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

        var bossPos = new Vector3(ctx.Boss.Position.X, ctx.Boss.Position.Y, ctx.Boss.Position.Z);
        var pos = bossPos + ComputeOffset(ctx.Boss.Rotation, angleDeg, distance);

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    /// <summary>
    /// ボス相対角からワールド オフセットを計算する純粋関数。
    /// FFXIV: Rotation=0 は +Z（南）向き・+X=東。南を向くと右手は西(-X)なので、
    /// 「正面=0 / 右=90 / 後=180 / 左=270」を満たすには bossFacing から angle を引く（時計回り）。
    /// 以前は加算しており angle=90 がボスの「左」を指していた（安置が逆側になる重大バグ）。
    /// </summary>
    public static Vector3 ComputeOffset(float bossFacingRad, float angleDeg, float distance)
    {
        var rad = bossFacingRad - angleDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(rad) * distance, 0f, MathF.Cos(rad) * distance);
    }
}
