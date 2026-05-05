using System;
using System.Numerics;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 複合制約（SPEC.md §6.4）。複数の SafeZoneCalculation を実行し、
/// それらの結果の重心を返す（簡易実装）。
/// </summary>
/// <remarks>
/// 真の意味の幾何 intersection（領域同士の重なり）はサポートしない。
/// 各制約が単一点を返す前提で、それらの中心 = 妥協点を返す。
/// 単一制約の時はそのまま、矛盾時（全制約 null）は null を返す。
/// </remarks>
public sealed class IntersectionPreset : ISafeZonePreset
{
    public string Method => "intersection";

    private readonly Func<SafeZoneCalculation, SafeZoneContext, SafeZoneResult?> _calculate;

    public IntersectionPreset(Func<SafeZoneCalculation, SafeZoneContext, SafeZoneResult?> calculate)
    {
        _calculate = calculate;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        if (calc.Constraints is null || calc.Constraints.Count == 0)
        {
            return null;
        }

        var sum = Vector3.Zero;
        var count = 0;
        SafeZoneResult? lastValid = null;

        foreach (var sub in calc.Constraints)
        {
            var result = _calculate(sub, ctx);
            if (result is null)
            {
                continue;
            }
            sum += result.WorldPosition;
            count++;
            lastValid = result;
        }

        if (count == 0)
        {
            return null;
        }

        var pos = count == 1 ? lastValid!.WorldPosition : sum / count;
        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }
}
