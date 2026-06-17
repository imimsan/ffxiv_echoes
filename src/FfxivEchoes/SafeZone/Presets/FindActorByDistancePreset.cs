using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 距離による選択（SPEC.md §6.1）。
/// params: { select: "nearest" or "farthest", actor_filter: enemy/ally/any,
///           name_contains: 名前フィルタ（任意）, status_id: ステータス条件（任意）,
///           reference: "self" or "boss" — 距離の起点（既定 self） }
/// </summary>
public sealed class FindActorByDistancePreset : ISafeZonePreset
{
    public string Method => "find_actor_by_distance";

    private readonly IObjectTable _objectTable;

    public FindActorByDistancePreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var select = (ParamHelper.GetString(calc.Params, "select") ?? "nearest").ToLowerInvariant();
        var filter = ParamHelper.GetString(calc.Params, "actor_filter") ?? "any";
        var nameContains = ParamHelper.GetString(calc.Params, "name_contains");
        var statusId = ParamHelper.GetInt(calc.Params, "status_id");
        var reference = ParamHelper.GetString(calc.Params, "reference") ?? "self";

        var refPos = reference == "boss" && ctx.Boss is { } b
            ? new Vector3(b.Position.X, b.Position.Y, b.Position.Z)
            : ctx.SelfPosition;

        // 自分自身を探索対象から除外する。reference=self + select=nearest + filter=any（既定）で
        // 距離0の自分が常に選ばれてしまう退行を防ぐ。
        var selfId = _objectTable.LocalPlayer?.GameObjectId;

        IBattleChara? best = null;
        var bestDist = select == "nearest" ? float.MaxValue : float.MinValue;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleChara chara)
            {
                continue;
            }
            if (selfId is { } selfGid && chara.GameObjectId == selfGid)
            {
                continue;
            }
            if (!FilterAccepts(chara, filter))
            {
                continue;
            }
            if (nameContains is not null && !chara.Name.TextValue.Contains(nameContains))
            {
                continue;
            }
            if (statusId is { } sid && !HasStatus(chara, sid))
            {
                continue;
            }

            var pos = new Vector3(chara.Position.X, chara.Position.Y, chara.Position.Z);
            var dx = pos.X - refPos.X;
            var dz = pos.Z - refPos.Z;
            var distSq = dx * dx + dz * dz;

            var better = select == "nearest" ? distSq < bestDist : distSq > bestDist;
            if (better)
            {
                bestDist = distSq;
                best = chara;
            }
        }

        if (best is null)
        {
            return null;
        }

        var resultPos = new Vector3(best.Position.X, best.Position.Y, best.Position.Z);
        return new SafeZoneResult(resultPos, DirectionInfo.Compute(ctx.SelfPosition, resultPos));
    }

    private static bool HasStatus(IBattleChara chara, int statusId)
    {
        foreach (var status in chara.StatusList)
        {
            if (status?.StatusId == statusId)
            {
                return true;
            }
        }
        return false;
    }

    private static bool FilterAccepts(IBattleChara chara, string filter) => filter.ToLowerInvariant() switch
    {
        "enemy" => chara is IBattleNpc bn && bn.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Pet,
        "ally" => chara is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter,
        _ => true,
    };
}
