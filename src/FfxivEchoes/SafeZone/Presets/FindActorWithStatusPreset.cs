using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 特定ステータスを持つアクターを検索（SPEC.md §6.1）。
/// params: { status_id: ステータス ID, actor_filter: enemy/ally/any（既定 any）,
///           offset_distance: 数値（位置をそこから少し離す）, offset_angle: 度 }
/// </summary>
public sealed class FindActorWithStatusPreset : ISafeZonePreset
{
    public string Method => "find_actor_with_status";

    private readonly IObjectTable _objectTable;

    public FindActorWithStatusPreset(IObjectTable objectTable)
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

        var found = FindActor(statusId.Value, filter);
        if (found is null)
        {
            return null;
        }

        var pos = new Vector3(found.Position.X, found.Position.Y, found.Position.Z);
        var offsetDistance = ParamHelper.GetFloat(calc.Params, "offset_distance") ?? 0f;
        if (offsetDistance > 0f)
        {
            var angle = ParamHelper.GetFloat(calc.Params, "offset_angle") ?? 0f;
            var rad = angle * System.MathF.PI / 180f;
            pos += new Vector3(
                System.MathF.Sin(rad) * offsetDistance,
                0,
                -System.MathF.Cos(rad) * offsetDistance);
        }

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    private IBattleChara? FindActor(int statusId, string filter)
    {
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
            foreach (var status in chara.StatusList)
            {
                if (status?.StatusId == statusId)
                {
                    return chara;
                }
            }
        }
        return null;
    }

    private static bool FilterAccepts(IBattleChara chara, string filter) => filter.ToLowerInvariant() switch
    {
        "enemy" => chara is IBattleNpc bn && bn.SubKind != (byte)Dalamud.Game.ClientState.Objects.SubKinds.BattleNpcSubKind.Pet,
        "ally" => chara is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter,
        _ => true,
    };
}
