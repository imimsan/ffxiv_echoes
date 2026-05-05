using System.Numerics;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// AoE 予兆の反対方向プリセット（SPEC.md §6.1）。
/// params: { telegraph_source: アクター指定, distance: 数値 }
/// 簡易実装：cast 中のアクター位置から自分への方向ベクトルを反転して返す。
/// 実際の telegraph 形状（扇/直線/ドーナツ）の解析は F7（telegraph_gap）で対応。
/// </summary>
public sealed class InverseOfTelegraphPreset : ISafeZonePreset
{
    public string Method => "inverse_of_telegraph";

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 12f;

        var actor = ctx.CastActor ?? (ctx.Boss is { } b ? b : null);
        if (actor is null)
        {
            return null;
        }

        var actorPos = new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
        var fromActor = ctx.SelfPosition - actorPos;
        var len = fromActor.Length();
        if (len < 0.01f)
        {
            return null;
        }
        var dir = fromActor / len;
        var pos = actorPos + dir * distance;
        // この pos は「アクターから見て自分側の方向にさらに離れた位置」＝AoE の反対。
        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }
}
