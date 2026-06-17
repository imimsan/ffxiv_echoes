using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 特定ステータスを持たないアクターを検索（SPEC.md §6.1）。
/// params: { status_id: ステータス ID, actor_filter: enemy/ally/any（既定 any）,
///           name_contains: 名前に含まれる文字列（任意）, npc_base_id: DataId 絞り込み（任意）,
///           select: nearest/farthest（候補が複数残ったときの選択。既定なし） }
/// </summary>
/// <remarks>
/// 塔反転（手を上げてない＝ステータス無しのボスが安置）系で、無ステータス敵が複数同時に
/// 存在する多体フェーズでは「最初に見つけた 1 体」を返すと別の塔/アクターを指して全滅しうる。
/// そこで候補を全列挙し、(1) <c>select</c> 指定があれば nearest/farthest で一意化、
/// (2) それでも複数残れば <b>null（無音）</b> を返してフェイルセーフ（誤コールより沈黙）。
/// </remarks>
public sealed class FindActorWithoutStatusPreset : ISafeZonePreset
{
    public string Method => "find_actor_without_status";

    private readonly IObjectTable _objectTable;

    public FindActorWithoutStatusPreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var statusId = ParamHelper.GetInt(calc.Params, "status_id");
        if (statusId is null)
        {
            return null;
        }
        var filter = ParamHelper.GetString(calc.Params, "actor_filter") ?? "any";
        var nameContains = ParamHelper.GetString(calc.Params, "name_contains");
        var npcBaseId = ParamHelper.GetInt(calc.Params, "npc_base_id");
        var select = ParamHelper.GetString(calc.Params, "select")?.ToLowerInvariant();

        var candidates = new List<IBattleChara>();
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleChara chara)
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
            if (npcBaseId is not null && chara.BaseId != (uint)npcBaseId.Value)
            {
                continue;
            }
            if (HasStatus(chara, statusId.Value))
            {
                continue;
            }
            candidates.Add(chara);
        }

        var chosen = SelectCandidate(candidates, select, ctx.SelfPosition);
        if (chosen is null)
        {
            return null;
        }
        var pos = new Vector3(chosen.Position.X, chosen.Position.Y, chosen.Position.Z);
        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    /// <summary>
    /// 候補から 1 体を選ぶ。0 件 → null。1 件 → それ。複数 → select があれば nearest/farthest、
    /// 無ければ null（曖昧フェイルセーフ）。
    /// </summary>
    private static IBattleChara? SelectCandidate(
        IReadOnlyList<IBattleChara> candidates, string? select, Vector3 self)
    {
        if (candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count == 1)
        {
            return candidates[0];
        }
        if (select is not "nearest" and not "farthest")
        {
            // 候補が複数で選択基準が無い → 誤指しを避けて無音にする。
            return null;
        }

        IBattleChara? best = null;
        var bestDistSq = select == "nearest" ? float.MaxValue : float.MinValue;
        foreach (var chara in candidates)
        {
            var dx = chara.Position.X - self.X;
            var dz = chara.Position.Z - self.Z;
            var distSq = dx * dx + dz * dz;
            var better = select == "nearest" ? distSq < bestDistSq : distSq > bestDistSq;
            if (better)
            {
                bestDistSq = distSq;
                best = chara;
            }
        }
        return best;
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
