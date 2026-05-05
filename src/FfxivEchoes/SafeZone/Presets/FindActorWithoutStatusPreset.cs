using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 特定ステータスを持たないアクターを検索（SPEC.md §6.1）。
/// params: { status_id: ステータス ID, actor_filter: enemy/ally/any（既定 any）,
///           name_contains: 名前に含まれる文字列（任意） }
/// </summary>
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

        IBattleChara? found = null;
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
            var hasStatus = false;
            foreach (var status in chara.StatusList)
            {
                if (status?.StatusId == statusId)
                {
                    hasStatus = true;
                    break;
                }
            }
            if (!hasStatus)
            {
                found = chara;
                break;
            }
        }

        if (found is null)
        {
            return null;
        }
        var pos = new Vector3(found.Position.X, found.Position.Y, found.Position.Z);
        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    private static bool FilterAccepts(IBattleChara chara, string filter) => filter.ToLowerInvariant() switch
    {
        "enemy" => chara is IBattleNpc bn && bn.SubKind != (byte)Dalamud.Game.ClientState.Objects.SubKinds.BattleNpcSubKind.Pet,
        "ally" => chara is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter,
        _ => true,
    };
}
